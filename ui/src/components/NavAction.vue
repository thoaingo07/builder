<script setup lang="ts">
import { useIsMobile } from '@/composables/useIsMobile'

/** Header action button: icon + label on desktop, icon-only (with tooltip/aria-label) on phones. */
defineOptions({ inheritAttrs: false })
defineProps<{ label: string; icon: string }>()
const mobile = useIsMobile()
</script>

<template>
  <UTooltip v-if="mobile" :text="label">
    <UButton v-bind="$attrs" :icon="icon" :aria-label="label" square />
  </UTooltip>
  <UButton v-else v-bind="$attrs" :icon="icon" :label="label" />
</template>
