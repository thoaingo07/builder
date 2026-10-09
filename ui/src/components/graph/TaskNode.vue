<script setup lang="ts">
import { Handle, Position } from '@vue-flow/core'
import type { TaskModel } from '@/lib/taskfile'

defineProps<{ task: TaskModel; selected: boolean; entry: boolean; reachable: boolean; direction: 'LR' | 'TB' }>()
</script>

<template>
  <div
    class="w-[200px] rounded-lg border bg-default px-3 py-2 shadow-sm transition"
    :class="[
      selected ? 'border-primary ring-2 ring-primary/40' : 'border-default',
      reachable ? '' : 'opacity-45',
    ]"
  >
    <Handle type="target" :position="direction === 'LR' ? Position.Left : Position.Top" />
    <div class="flex items-center gap-1.5">
      <UIcon v-if="entry" name="i-lucide-flag" class="size-3.5 shrink-0 text-primary" />
      <span class="truncate font-mono text-sm font-semibold text-highlighted">{{ task.name }}</span>
      <span class="flex-1" />
      <UIcon v-if="task.secrets.length" name="i-lucide-key-round" class="size-3.5 text-muted" />
      <UIcon v-if="task.approval" name="i-lucide-lock" class="size-3.5 text-warning" />
      <UIcon v-if="task.deploy" name="i-lucide-rocket" class="size-3.5 text-primary" />
      <UIcon v-if="task.artifacts.length" name="i-lucide-package" class="size-3.5 text-muted" />
    </div>
    <div class="mt-0.5 truncate text-[11px] text-muted">
      {{ task.desc || (task.cmds.length ? `${task.cmds.length} cmd${task.cmds.length > 1 ? 's' : ''}` : 'no commands') }}
    </div>
    <div v-if="task.labels.length" class="mt-1 flex flex-wrap gap-1">
      <span v-for="l in task.labels" :key="l" class="rounded bg-elevated px-1 font-mono text-[10px] text-toned">{{ l }}</span>
    </div>
    <Handle type="source" :position="direction === 'LR' ? Position.Right : Position.Bottom" />
  </div>
</template>
