<script setup lang="ts">
import NavAction from '@/components/NavAction.vue'
import { onMounted, reactive, ref } from 'vue'
import type { TableColumn } from '@nuxt/ui'
import { api } from '@/api/client'
import type { EnvironmentDto } from '@/api/types'
import { upsert } from '@/lib/collections'
import { useNotify } from '@/composables/useNotify'
import { useConfirm } from '@/composables/useConfirm'
import EnvironmentFormModal from '@/components/environments/EnvironmentFormModal.vue'
import { useOrgStore } from '@/stores/org'
import { useHighlight } from '@/composables/useHighlight'
import DataList from '@/components/DataList.vue'
import ScopeBadge from '@/components/ScopeBadge.vue'
import TestResult from '@/components/TestResult.vue'
import { useProjectStore } from '@/stores/project'
import { useScopedSections } from '@/composables/useScopedSections'

const org = useOrgStore()
const notify = useNotify()
const confirm = useConfirm()
const envs = ref<EnvironmentDto[]>([])
const hl = useHighlight()
function saved(e: EnvironmentDto) { envs.value = upsert(envs.value, e, false); void hl.flash(e.id) }
const loading = ref(true)
const formOpen = ref(false)
const editing = ref<EnvironmentDto | null>(null)
const project = useProjectStore()
const { sections, overrides, defaultScope, showScope } = useScopedSections(envs)
const toast = useToast()
const testing = reactive(new Set<string>())
const results = reactive<Record<string, { ok: boolean; message: string }>>({})

/** An online agent with the environment's labels tries SSH + docker or the Kubernetes API (can take ~90 s). */
async function test(e: EnvironmentDto) {
  testing.add(e.id)
  try {
    const r = await api.environments.test(e.id)
    results[e.id] = r
    toast.add({
      title: r.ok ? `${e.name}: reachable` : `${e.name}: test failed`, description: r.message,
      color: r.ok ? 'success' : 'error', icon: r.ok ? 'i-lucide-circle-check' : 'i-lucide-circle-x', duration: r.ok ? 6000 : 12000,
    })
  } catch (err) {
    // 409: no online agent with the environment's labels can reach it
    results[e.id] = { ok: false, message: err instanceof Error ? err.message : 'Test failed' }
    notify.error(err, `${e.name}: test failed`)
  } finally {
    testing.delete(e.id)
  }
}

const columns: TableColumn<EnvironmentDto>[] = [
  { accessorKey: 'name', header: 'Name' },
  { accessorKey: 'type', header: 'Target' },
  { id: 'policy', header: 'Policy' },
  { id: 'actions', header: '' },
]

async function load() {
  try { envs.value = await api.environments.list(project.query) } catch (e) { notify.error(e, 'Could not load environments') } finally { loading.value = false }
}
function create() { editing.value = null; formOpen.value = true }
function edit(e: EnvironmentDto) { if (!org.isAdmin) return; editing.value = e; formOpen.value = true }
async function remove(e: EnvironmentDto) {
  if (!await confirm({ title: 'Delete environment', message: `Delete "${e.name}"? Runners that deploy to it will fail until it exists again. Running apps are not torn down.`, confirmLabel: 'Delete', danger: true })) return
  try {
    await api.environments.remove(e.id)
    envs.value = envs.value.filter(x => x.id !== e.id)
    notify.success(`Deleted ${e.name}`)
  } catch (err) { notify.error(err, 'Delete failed') }
}

function target(e: EnvironmentDto) {
  if (e.type === 'SshDocker') return `${e.username ?? '?'}@${e.host ?? '?'}${e.port && e.port !== 22 ? `:${e.port}` : ''}`
  if (e.aksClusterName) return `AKS ${e.aksResourceGroup}/${e.aksClusterName}`
  return e.hasKubeconfig ? 'kubeconfig' : 'not configured'
}

onMounted(load)
</script>

<template>
  <UDashboardPanel id="environments">
    <template #header>
      <UDashboardNavbar title="Environments" icon="i-lucide-cloud">
        <template #right>
          <NavAction icon="i-lucide-rocket" label="Deployments" color="neutral" variant="outline" to="/deployments" />
          <NavAction v-if="org.isAdmin" icon="i-lucide-plus" label="New environment" @click="create" />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <UEmpty
        v-if="!loading && !envs.length" icon="i-lucide-cloud" title="No environments"
        description="Add a server reached over SSH (with Docker already installed) or a Kubernetes/AKS cluster, then reference it from a task's x-deploy."
        :actions="org.isAdmin ? [{ label: 'New environment', icon: 'i-lucide-plus', onClick: create }] : []"
      />
      <template v-else>
      <section v-for="sec in sections" :key="sec.key" class="space-y-2">
        <div v-if="sec.title" class="flex flex-wrap items-baseline gap-x-2">
          <h2 class="font-semibold text-highlighted">{{ sec.title }}</h2>
          <span class="text-xs text-muted">{{ sec.hint }}</span>
        </div>
      <UCard :ui="{ body: 'p-0 sm:p-0' }">
        <DataList :empty="sec.key === 'project' ? 'No environments in this project yet' : sec.key === 'shared' ? 'No shared environments' : 'No environments'" :data="sec.items" :columns="columns" :loading="loading" :highlight-id="hl.id.value" :clickable="org.isAdmin" @select="edit">
          <template #card="{ item: e }">
            <div class="flex items-start gap-2">
              <UIcon :name="e.type === 'SshDocker' ? 'i-lucide-server' : 'i-lucide-ship-wheel'" class="mt-0.5 shrink-0 text-muted" />
              <div class="min-w-0 flex-1 space-y-1">
                <div class="flex flex-wrap items-center gap-1.5">
                  <span class="font-mono font-medium text-highlighted">{{ e.name }}</span>
                  <ScopeBadge v-if="showScope || overrides(e)" :project-id="e.projectId" :scope="showScope" :overrides="overrides(e)" />
                </div>
                <div class="truncate font-mono text-xs text-muted" :title="target(e)">{{ e.type === 'SshDocker' ? (e.hasPassword ? 'SSH (password)' : 'SSH (key)') : 'Kubernetes' }} · {{ target(e) }}</div>
                <TestResult :result="results[e.id]" :running="testing.has(e.id)" />
                <div v-if="e.requiresApproval || e.agentLabels.length" class="flex flex-wrap gap-1">
                  <UBadge v-if="e.requiresApproval" icon="i-lucide-lock" color="warning" variant="subtle" size="sm" label="Approval" />
                  <UBadge v-for="l in e.agentLabels" :key="l" :label="l" color="neutral" variant="outline" size="sm" class="font-mono" />
                </div>
              </div>
              <UButton
                v-if="org.isAdmin" icon="i-lucide-plug-zap" label="Test" size="xs" color="neutral" variant="outline" class="shrink-0"
                :loading="testing.has(e.id)" @click.stop="test(e)"
              />
              <UDropdownMenu
                v-if="org.isAdmin" :content="{ align: 'end' }"
                :items="[[{ label: 'Edit', icon: 'i-lucide-pencil', onSelect: () => edit(e) }], [{ label: 'Delete', icon: 'i-lucide-trash-2', color: 'error', onSelect: () => remove(e) }]]"
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
            <TestResult :result="results[row.original.id]" :running="testing.has(row.original.id)" class="max-w-64" />
          </template>
          <template #type-cell="{ row }">
            <div class="flex items-center gap-2">
              <UIcon :name="row.original.type === 'SshDocker' ? 'i-lucide-server' : 'i-lucide-ship-wheel'" class="text-muted" />
              <div>
                <div class="text-sm">{{ row.original.type === 'SshDocker' ? (row.original.hasPassword ? 'SSH (password)' : 'SSH (key)') : 'Kubernetes' }}</div>
                <div class="font-mono text-xs text-muted">{{ target(row.original) }}</div>
              </div>
            </div>
          </template>
          <template #policy-cell="{ row }">
            <div class="flex flex-wrap gap-1">
              <UBadge v-if="row.original.requiresApproval" icon="i-lucide-lock" color="warning" variant="subtle" size="sm"
                :label="row.original.approvers.length ? `Approval: ${row.original.approvers.join(', ')}` : 'Approval'" />
              <UBadge v-for="l in row.original.agentLabels" :key="l" :label="l" color="neutral" variant="outline" size="sm" class="font-mono" />
              <span v-if="!row.original.requiresApproval && !row.original.agentLabels.length" class="text-xs text-dimmed">—</span>
            </div>
          </template>
          <template #actions-cell="{ row }">
            <div v-if="org.isAdmin" class="flex justify-end gap-1" @click.stop>
              <UButton icon="i-lucide-plug-zap" label="Test" size="xs" color="neutral" variant="outline" :loading="testing.has(row.original.id)" @click="test(row.original)" />
              <UButton icon="i-lucide-pencil" size="xs" color="neutral" variant="ghost" aria-label="Edit" @click="edit(row.original)" />
              <UButton icon="i-lucide-trash-2" size="xs" color="error" variant="ghost" aria-label="Delete" @click="remove(row.original)" />
            </div>
          </template>
        </DataList>
      </UCard>
      </section>
      </template>
      <EnvironmentFormModal v-model:open="formOpen" :environment="editing" :default-scope="defaultScope" @saved="saved" />
    </template>
  </UDashboardPanel>
</template>
