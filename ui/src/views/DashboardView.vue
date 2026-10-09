<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { api } from '@/api/client'
import type { BuildSummaryDto, DashboardDto, DeploymentDto } from '@/api/types'
import { isBuildActive, relativeTime } from '@/lib/format'
import { debounce, upsert } from '@/lib/collections'
import { useLiveStore } from '@/stores/live'
import { useNotify } from '@/composables/useNotify'
import { useNow } from '@/composables/useNow'
import AgentCard from '@/components/AgentCard.vue'
import BuildsTable from '@/components/BuildsTable.vue'
import StatusBadge from '@/components/StatusBadge.vue'

const live = useLiveStore()
const notify = useNotify()
const now = useNow(5000)

const loading = ref(true)
const stats = ref<DashboardDto['last24h']>({ succeeded: 0, failed: 0, canceled: 0, running: 0 })
const activeBuilds = ref<BuildSummaryDto[]>([])
const recentBuilds = ref<BuildSummaryDto[]>([])
const deployments = ref<DeploymentDto[]>([])

const onlineAgents = computed(() => live.agents.filter(a => a.online).length)
const busySlots = computed(() => live.agents.reduce((n, a) => n + a.runningJobs.length, 0))
const totalSlots = computed(() => live.agents.filter(a => a.online && a.enabled).reduce((n, a) => n + a.capacity, 0))

const tiles = computed(() => [
  { label: 'Running now', value: activeBuilds.value.length, icon: 'i-lucide-loader-circle', color: 'text-info' },
  { label: 'Succeeded · 24h', value: stats.value.succeeded, icon: 'i-lucide-circle-check', color: 'text-success' },
  { label: 'Failed · 24h', value: stats.value.failed, icon: 'i-lucide-circle-x', color: 'text-error' },
  { label: 'Agents online', value: `${onlineAgents.value}/${live.agents.length}`, icon: 'i-lucide-server', color: 'text-primary' },
  { label: 'Busy slots', value: `${busySlots.value}/${totalSlots.value}`, icon: 'i-lucide-layers', color: 'text-warning' },
])

async function load(initial = false) {
  try {
    const d = await api.dashboard()
    stats.value = d.last24h
    activeBuilds.value = d.activeBuilds
    recentBuilds.value = d.recentBuilds
    deployments.value = d.activeDeployments
    live.setAgents(d.agents)
    if (initial) for (const a of d.agents) void live.loadHistory(a.id)
  } catch (e) {
    notify.error(e, 'Could not load dashboard')
  } finally {
    loading.value = false
  }
}

const refreshStats = debounce(() => void load(), 3000)

const offs: (() => void)[] = []
onMounted(() => {
  void load(true)
  offs.push(live.onBuild(b => {
    if (isBuildActive(b.status)) activeBuilds.value = upsert(activeBuilds.value, b)
    else {
      activeBuilds.value = activeBuilds.value.filter(x => x.id !== b.id)
      recentBuilds.value = upsert(recentBuilds.value, b).slice(0, 15)
      refreshStats()
    }
  }))
  offs.push(live.onDeployment(d => {
    deployments.value = d.status === 'Destroyed'
      ? deployments.value.filter(x => x.id !== d.id)
      : upsert(deployments.value, d)
  }))
})
onBeforeUnmount(() => offs.forEach(f => f()))
</script>

<template>
  <UDashboardPanel id="dashboard">
    <template #header>
      <UDashboardNavbar title="Dashboard" icon="i-lucide-layout-dashboard">
        <template #right>
          <UButton icon="i-lucide-refresh-cw" color="neutral" variant="ghost" aria-label="Refresh" @click="load()" />
          <UButton icon="i-lucide-play" label="Run pipeline" to="/pipelines" />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <div class="grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-5">
        <UCard v-for="t in tiles" :key="t.label" :ui="{ body: 'sm:p-4 p-3' }">
          <div class="flex items-center gap-3">
            <UIcon :name="t.icon" :class="t.color" class="size-6 shrink-0" />
            <div class="min-w-0">
              <div class="text-xl font-semibold text-highlighted tabular-nums">
                <USkeleton v-if="loading" class="h-6 w-10" />
                <template v-else>{{ t.value }}</template>
              </div>
              <div class="truncate text-xs text-muted">{{ t.label }}</div>
            </div>
          </div>
        </UCard>
      </div>

      <section class="space-y-3">
        <h2 class="flex items-center gap-2 font-semibold text-highlighted">
          <UIcon name="i-lucide-server" /> Agents
        </h2>
        <div v-if="loading" class="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
          <USkeleton v-for="i in 3" :key="i" class="h-56" />
        </div>
        <UEmpty
          v-else-if="!live.agents.length" icon="i-lucide-server-off" title="No agents yet"
          description="Start Builder.Agent on a machine with the agent token; it registers itself and shows up here."
        />
        <div v-else class="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
          <AgentCard v-for="a in live.agents" :key="a.id" :agent="a" />
        </div>
      </section>

      <section class="space-y-3">
        <h2 class="flex items-center gap-2 font-semibold text-highlighted">
          <UIcon name="i-lucide-loader-circle" /> Active builds
        </h2>
        <UCard :ui="{ body: 'p-0 sm:p-0' }">
          <BuildsTable :builds="activeBuilds" :loading="loading" compact empty="Nothing running" />
        </UCard>
      </section>

      <section v-if="deployments.length" class="space-y-3">
        <h2 class="flex items-center gap-2 font-semibold text-highlighted">
          <UIcon name="i-lucide-rocket" /> Active deployments
        </h2>
        <div class="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
          <UCard v-for="d in deployments" :key="d.id" :ui="{ body: 'sm:p-4 p-3' }">
            <div class="flex items-start gap-3">
              <UIcon name="i-lucide-rocket" class="mt-0.5 size-5 text-primary" />
              <div class="min-w-0 flex-1">
                <div class="truncate font-medium text-highlighted">{{ d.name }}</div>
                <div class="truncate text-xs text-muted">
                  {{ d.environmentName }} · <RouterLink :to="`/builds/${d.buildId}`" class="hover:text-primary">{{ d.pipelineName }} #{{ d.buildNumber }}</RouterLink>
                  · {{ relativeTime(d.updatedAt ?? d.createdAt, now) }}
                </div>
              </div>
              <StatusBadge :status="d.status" size="sm" />
            </div>
            <UButton
              v-if="d.url" :to="d.url" target="_blank" label="Open app" trailing-icon="i-lucide-external-link"
              size="xs" variant="soft" class="mt-3"
            />
          </UCard>
        </div>
      </section>

      <section class="space-y-3">
        <div class="flex items-center gap-2">
          <h2 class="flex flex-1 items-center gap-2 font-semibold text-highlighted">
            <UIcon name="i-lucide-history" /> Recent builds
          </h2>
          <UButton to="/builds" label="All builds" variant="link" trailing-icon="i-lucide-arrow-right" size="sm" />
        </div>
        <UCard :ui="{ body: 'p-0 sm:p-0' }">
          <BuildsTable :builds="recentBuilds" :loading="loading" />
        </UCard>
      </section>
    </template>
  </UDashboardPanel>
</template>
