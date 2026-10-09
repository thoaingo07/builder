<script setup lang="ts">
import { computed } from 'vue'
import type { AgentDto } from '@/api/types'
import { bytes, pct, relativeTime } from '@/lib/format'
import { useLiveStore } from '@/stores/live'
import ResourceMeter from './ResourceMeter.vue'
import Sparkline from './Sparkline.vue'

const props = defineProps<{ agent: AgentDto }>()
const live = useLiveStore()

const m = computed(() => props.agent.metrics)
const cpuHistory = computed(() => (live.history[props.agent.id] ?? []).map(s => s.cpuPercent))
const memPct = computed(() => (m.value ? pct(m.value.memoryUsedBytes, m.value.memoryTotalBytes) : 0))
const diskPct = computed(() => (m.value ? pct(m.value.diskUsedBytes, m.value.diskTotalBytes) : 0))
const slots = computed(() => `${props.agent.runningJobs.length}/${props.agent.capacity}`)
</script>

<template>
  <UCard :ui="{ body: 'space-y-3 sm:p-4', header: 'sm:px-4 py-3' }" :class="{ 'opacity-70': !agent.online }">
    <template #header>
      <div class="flex items-center gap-2">
        <UChip :color="agent.online ? 'success' : 'neutral'" standalone inset size="lg" />
        <div class="min-w-0 flex-1">
          <div class="truncate font-semibold text-highlighted">{{ agent.name }}</div>
          <div class="truncate text-xs text-muted">{{ agent.hostName || '—' }} · {{ agent.os || 'unknown os' }}</div>
        </div>
        <UTooltip v-if="agent.shared" text="Shared agent: serves every organization and is managed by the Builder operator.">
          <UBadge color="info" variant="subtle" size="sm" icon="i-lucide-globe" label="Shared" />
        </UTooltip>
        <UBadge v-if="!agent.enabled" color="warning" variant="subtle" size="sm" label="Disabled" />
        <UTooltip :text="`${agent.runningJobs.length} running of ${agent.capacity} slots`">
          <UBadge color="neutral" variant="outline" size="sm" icon="i-lucide-layers" :label="slots" />
        </UTooltip>
        <slot name="actions" />
      </div>
    </template>

    <template v-if="m && agent.online">
      <div>
        <ResourceMeter label="CPU" :percent="m.cpuPercent" :detail="`${m.cpuCount} cores · load ${m.loadAverage1.toFixed(2)}`" />
        <Sparkline :values="cpuHistory" :height="30" class="mt-1" />
      </div>
      <ResourceMeter label="Memory" :percent="memPct" :detail="`${bytes(m.memoryUsedBytes)} / ${bytes(m.memoryTotalBytes)}`" />
      <ResourceMeter label="Disk" :percent="diskPct" :detail="`${bytes(m.diskUsedBytes)} / ${bytes(m.diskTotalBytes)}`" />
    </template>
    <div v-else class="py-3 text-center text-sm text-dimmed">
      {{ agent.online ? 'Waiting for metrics…' : `Offline · last seen ${relativeTime(agent.lastSeenAt)}` }}
    </div>

    <div v-if="agent.labels.length" class="flex flex-wrap gap-1">
      <UBadge v-for="l in agent.labels" :key="l" :label="l" color="neutral" variant="soft" size="sm" class="font-mono" />
    </div>

    <div v-if="agent.runningJobs.length" class="space-y-1 border-t border-default pt-2">
      <RouterLink
        v-for="j in agent.runningJobs" :key="j.jobId" :to="`/builds/${j.buildId}`"
        class="flex items-center gap-2 text-xs hover:text-primary"
      >
        <UIcon name="i-lucide-loader-circle" class="size-3.5 animate-spin text-info" />
        <span class="truncate">{{ j.pipelineName }} #{{ j.buildNumber }}</span>
        <span class="ml-auto truncate font-mono text-muted">{{ j.taskName }}</span>
      </RouterLink>
    </div>
  </UCard>
</template>
