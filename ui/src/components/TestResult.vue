<script setup lang="ts">
/** Inline outcome of a "Test" next to a connection/environment. */
defineProps<{ result: { ok: boolean; message: string } | null | undefined; running?: boolean }>()
</script>

<template>
  <div v-if="running" class="flex items-center gap-1 text-xs text-info">
    <UIcon name="i-lucide-loader-circle" class="size-3.5 shrink-0 animate-spin" /> Testing…
  </div>
  <UTooltip v-else-if="result" :text="result.message">
    <div class="flex min-w-0 items-center gap-1 text-xs" :class="result.ok ? 'text-success' : 'text-error'">
      <UIcon :name="result.ok ? 'i-lucide-circle-check' : 'i-lucide-circle-x'" class="size-3.5 shrink-0" />
      <span class="truncate">{{ result.ok ? 'Works' : 'Failed' }} · {{ result.message }}</span>
    </div>
  </UTooltip>
</template>
