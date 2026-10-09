<script setup lang="ts">
import { computed } from 'vue'
import { Handle, Position } from '@vue-flow/core'
import type { JobDto } from '@/api/types'
import { duration, humanize, statusCssColor, statusIcon, stepProgress } from '@/lib/format'

const props = defineProps<{ job: JobDto; selected: boolean; now: number; direction: 'LR' | 'TB' }>()

const color = computed(() => statusCssColor(props.job.status))
const icon = computed(() => statusIcon(props.job.status))
const steps = computed(() => stepProgress(props.job.steps))
const showProgress = computed(() => (props.job.status === 'Running' || props.job.status === 'Assigned') && steps.value.total > 0)
</script>

<template>
  <div
    class="job-node min-h-[58px] w-[210px] rounded-lg border bg-default px-3 py-2 shadow-sm transition-shadow"
    :class="selected ? 'ring-2 ring-primary' : ''"
    :style="{ borderColor: `color-mix(in srgb, ${color} 45%, var(--ui-border))`, borderLeftWidth: '4px', borderLeftColor: `${color}` }"
  >
    <Handle type="target" :position="direction === 'LR' ? Position.Left : Position.Top" :connectable="false" />
    <div class="flex items-center gap-1.5">
      <UIcon :name="icon.name" class="size-4 shrink-0" :class="icon.spin ? 'animate-spin' : ''" :style="{ color: `${color}` }" />
      <span class="truncate text-sm font-semibold text-highlighted">{{ job.taskName }}</span>
      <span class="flex-1" />
      <UIcon v-if="job.approval" name="i-lucide-lock" class="size-3.5 text-warning" />
      <UIcon v-if="job.deploy" name="i-lucide-rocket" class="size-3.5 text-primary" />
    </div>
    <div class="mt-0.5 flex items-center gap-1 text-[11px] text-muted">
      <span>{{ humanize(job.status) }}</span>
      <template v-if="job.startedAt">· <span class="tabular-nums">{{ duration(job.startedAt, job.finishedAt, now) }}</span></template>
      <span class="flex-1" />
      <span v-if="job.agentName" class="max-w-20 truncate font-mono">{{ job.agentName }}</span>
    </div>
    <div v-if="showProgress" class="mt-1 flex items-center gap-1.5" :title="steps.running?.label">
      <div class="h-1 flex-1 overflow-hidden rounded-full bg-elevated">
        <div class="h-full bg-info transition-all duration-500" :style="{ width: `${(steps.done / steps.total) * 100}%` }" />
      </div>
      <span class="text-[10px] text-muted tabular-nums">{{ steps.done }}/{{ steps.total }} steps</span>
    </div>
    <div v-else-if="job.status === 'Failed' && steps.failed" class="mt-0.5 truncate font-mono text-[10px] text-error" :title="steps.failed.label">
      ✗ {{ steps.failed.label }}
    </div>
    <Handle type="source" :position="direction === 'LR' ? Position.Right : Position.Bottom" :connectable="false" />
  </div>
</template>
