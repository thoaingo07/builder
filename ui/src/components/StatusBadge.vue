<script setup lang="ts">
import { computed } from 'vue'
import type { BuildStatus, DeploymentStatus, JobStatus } from '@/api/types'
import { humanize, statusColor, statusIcon } from '@/lib/format'

const props = withDefaults(defineProps<{
  status: BuildStatus | JobStatus | DeploymentStatus
  size?: 'sm' | 'md' | 'lg'
}>(), { size: 'md' })

const icon = computed(() => statusIcon(props.status))
</script>

<template>
  <UBadge
    :color="statusColor(status)" variant="subtle" :size="size" :label="humanize(status)"
    :icon="icon.name" :ui="{ leadingIcon: icon.spin ? 'animate-spin' : '' }"
  />
</template>
