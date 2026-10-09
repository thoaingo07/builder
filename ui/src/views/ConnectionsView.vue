<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import type { FormError, TableColumn } from '@nuxt/ui'
import { api } from '@/api/client'
import type { ConnectionDto, ConnectionInput, ConnectionType } from '@/api/types'
import { upsert } from '@/lib/collections'
import { useNotify } from '@/composables/useNotify'
import { useConfirm } from '@/composables/useConfirm'

const notify = useNotify()
const confirm = useConfirm()
const connections = ref<ConnectionDto[]>([])
const loading = ref(true)
const formOpen = ref(false)
const editing = ref<ConnectionDto | null>(null)
const saving = ref(false)
const s = reactive({ name: '', type: 'AzureDevOps' as ConnectionType, url: '', username: '', token: '' })

const typeItems = [
  { label: 'Azure DevOps', value: 'AzureDevOps', icon: 'i-lucide-git-branch' },
  { label: 'Generic git (GitHub, GitLab, …)', value: 'Git', icon: 'i-lucide-git-fork' },
]
const columns: TableColumn<ConnectionDto>[] = [
  { accessorKey: 'name', header: 'Name' },
  { accessorKey: 'type', header: 'Type' },
  { accessorKey: 'url', header: 'URL' },
  { id: 'auth', header: 'Credentials' },
  { id: 'actions', header: '' },
]

async function load() {
  try { connections.value = await api.connections.list() } catch (e) { notify.error(e, 'Could not load connections') } finally { loading.value = false }
}

function openForm(c: ConnectionDto | null) {
  editing.value = c
  Object.assign(s, { name: c?.name ?? '', type: c?.type ?? 'AzureDevOps', url: c?.url ?? '', username: c?.username ?? '', token: '' })
  formOpen.value = true
}

function validate(v: typeof s): FormError[] {
  const errors: FormError[] = []
  if (!v.name.trim()) errors.push({ name: 'name', message: 'Required' })
  if (!v.url.trim()) errors.push({ name: 'url', message: 'Required' })
  if (!v.token.trim() && !editing.value?.hasToken) errors.push({ name: 'token', message: 'Required' })
  return errors
}

async function submit() {
  saving.value = true
  const input: ConnectionInput = { name: s.name.trim(), type: s.type, url: s.url.trim(), username: s.username.trim() || null, token: s.token.trim() || null }
  try {
    const saved = editing.value ? await api.connections.update(editing.value.id, input) : await api.connections.create(input)
    connections.value = upsert(connections.value, saved, false)
    notify.success(editing.value ? 'Connection updated' : 'Connection created')
    formOpen.value = false
  } catch (e) { notify.error(e, 'Could not save connection') } finally { saving.value = false }
}

async function remove(c: ConnectionDto) {
  if (!await confirm({ title: 'Delete connection', message: `Delete "${c.name}"? Pipelines using it will clone anonymously.`, confirmLabel: 'Delete', danger: true })) return
  try {
    await api.connections.remove(c.id)
    connections.value = connections.value.filter(x => x.id !== c.id)
    notify.success(`Deleted ${c.name}`)
  } catch (e) { notify.error(e, 'Delete failed') }
}

onMounted(load)
</script>

<template>
  <UDashboardPanel id="connections">
    <template #header>
      <UDashboardNavbar title="Connections" icon="i-lucide-git-branch">
        <template #right>
          <UButton icon="i-lucide-plus" label="New connection" @click="openForm(null)" />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <UEmpty
        v-if="!loading && !connections.length" icon="i-lucide-key-round" title="No connections"
        description="Add an Azure DevOps organization with a PAT (Code: Read & Write) to clone repositories and commit Taskfile edits."
        :actions="[{ label: 'New connection', icon: 'i-lucide-plus', onClick: () => openForm(null) }]"
      />
      <UCard v-else :ui="{ body: 'p-0 sm:p-0' }">
        <UTable :data="connections" :columns="columns" :loading="loading" :ui="{ tr: 'cursor-pointer' }" @select="(_e, row) => openForm(row.original)">
          <template #name-cell="{ row }"><span class="font-medium text-highlighted">{{ row.original.name }}</span></template>
          <template #type-cell="{ row }">
            <UBadge :label="row.original.type === 'AzureDevOps' ? 'Azure DevOps' : 'Git'" color="neutral" variant="subtle" size="sm" />
          </template>
          <template #url-cell="{ row }"><span class="font-mono text-xs">{{ row.original.url }}</span></template>
          <template #auth-cell="{ row }">
            <span class="text-xs" :class="row.original.hasToken ? 'text-success' : 'text-warning'">
              {{ row.original.hasToken ? `Token stored${row.original.username ? ` · ${row.original.username}` : ''}` : 'No token' }}
            </span>
          </template>
          <template #actions-cell="{ row }">
            <div class="flex justify-end" @click.stop>
              <UButton icon="i-lucide-trash-2" size="xs" color="error" variant="ghost" aria-label="Delete" @click="remove(row.original)" />
            </div>
          </template>
        </UTable>
      </UCard>

      <UModal v-model:open="formOpen" :title="editing ? `Edit ${editing.name}` : 'New connection'">
        <template #body>
          <UForm id="conn-form" :state="s" :validate="validate" class="space-y-4" @submit="submit">
            <UFormField label="Name" name="name" required><UInput v-model="s.name" class="w-full" placeholder="contoso" /></UFormField>
            <UFormField label="Type" name="type"><USelect v-model="s.type" :items="typeItems" class="w-full" /></UFormField>
            <UFormField
              label="URL" name="url" required
              :help="s.type === 'AzureDevOps' ? 'Organization URL, e.g. https://dev.azure.com/contoso' : 'Git host base URL, e.g. https://github.com'"
            >
              <UInput v-model="s.url" class="w-full font-mono" />
            </UFormField>
            <UFormField label="Username" name="username" help="Optional; any value works for Azure DevOps PATs.">
              <UInput v-model="s.username" class="w-full" autocomplete="off" />
            </UFormField>
            <UFormField
              :label="s.type === 'AzureDevOps' ? 'Personal access token' : 'Token / password'" name="token"
              :required="!editing?.hasToken" :help="editing?.hasToken ? 'Stored ✓ — leave empty to keep it' : undefined"
            >
              <UInput v-model="s.token" type="password" class="w-full" autocomplete="new-password" />
            </UFormField>
          </UForm>
        </template>
        <template #footer>
          <div class="flex w-full justify-end gap-2">
            <UButton color="neutral" variant="outline" label="Cancel" @click="formOpen = false" />
            <UButton type="submit" form="conn-form" :loading="saving" :label="editing ? 'Save' : 'Create'" />
          </div>
        </template>
      </UModal>
    </template>
  </UDashboardPanel>
</template>
