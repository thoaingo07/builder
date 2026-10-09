<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import type { FormError, TableColumn } from '@nuxt/ui'
import { api } from '@/api/client'
import type { ConnectionAuthKind, ConnectionDto, ConnectionInput, ConnectionType } from '@/api/types'
import { upsert } from '@/lib/collections'
import { useOrgStore } from '@/stores/org'
import { useNotify } from '@/composables/useNotify'
import { useConfirm } from '@/composables/useConfirm'

const org = useOrgStore()
const notify = useNotify()
const toast = useToast()
const confirm = useConfirm()
const connections = ref<ConnectionDto[]>([])
const loading = ref(true)
const formOpen = ref(false)
const editing = ref<ConnectionDto | null>(null)
const saving = ref(false)
const testing = ref<string | null>(null)
const s = reactive({
  name: '', type: 'AzureDevOps' as ConnectionType, authKind: 'Pat' as ConnectionAuthKind,
  url: '', username: '', token: '', tenantId: '', clientId: '',
})

const ARM_URL = 'https://management.azure.com'
const typeItems = [
  { label: 'Azure DevOps', value: 'AzureDevOps', icon: 'i-simple-icons-azuredevops' },
  { label: 'Git (GitHub, GitLab, any HTTPS remote)', value: 'Git', icon: 'i-lucide-git-fork' },
  { label: 'Azure (container registries)', value: 'Azure', icon: 'i-lucide-cloud' },
]
const authItems = [
  { label: 'Personal access token', value: 'Pat' },
  { label: 'Service principal (Entra ID)', value: 'ServicePrincipal' },
]
const isAzure = computed(() => s.type === 'AzureDevOps')
const isArm = computed(() => s.type === 'Azure')
/** Azure (ARM) connections are always service principals; Git ones always token-based. */
const isSp = computed(() => isArm.value || (isAzure.value && s.authKind === 'ServicePrincipal'))
/** The stored secret only counts if it is the same kind we are editing. */
const hasStoredSecret = computed(() => !!editing.value?.hasToken && editing.value.authKind === (isSp.value ? 'ServicePrincipal' : 'Pat'))
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const typeLabel = (t: ConnectionType) => (t === 'AzureDevOps' ? 'Azure DevOps' : t === 'Azure' ? 'Azure' : 'Git')
const typeIcon = (t: ConnectionType) => (t === 'AzureDevOps' ? 'i-simple-icons-azuredevops' : t === 'Azure' ? 'i-lucide-cloud' : 'i-lucide-git-fork')

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
  if (!org.isAdmin) return
  editing.value = c
  Object.assign(s, {
    name: c?.name ?? '', type: c?.type ?? 'AzureDevOps', authKind: c?.authKind ?? 'Pat',
    url: c?.type === 'Azure' ? '' : (c?.url ?? ''), username: c?.username ?? '', token: '',
    tenantId: c?.tenantId ?? '', clientId: c?.clientId ?? '',
  })
  formOpen.value = true
}

function validate(v: typeof s): FormError[] {
  const errors: FormError[] = []
  if (!v.name.trim()) errors.push({ name: 'name', message: 'Required' })
  if (v.type !== 'Azure') {
    if (!v.url.trim()) errors.push({ name: 'url', message: 'Required' })
    else if (!/^https?:\/\//i.test(v.url.trim())) errors.push({ name: 'url', message: 'Must start with https://' })
  }
  if (isSp.value) {
    if (!GUID.test(v.tenantId.trim())) errors.push({ name: 'tenantId', message: 'Directory (tenant) ID — a GUID' })
    if (!GUID.test(v.clientId.trim())) errors.push({ name: 'clientId', message: 'Application (client) ID — a GUID' })
    if (!v.token.trim() && !hasStoredSecret.value) errors.push({ name: 'token', message: 'Client secret is required' })
  } else if (v.type === 'AzureDevOps' && !v.token.trim() && !hasStoredSecret.value) {
    errors.push({ name: 'token', message: 'A PAT is required for Azure DevOps' })
  }
  return errors
}

async function submit() {
  saving.value = true
  const sp = isSp.value
  const input: ConnectionInput = {
    name: s.name.trim(), type: s.type, url: isArm.value ? ARM_URL : s.url.trim(),
    username: s.type === 'Git' ? (s.username.trim() || null) : null,
    token: s.token.trim() || null,
    authKind: sp ? 'ServicePrincipal' : 'Pat',
    tenantId: sp ? s.tenantId.trim() : null,
    clientId: sp ? s.clientId.trim() : null,
  }
  try {
    const saved = editing.value ? await api.connections.update(editing.value.id, input) : await api.connections.create(input)
    connections.value = upsert(connections.value, saved, false)
    notify.success(editing.value ? 'Connection updated' : 'Connection created', editing.value ? undefined : 'Use “Test” to check the credentials.')
    formOpen.value = false
  } catch (e) { notify.error(e, 'Could not save connection') } finally { saving.value = false }
}

async function test(c: ConnectionDto) {
  testing.value = c.id
  try {
    const r = await api.connections.test(c.id)
    toast.add({
      title: r.ok ? `${c.name}: connection works` : `${c.name}: connection failed`, description: r.message,
      color: r.ok ? 'success' : 'error', icon: r.ok ? 'i-lucide-plug-zap' : 'i-lucide-unplug', duration: r.ok ? 5000 : 10000,
    })
  } catch (e) { notify.error(e, `${c.name}: test failed`) } finally { testing.value = null }
}

async function remove(c: ConnectionDto) {
  if (!await confirm({ title: 'Delete connection', message: `Delete "${c.name}"? Repositories using it can no longer be read or built.`, confirmLabel: 'Delete', danger: true })) return
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
      <UDashboardNavbar title="Connections" icon="i-lucide-plug">
        <template #right>
          <UButton v-if="org.isAdmin" icon="i-lucide-plus" label="New connection" @click="openForm(null)" />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <UEmpty
        v-if="!loading && !connections.length" icon="i-simple-icons-azuredevops" title="No connections yet"
        description="Connect an Azure DevOps organization with a personal access token, then add its repositories."
        :actions="org.isAdmin ? [{ label: 'New connection', icon: 'i-lucide-plus', onClick: () => openForm(null) }] : []"
      />
      <UCard v-else :ui="{ body: 'p-0 sm:p-0' }">
        <UTable :data="connections" :columns="columns" :loading="loading" :ui="{ tr: org.isAdmin ? 'cursor-pointer' : '' }" @select="(_e, row) => openForm(row.original)">
          <template #name-cell="{ row }"><span class="font-medium text-highlighted">{{ row.original.name }}</span></template>
          <template #type-cell="{ row }">
            <UBadge :label="typeLabel(row.original.type)" color="neutral" variant="subtle" size="sm" :icon="typeIcon(row.original.type)" />
          </template>
          <template #url-cell="{ row }"><span class="font-mono text-xs">{{ row.original.type === 'Azure' ? 'Azure Resource Manager' : row.original.url }}</span></template>
          <template #auth-cell="{ row }">
            <div class="flex items-center gap-2">
              <UBadge
                v-if="row.original.authKind === 'ServicePrincipal'" label="Service principal" icon="i-lucide-shield-check"
                color="primary" variant="subtle" size="sm"
              />
              <UBadge v-else-if="row.original.hasToken" label="PAT" icon="i-lucide-key-round" color="neutral" variant="subtle" size="sm" />
              <span v-if="row.original.authKind === 'ServicePrincipal'" class="truncate font-mono text-xs text-muted">{{ row.original.clientId }}</span>
              <span v-else class="text-xs" :class="row.original.hasToken ? 'text-muted' : 'text-dimmed'">
                {{ row.original.hasToken ? (row.original.username ?? '') : 'Anonymous' }}
              </span>
            </div>
          </template>
          <template #actions-cell="{ row }">
            <div class="flex justify-end gap-1" @click.stop>
              <UButton icon="i-lucide-plug-zap" label="Test" size="xs" color="neutral" variant="outline" :loading="testing === row.original.id" @click="test(row.original)" />
              <UButton v-if="org.isAdmin" icon="i-lucide-trash-2" size="xs" color="error" variant="ghost" aria-label="Delete" @click="remove(row.original)" />
            </div>
          </template>
        </UTable>
      </UCard>

      <UModal v-model:open="formOpen" :title="editing ? `Edit ${editing.name}` : 'New connection'" :ui="{ content: 'sm:max-w-xl' }">
        <template #body>
          <UForm id="conn-form" :state="s" :validate="validate" class="space-y-4" @submit="submit">
            <UFormField label="Type" name="type"><USelect v-model="s.type" :items="typeItems" class="w-full" /></UFormField>
            <UFormField label="Name" name="name" required><UInput v-model="s.name" class="w-full" :placeholder="isArm ? 'azure-prod' : isAzure ? 'contoso' : 'github'" /></UFormField>
            <UFormField
              v-if="!isArm" :label="isAzure ? 'Organization URL' : 'Host URL'" name="url" required
              :help="isAzure ? 'Your Azure DevOps organization — it is normalized to https://dev.azure.com/<org>.' : 'Base URL of the git host, e.g. https://github.com'"
            >
              <UInput v-model="s.url" class="w-full font-mono" :placeholder="isAzure ? 'https://dev.azure.com/your-org' : 'https://github.com'" />
            </UFormField>

            <UFormField v-if="isAzure" label="Authentication" name="authKind">
              <URadioGroup v-model="s.authKind" :items="authItems" orientation="horizontal" />
            </UFormField>

            <template v-if="isSp">
              <div class="grid gap-4 sm:grid-cols-2">
                <UFormField label="Tenant ID" name="tenantId" required help="Directory (tenant) ID">
                  <UInput v-model="s.tenantId" class="w-full font-mono" placeholder="00000000-0000-0000-0000-000000000000" autocomplete="off" />
                </UFormField>
                <UFormField label="Client (application) ID" name="clientId" required>
                  <UInput v-model="s.clientId" class="w-full font-mono" placeholder="00000000-0000-0000-0000-000000000000" autocomplete="off" />
                </UFormField>
              </div>
              <UFormField label="Client secret" name="token" :required="!hasStoredSecret" :help="hasStoredSecret ? 'Stored ✓ — leave empty to keep it.' : undefined">
                <UInput v-model="s.token" type="password" class="w-full" autocomplete="new-password" :placeholder="hasStoredSecret ? '••••••••  (unchanged)' : ''" />
              </UFormField>
              <UAlert color="neutral" variant="subtle" icon="i-lucide-shield-check" title="Service principal (Entra ID)">
                <template #description>
                  <p v-if="isAzure">Add the service principal to the Azure DevOps organization (<em>Organization settings → Users</em>) and give it access to the repositories (and feeds) it needs.</p>
                  <p v-else>Give the service principal the <strong>AcrPush</strong> role on the container registries it pushes to (<strong>AcrPull</strong> is enough to pull).</p>
                  <p class="mt-1">With a service principal Builder mints tokens per job that expire in ~1 h (git, Azure Artifacts) or ~3 h (ACR); nothing long-lived reaches your servers.</p>
                </template>
              </UAlert>
            </template>

            <template v-else>
              <UFormField v-if="!isAzure" label="Username" name="username" help="Optional; some hosts need it together with the token.">
                <UInput v-model="s.username" class="w-full" autocomplete="off" />
              </UFormField>
              <UFormField
                :label="isAzure ? 'Personal access token' : 'Token / password'" name="token" :required="isAzure && !hasStoredSecret"
                :help="hasStoredSecret ? 'Stored ✓ — leave empty to keep it.' : undefined"
              >
                <UInput v-model="s.token" type="password" class="w-full" autocomplete="new-password" :placeholder="hasStoredSecret ? '••••••••  (unchanged)' : ''" />
              </UFormField>
              <UAlert v-if="isAzure" color="neutral" variant="subtle" icon="i-lucide-info" title="PAT scopes">
                <template #description>
                  Create it under <em>User settings → Personal access tokens</em> with scope <strong>Code: Read</strong>.
                  Choose <strong>Code: Read &amp; write</strong> if you want to save Taskfile edits from the runner editor (Builder commits and pushes them).
                </template>
              </UAlert>
            </template>
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
