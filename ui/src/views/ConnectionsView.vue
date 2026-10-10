<script setup lang="ts">
import NavAction from '@/components/NavAction.vue'
import { computed, onMounted, reactive, ref } from 'vue'
import type { FormError, TableColumn } from '@nuxt/ui'
import { api } from '@/api/client'
import type { ConnectionAuthKind, ConnectionDto, ConnectionInput, ConnectionType } from '@/api/types'
import { upsert } from '@/lib/collections'
import { useOrgStore } from '@/stores/org'
import { useNotify } from '@/composables/useNotify'
import { useConfirm } from '@/composables/useConfirm'
import { useHighlight } from '@/composables/useHighlight'
import DataList from '@/components/DataList.vue'

const org = useOrgStore()
const notify = useNotify()
const toast = useToast()
const confirm = useConfirm()
const connections = ref<ConnectionDto[]>([])
const hl = useHighlight()
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
  { label: 'Azure DevOps', value: 'AzureDevOps', icon: 'i-lucide-cloud-cog' },
  { label: 'Git (GitHub, GitLab, any HTTPS remote)', value: 'Git', icon: 'i-lucide-git-fork' },
  { label: 'Azure (ACR, service principal)', value: 'Azure', icon: 'i-lucide-cloud' },
  { label: 'Container registry (Docker Hub, GHCR…)', value: 'Registry', icon: 'i-lucide-container' },
]
const authItems = [
  { label: 'Personal access token', value: 'Pat' },
  { label: 'Service principal (Entra ID)', value: 'ServicePrincipal' },
]
const isAzure = computed(() => s.type === 'AzureDevOps')
const isArm = computed(() => s.type === 'Azure')
const isRegistry = computed(() => s.type === 'Registry')
/** Azure (ARM) connections are always service principals; Git ones always token-based. */
const isSp = computed(() => isArm.value || (isAzure.value && s.authKind === 'ServicePrincipal'))
/** The stored secret only counts if it is the same kind we are editing. */
const hasStoredSecret = computed(() => !!editing.value?.hasToken && editing.value.authKind === (isSp.value ? 'ServicePrincipal' : 'Pat'))
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const typeLabel = (t: ConnectionType) => ({ AzureDevOps: 'Azure DevOps', Azure: 'Azure', Registry: 'Registry', Git: 'Git' }[t])
const typeIcon = (t: ConnectionType) => ({ AzureDevOps: 'i-lucide-cloud-cog', Azure: 'i-lucide-cloud', Registry: 'i-lucide-container', Git: 'i-lucide-git-fork' }[t])

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
  if (v.type === 'Registry') {
    if (!v.url.trim()) errors.push({ name: 'url', message: 'Registry host, e.g. docker.io' })
    if (!v.username.trim()) errors.push({ name: 'username', message: 'Required' })
    if (!v.token.trim() && !hasStoredSecret.value) errors.push({ name: 'token', message: 'Required' })
  } else if (v.type !== 'Azure') {
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
    username: s.type === 'Git' || s.type === 'Registry' ? (s.username.trim() || null) : null,
    token: s.token.trim() || null,
    authKind: sp ? 'ServicePrincipal' : 'Pat',
    tenantId: sp ? s.tenantId.trim() : null,
    clientId: sp ? s.clientId.trim() : null,
  }
  try {
    const saved = editing.value ? await api.connections.update(editing.value.id, input) : await api.connections.create(input)
    connections.value = upsert(connections.value, saved, false)
    void hl.flash(saved.id)
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
          <NavAction v-if="org.isAdmin" icon="i-lucide-plus" label="New connection" @click="openForm(null)" />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <UEmpty
        v-if="!loading && !connections.length" icon="i-lucide-cloud-cog" title="No connections yet"
        description="Connect an Azure DevOps organization with a personal access token, then add its repositories."
        :actions="org.isAdmin ? [{ label: 'New connection', icon: 'i-lucide-plus', onClick: () => openForm(null) }] : []"
      />
      <UCard v-else :ui="{ body: 'p-0 sm:p-0' }">
        <DataList :data="connections" :columns="columns" :loading="loading" :highlight-id="hl.id.value" :clickable="org.isAdmin" @select="openForm">
          <template #card="{ item: c }">
            <div class="flex items-start gap-2">
              <div class="min-w-0 flex-1 space-y-1">
                <div class="flex flex-wrap items-center gap-1.5">
                  <span class="font-medium text-highlighted">{{ c.name }}</span>
                  <UBadge :label="typeLabel(c.type)" color="neutral" variant="subtle" size="sm" :icon="typeIcon(c.type)" />
                  <UBadge v-if="c.authKind === 'ServicePrincipal'" label="Service principal" icon="i-lucide-shield-check" color="primary" variant="subtle" size="sm" />
                  <UBadge v-else-if="c.hasToken" :label="c.type === 'Registry' ? 'Token' : 'PAT'" icon="i-lucide-key-round" color="neutral" variant="subtle" size="sm" />
                </div>
                <div class="truncate font-mono text-xs text-muted" :title="c.url">{{ c.type === 'Azure' ? (c.clientId ?? 'Azure Resource Manager') : c.url }}</div>
              </div>
              <div class="flex shrink-0 items-center gap-1" @click.stop>
                <UButton icon="i-lucide-plug-zap" label="Test" size="xs" color="neutral" variant="outline" :loading="testing === c.id" @click="test(c)" />
                <UDropdownMenu
                  v-if="org.isAdmin" :content="{ align: 'end' }"
                  :items="[[{ label: 'Edit', icon: 'i-lucide-pencil', onSelect: () => openForm(c) }], [{ label: 'Delete', icon: 'i-lucide-trash-2', color: 'error', onSelect: () => remove(c) }]]"
                >
                  <UButton icon="i-lucide-ellipsis-vertical" size="xs" color="neutral" variant="ghost" aria-label="More actions" />
                </UDropdownMenu>
              </div>
            </div>
          </template>
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
              <UBadge v-else-if="row.original.hasToken" :label="row.original.type === 'Registry' ? 'Token' : 'PAT'" icon="i-lucide-key-round" color="neutral" variant="subtle" size="sm" />
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
        </DataList>
      </UCard>

      <UModal v-model:open="formOpen" :title="editing ? `Edit ${editing.name}` : 'New connection'" :ui="{ content: 'sm:max-w-xl' }">
        <template #body>
          <UForm id="conn-form" :state="s" :validate="validate" class="space-y-4" @submit="submit">
            <UFormField label="Type" name="type"><USelect v-model="s.type" :items="typeItems" class="w-full" /></UFormField>
            <UFormField label="Name" name="name" required><UInput v-model="s.name" class="w-full" :placeholder="isArm ? 'azure-prod' : isAzure ? 'contoso' : isRegistry ? 'docker-hub' : 'github'" /></UFormField>
            <UFormField
              v-if="!isArm" :label="isAzure ? 'Organization URL' : isRegistry ? 'Registry host' : 'Host URL'" name="url" required
              :help="isAzure ? 'Your Azure DevOps organization — it is normalized to https://dev.azure.com/<org>.' : isRegistry ? 'As used in image names: docker.io, ghcr.io, registry.gitlab.com…' : 'Base URL of the git host, e.g. https://github.com'"
            >
              <UInput v-model="s.url" class="w-full font-mono" :placeholder="isAzure ? 'https://dev.azure.com/your-org' : isRegistry ? 'docker.io' : 'https://github.com'" />
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
              <UFormField
                v-if="!isAzure" label="Username" name="username" :required="isRegistry"
                :help="isRegistry ? 'Docker Hub: your Docker ID. GHCR: your GitHub user name.' : 'Optional; some hosts need it together with the token.'"
              >
                <UInput v-model="s.username" class="w-full" autocomplete="off" />
              </UFormField>
              <UFormField
                :label="isAzure ? 'Personal access token' : isRegistry ? 'Access token' : 'Token / password'" name="token" :required="(isAzure || isRegistry) && !hasStoredSecret"
                :help="hasStoredSecret ? 'Stored ✓ — leave empty to keep it.' : undefined"
              >
                <UInput v-model="s.token" type="password" class="w-full" autocomplete="new-password" :placeholder="hasStoredSecret ? '••••••••  (unchanged)' : ''" />
              </UFormField>
              <UAlert v-if="isRegistry" color="neutral" variant="subtle" icon="i-lucide-info" title="Container registry">
                <template #description>
                  Used by <code>x-registries: [docker.io]</code> — the agent runs <code>docker login</code> for the job only and logs out afterwards.
                  Use an access token (Docker Hub: <em>Account settings → Personal access tokens</em>; GHCR: a PAT with <strong>write:packages</strong>), not your password.
                </template>
              </UAlert>
              <UAlert v-if="isAzure" color="neutral" variant="subtle" icon="i-lucide-info" title="PAT scopes">
                <template #description>
                  Create it under <em>User settings → Personal access tokens</em> with <strong>Code → Read</strong> and
                  <strong>Code → Status</strong> (build results on commits and pull requests).
                  Add <strong>Service hooks → Read &amp; write</strong> only if you use “Install in Azure DevOps” on a repository
                  (it is under <em>Show all scopes</em> in the PAT dialog). Builder never writes to your repositories.
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
