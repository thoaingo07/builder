<script setup lang="ts">
import type { KeyValue } from '@/lib/taskfile'

/** Read-only key/value rows (task vars, env, call vars). Computed (`sh:`) values are marked. */
defineProps<{ entries: KeyValue[] }>()
</script>

<template>
  <dl class="grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 font-mono text-xs">
    <template v-for="e in entries" :key="e.key">
      <dt class="text-muted">{{ e.key }}</dt>
      <dd class="flex min-w-0 items-center gap-1">
        <UIcon v-if="e.dynamic" name="i-lucide-terminal" class="size-3 shrink-0 text-info" title="computed value" />
        <span class="truncate" :title="e.value">{{ e.value || '""' }}</span>
      </dd>
    </template>
  </dl>
</template>
