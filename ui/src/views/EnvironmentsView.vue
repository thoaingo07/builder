<script setup lang="ts">
import { onMounted, ref } from 'vue'
import type { TableColumn } from '@nuxt/ui'
import { api } from '@/api/client'
import type { EnvironmentDto } from '@/api/types'
import { upsert } from '@/lib/collections'
import { useNotify } from '@/composables/useNotify'
import { useConfirm } from '@/composables/useConfirm'
import EnvironmentFormModal from '@/components/environments/EnvironmentFormModal.vue'

const notify = useNotify()
const confirm = useConfirm()
const envs = ref<EnvironmentDto[]>([])
const loading = ref(true)
const formOpen = ref(false)
const editing = ref<EnvironmentDto | null>(null)

const columns: TableColumn<EnvironmentDto>[] = [
  { accessorKey: 'name', header: 'Name' },
  { accessorKey: 'type', header: 'Target' },
  { id: 'policy', header: 'Policy' },
  { id: 'actions', header: '' },
]

async function load() {
  try { envs.value = await api.environments.list() } catch (e) { notify.error(e, 'Could not load environments') } finally { loading.value = false }
}
function create() { editing.value = null; formOpen.value = true }
function edit(e: EnvironmentDto) { editing.value = e; formOpen.value = true }
async function remove(e: EnvironmentDto) {
  if (!await confirm({ title: 'Delete environment', message: `Delete "${e.name}"? Pipelines that deploy to it will fail until it exists again. Running apps are not torn down.`, confirmLabel: 'Delete', danger: true })) return
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
          <UButton icon="i-lucide-rocket" label="Deployments" color="neutral" variant="outline" to="/deployments" />
          <UButton icon="i-lucide-plus" label="New environment" @click="create" />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <UEmpty
        v-if="!loading && !envs.length" icon="i-lucide-cloud" title="No environments"
        description="Add a VPS (SSH + Docker) or a Kubernetes/AKS cluster, then reference it from a task's x-deploy."
        :actions="[{ label: 'New environment', icon: 'i-lucide-plus', onClick: create }]"
      />
      <UCard v-else :ui="{ body: 'p-0 sm:p-0' }">
        <UTable :data="envs" :columns="columns" :loading="loading" :ui="{ tr: 'cursor-pointer' }" @select="(_e, row) => edit(row.original)">
          <template #name-cell="{ row }"><span class="font-mono font-medium text-highlighted">{{ row.original.name }}</span></template>
          <template #type-cell="{ row }">
            <div class="flex items-center gap-2">
              <UIcon :name="row.original.type === 'SshDocker' ? 'i-lucide-server' : 'i-lucide-ship-wheel'" class="text-muted" />
              <div>
                <div class="text-sm">{{ row.original.type === 'SshDocker' ? 'SSH + Docker' : 'Kubernetes' }}</div>
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
            <div class="flex justify-end gap-1" @click.stop>
              <UButton icon="i-lucide-pencil" size="xs" color="neutral" variant="ghost" aria-label="Edit" @click="edit(row.original)" />
              <UButton icon="i-lucide-trash-2" size="xs" color="error" variant="ghost" aria-label="Delete" @click="remove(row.original)" />
            </div>
          </template>
        </UTable>
      </UCard>
      <EnvironmentFormModal v-model:open="formOpen" :environment="editing" @saved="e => (envs = upsert(envs, e, false))" />
    </template>
  </UDashboardPanel>
</template>
