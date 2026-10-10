<script setup lang="ts">
import { computed } from 'vue'
import type { BuildStatus, DeploymentStatus, JobStatus } from '@/api/types'
import { deploymentHint, humanize, statusColor, statusIcon } from '@/lib/format'

const props = withDefaults(defineProps<{
  status: BuildStatus | JobStatus | DeploymentStatus
  size?: 'sm' | 'md' | 'lg'
}>(), { size: 'md' })

const icon = computed(() => statusIcon(props.status))
const hint = computed(() => deploymentHint[props.status as DeploymentStatus])
</script>

<template>
  <UTooltip :text="hint" :disabled="!hint">
    <UBadge
      :color="statusColor(status)" variant="subtle" :size="size" :label="humanize(status)"
      :icon="icon.name" :ui="{ leadingIcon: icon.spin ? 'animate-spin' : '' }"
    />
  </UTooltip>
</template>
