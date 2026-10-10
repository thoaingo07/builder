<script setup lang="ts">
import { computed } from 'vue'
import type { BuildReason } from '@/api/types'

const props = withDefaults(defineProps<{ reason: BuildReason | null | undefined; pullRequestId?: number | null; by?: string; size?: 'xs' | 'sm' | 'md' }>(), {
  pullRequestId: null, by: '', size: 'sm',
})

const info = computed(() => {
  switch (props.reason) {
    case 'Push': return { icon: 'i-lucide-git-commit-horizontal', label: 'Push', tip: 'Triggered by a push' }
    case 'PullRequest': return {
      icon: 'i-lucide-git-pull-request', label: props.pullRequestId ? `PR !${props.pullRequestId}` : 'PR',
      tip: props.pullRequestId ? `Pull request !${props.pullRequestId}` : 'Pull request',
    }
    case 'Schedule': return { icon: 'i-lucide-clock', label: 'Schedule', tip: 'Started by a schedule' }
    case 'Rerun': return { icon: 'i-lucide-rotate-ccw', label: 'Re-run', tip: props.by ? `Re-run by ${props.by}` : 'Re-run' }
    default: return { icon: 'i-lucide-user', label: 'Manual', tip: props.by ? `Started by ${props.by}` : 'Started manually' }
  }
})
</script>

<template>
  <UTooltip :text="info.tip">
    <UBadge :icon="info.icon" :label="info.label" :size="size" color="neutral" variant="outline" class="whitespace-nowrap" />
  </UTooltip>
</template>
