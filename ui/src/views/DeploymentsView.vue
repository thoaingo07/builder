<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { TableColumn } from '@nuxt/ui'
import { api } from '@/api/client'
import type { DeploymentDto, EnvironmentDto } from '@/api/types'
import { relativeTime } from '@/lib/format'
import { upsert } from '@/lib/collections'
import { useLiveStore } from '@/stores/live'
import { useNotify } from '@/composables/useNotify'
import { useNow } from '@/composables/useNow'
import { useBuildActions } from '@/composables/useBuildActions'
import StatusBadge from '@/components/StatusBadge.vue'

const live = useLiveStore()
const notify = useNotify()
const now = useNow(10000)
const actions = useBuildActions()

const ALL = 'all'
const deployments = ref<DeploymentDto[]>([])
const environments = ref<EnvironmentDto[]>([])
const loading = ref(true)
const environmentId = ref(ALL)
const activeOnly = ref(true)
const expanded = ref<string | null>(null)

const envItems = computed(() => [{ label: 'All environments', value: ALL }, ...environments.value.map(e => ({ label: e.name, value: e.id }))])

const columns: TableColumn<DeploymentDto>[] = [
  { accessorKey: 'status', header: 'Status' },
  { accessorKey: 'name', header: 'App' },
  { accessorKey: 'environmentName', header: 'Environment' },
  { id: 'build', header: 'Build' },
  { id: 'updated', header: 'Updated' },
  { id: 'actions', header: '' },
]

async function load() {
  loading.value = true
  try {
    deployments.value = await api.deployments.list({ environmentId: environmentId.value === ALL ? null : environmentId.value, active: activeOnly.value })
  } catch (e) { notify.error(e, 'Could not load deployments') } finally { loading.value = false }
}
watch([environmentId, activeOnly], load)

async function destroy(d: DeploymentDto) {
  const r = await actions.destroyDeployment(d)
  if (r) deployments.value = upsert(deployments.value, r)
}

let off: (() => void) | undefined
onMounted(() => {
  void load()
  void api.environments.list().then(r => { environments.value = r }).catch(() => undefined)
  off = live.onDeployment(d => {
    if (environmentId.value !== ALL && d.environmentId !== environmentId.value) return
    if (activeOnly.value && d.status === 'Destroyed') deployments.value = deployments.value.filter(x => x.id !== d.id)
    else deployments.value = upsert(deployments.value, d)
  })
})
onBeforeUnmount(() => off?.())
</script>

<template>
  <UDashboardPanel id="deployments">
    <template #header>
      <UDashboardNavbar title="Deployments" icon="i-lucide-rocket">
        <template #right>
          <UButton icon="i-lucide-refresh-cw" color="neutral" variant="ghost" aria-label="Refresh" :loading="loading" @click="load" />
        </template>
      </UDashboardNavbar>
      <UDashboardToolbar>
        <template #left>
          <USelect v-model="environmentId" :items="envItems" class="w-56" icon="i-lucide-cloud" />
          <USwitch v-model="activeOnly" label="Active only" />
        </template>
      </UDashboardToolbar>
    </template>

    <template #body>
      <UCard :ui="{ body: 'p-0 sm:p-0' }">
        <UTable
          :data="deployments" :columns="columns" :loading="loading" empty="No deployments"
          :ui="{ tr: 'cursor-pointer' }" @select="(_e, row) => (expanded = expanded === row.original.id ? null : row.original.id)"
        >
          <template #status-cell="{ row }"><StatusBadge :status="row.original.status" size="sm" /></template>
          <template #name-cell="{ row }">
            <div class="font-mono font-medium text-highlighted">{{ row.original.name }}</div>
            <a v-if="row.original.url" :href="row.original.url" target="_blank" rel="noopener" class="text-xs text-primary hover:underline" @click.stop>{{ row.original.url }}</a>
            <pre v-if="expanded === row.original.id && row.original.output" class="mt-2 max-w-xl overflow-x-auto rounded bg-elevated p-2 text-xs whitespace-pre-wrap">{{ row.original.output }}</pre>
          </template>
          <template #build-cell="{ row }">
            <RouterLink :to="`/builds/${row.original.buildId}`" class="text-sm hover:text-primary" @click.stop>
              {{ row.original.pipelineName }} #{{ row.original.buildNumber }}
            </RouterLink>
          </template>
          <template #updated-cell="{ row }"><span class="text-xs text-muted">{{ relativeTime(row.original.updatedAt ?? row.original.createdAt, now) }}</span></template>
          <template #actions-cell="{ row }">
            <div class="flex justify-end gap-1" @click.stop>
              <UButton
                v-if="row.original.url && row.original.status === 'Active'" :to="row.original.url" target="_blank"
                size="xs" variant="soft" label="Open app" trailing-icon="i-lucide-external-link"
              />
              <UButton
                v-if="row.original.status === 'Active' || row.original.status === 'Failed'" size="xs" color="error" variant="ghost"
                icon="i-lucide-trash-2" label="Destroy" :loading="actions.busy.value === `destroy:${row.original.id}`" @click="destroy(row.original)"
              />
            </div>
          </template>
        </UTable>
      </UCard>
    </template>
  </UDashboardPanel>
</template>
