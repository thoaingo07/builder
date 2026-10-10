<script setup lang="ts">
import NavAction from '@/components/NavAction.vue'
import { onMounted, reactive, ref } from 'vue'
import type { FormError, TableColumn } from '@nuxt/ui'
import { api } from '@/api/client'
import type { SecretDto } from '@/api/types'
import { dateTime } from '@/lib/format'
import { upsert } from '@/lib/collections'
import { useOrgStore } from '@/stores/org'
import { useNotify } from '@/composables/useNotify'
import { useConfirm } from '@/composables/useConfirm'
import { useHighlight } from '@/composables/useHighlight'
import DataList from '@/components/DataList.vue'
import ScopeBadge from '@/components/ScopeBadge.vue'
import ScopeSelect from '@/components/ScopeSelect.vue'
import { useProjectStore } from '@/stores/project'
import { useScopedSections } from '@/composables/useScopedSections'

const org = useOrgStore()
const notify = useNotify()
const confirm = useConfirm()

const NAME = /^[A-Z][A-Z0-9_]*$/
const secrets = ref<SecretDto[]>([])
const hl = useHighlight()
const project = useProjectStore()
const { sections, overrides, defaultScope, showScope } = useScopedSections(secrets)
const loading = ref(true)
const formOpen = ref(false)
const editing = ref<SecretDto | null>(null)
const saving = ref(false)
const s = reactive({ name: '', value: '', description: '', projectId: null as string | null })

const columns: TableColumn<SecretDto>[] = [
  { accessorKey: 'name', header: 'Name' },
  { accessorKey: 'description', header: 'Description' },
  { id: 'updated', header: 'Updated' },
  { id: 'actions', header: '' },
]

async function load() {
  try { secrets.value = await api.secrets.list(project.query) } catch (e) { notify.error(e, 'Could not load secrets') } finally { loading.value = false }
}

function openForm(x: SecretDto | null) {
  if (!org.isAdmin) return
  editing.value = x
  Object.assign(s, { name: x?.name ?? '', value: '', description: x?.description ?? '', projectId: x ? x.projectId : defaultScope.value })
  formOpen.value = true
}

function validate(v: typeof s): FormError[] {
  const errors: FormError[] = []
  if (!NAME.test(v.name)) errors.push({ name: 'name', message: 'Upper case letters, digits and _ ; must start with a letter' })
  // the same name may exist once per scope (a project secret overrides a shared one)
  else if (secrets.value.some(x => x.name === v.name && x.projectId === v.projectId && x.id !== editing.value?.id)) errors.push({ name: 'name', message: 'A secret with this name exists in this scope' })
  if (!editing.value && !v.value) errors.push({ name: 'value', message: 'Required' })
  return errors
}

async function submit() {
  saving.value = true
  // always send the scope: omitting projectId on update would make the secret shared
  const input = { name: s.name, value: s.value ? s.value : null, description: s.description.trim() || null, projectId: s.projectId }
  try {
    const saved = editing.value ? await api.secrets.update(editing.value.id, input) : await api.secrets.create(input)
    secrets.value = upsert(secrets.value, saved, false)
    void hl.flash(saved.id)
    notify.success(editing.value ? `Updated ${saved.name}` : `Created ${saved.name}`)
    formOpen.value = false
  } catch (e) { notify.error(e, 'Could not save secret') } finally { saving.value = false }
}

async function remove(x: SecretDto) {
  if (!await confirm({ title: 'Delete secret', message: `Delete ${x.name}? Runners that list it in x-secrets will fail until it exists again.`, confirmLabel: 'Delete', danger: true })) return
  try {
    await api.secrets.remove(x.id)
    secrets.value = secrets.value.filter(y => y.id !== x.id)
    notify.success(`Deleted ${x.name}`)
  } catch (e) { notify.error(e, 'Delete failed') }
}

onMounted(load)
</script>

<template>
  <UDashboardPanel id="secrets">
    <template #header>
      <UDashboardNavbar title="Secrets" icon="i-lucide-key-round">
        <template #right>
          <NavAction v-if="org.isAdmin" icon="i-lucide-plus" label="New secret" @click="openForm(null)" />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <UAlert color="neutral" variant="subtle" icon="i-lucide-info" title="Using secrets in a runner">
        <template #description>
          List the names a task needs with <code>x-secrets: [DB_PASSWORD]</code> in the runner file. When the job runs,
          the agent fetches the values and passes them to go-task both as task variables (<code v-pre>{{.DB_PASSWORD}}</code>)
          and as environment variables (<code>$DB_PASSWORD</code>). Values are masked in build logs and are never shown again after saving.
        </template>
      </UAlert>

      <UEmpty
        v-if="!loading && !secrets.length" icon="i-lucide-key-round" title="No secrets yet"
        :description="org.isAdmin ? 'Add credentials your runners need, such as registry passwords or API keys.' : 'An admin can add secrets for this organization.'"
        :actions="org.isAdmin ? [{ label: 'New secret', icon: 'i-lucide-plus', onClick: () => openForm(null) }] : []"
      />
      <template v-else>
      <section v-for="sec in sections" :key="sec.key" class="space-y-2">
        <div v-if="sec.title" class="flex flex-wrap items-baseline gap-x-2">
          <h2 class="font-semibold text-highlighted">{{ sec.title }}</h2>
          <span class="text-xs text-muted">{{ sec.hint }}</span>
        </div>
      <UCard :ui="{ body: 'p-0 sm:p-0' }">
        <DataList :empty="sec.key === 'project' ? 'No secrets in this project yet' : sec.key === 'shared' ? 'No shared secrets' : 'No secrets'" :data="sec.items" :columns="columns" :loading="loading" :highlight-id="hl.id.value" :clickable="org.isAdmin" @select="openForm">
          <template #card="{ item: x }">
            <div class="flex items-start gap-2">
              <div class="min-w-0 flex-1">
                <div class="truncate font-mono font-medium text-highlighted">{{ x.name }}</div>
                <ScopeBadge v-if="showScope || overrides(x)" :project-id="x.projectId" :scope="showScope" :overrides="overrides(x)" class="my-0.5" />
                <div v-if="x.description" class="truncate text-sm">{{ x.description }}</div>
                <div class="text-xs text-muted">{{ dateTime(x.updatedAt) }} · {{ x.updatedBy }}</div>
              </div>
              <UDropdownMenu
                v-if="org.isAdmin" :content="{ align: 'end' }"
                :items="[[{ label: 'Edit', icon: 'i-lucide-pencil', onSelect: () => openForm(x) }], [{ label: 'Delete', icon: 'i-lucide-trash-2', color: 'error', onSelect: () => remove(x) }]]"
              >
                <UButton icon="i-lucide-ellipsis-vertical" size="xs" color="neutral" variant="ghost" aria-label="More actions" @click.stop />
              </UDropdownMenu>
            </div>
          </template>
          <template #name-cell="{ row }">
            <div class="flex flex-wrap items-center gap-1.5">
              <span class="font-mono font-medium text-highlighted">{{ row.original.name }}</span>
              <ScopeBadge v-if="showScope || overrides(row.original)" :project-id="row.original.projectId" :scope="showScope" :overrides="overrides(row.original)" />
            </div>
          </template>
          <template #description-cell="{ row }"><span class="text-sm" :class="row.original.description ? '' : 'text-dimmed'">{{ row.original.description || '—' }}</span></template>
          <template #updated-cell="{ row }"><span class="text-xs text-muted">{{ dateTime(row.original.updatedAt) }} · {{ row.original.updatedBy }}</span></template>
          <template #actions-cell="{ row }">
            <div v-if="org.isAdmin" class="flex justify-end gap-1" @click.stop>
              <UButton icon="i-lucide-pencil" size="xs" color="neutral" variant="ghost" aria-label="Edit" @click="openForm(row.original)" />
              <UButton icon="i-lucide-trash-2" size="xs" color="error" variant="ghost" aria-label="Delete" @click="remove(row.original)" />
            </div>
          </template>
        </DataList>
      </UCard>
      </section>
      </template>

      <UModal v-model:open="formOpen" :title="editing ? `Edit ${editing.name}` : 'New secret'">
        <template #body>
          <UForm id="secret-form" :state="s" :validate="validate" class="space-y-4" @submit="submit">
            <UFormField label="Name" name="name" required help="Upper case, e.g. REGISTRY_PASSWORD">
              <UInput
                v-model="s.name" class="w-full font-mono" placeholder="REGISTRY_PASSWORD" autocomplete="off"
                @update:model-value="v => (s.name = String(v).toUpperCase().replace(/[^A-Z0-9_]/g, '_'))"
              />
            </UFormField>
            <UFormField
              label="Value" name="value" :required="!editing"
              :help="editing ? 'Stored ✓ — leave empty to keep the current value.' : 'Write-only: it cannot be viewed after saving.'"
            >
              <UTextarea v-model="s.value" :rows="3" autoresize class="w-full font-mono" autocomplete="off" spellcheck="false" :placeholder="editing ? '(unchanged)' : ''" />
            </UFormField>
            <ScopeSelect v-model="s.projectId" />
            <UFormField label="Description" name="description">
              <UInput v-model="s.description" class="w-full" placeholder="What it is for" />
            </UFormField>
          </UForm>
        </template>
        <template #footer>
          <div class="flex w-full justify-end gap-2">
            <UButton color="neutral" variant="outline" label="Cancel" @click="formOpen = false" />
            <UButton type="submit" form="secret-form" :loading="saving" :label="editing ? 'Save' : 'Create'" />
          </div>
        </template>
      </UModal>
    </template>
  </UDashboardPanel>
</template>
