<script setup lang="ts">
import { computed } from 'vue'
import type { JobCounts } from '@/api/types'

const props = defineProps<{ counts: JobCounts }>()

// UProgress has a single value, so the multi-status bar is a stacked flexbox in Nuxt UI colors.
const parts = computed(() => {
  const c = props.counts
  const t = Math.max(1, c.total)
  return [
    { cls: 'bg-success', w: (c.succeeded / t) * 100, label: `${c.succeeded} succeeded` },
    { cls: 'bg-error', w: (c.failed / t) * 100, label: `${c.failed} failed` },
    { cls: 'bg-info', w: (c.running / t) * 100, label: `${c.running} running` },
    { cls: 'bg-warning', w: (c.waitingApproval / t) * 100, label: `${c.waitingApproval} waiting approval` },
    { cls: 'bg-accented', w: ((c.skipped + c.canceled) / t) * 100, label: `${c.skipped + c.canceled} skipped/canceled` },
  ].filter(p => p.w > 0)
})
const done = computed(() => props.counts.succeeded + props.counts.failed + props.counts.skipped + props.counts.canceled)
</script>

<template>
  <UTooltip :text="parts.map(p => p.label).join(' · ') || 'No jobs yet'">
    <div class="flex min-w-28 items-center gap-2">
      <div class="flex h-1.5 flex-1 overflow-hidden rounded-full bg-elevated">
        <span v-for="p in parts" :key="p.cls" :class="p.cls" class="h-full transition-all duration-500" :style="{ width: `${p.w}%` }" />
      </div>
      <span class="text-xs whitespace-nowrap text-muted tabular-nums">{{ done }}/{{ counts.total }}</span>
    </div>
  </UTooltip>
</template>
