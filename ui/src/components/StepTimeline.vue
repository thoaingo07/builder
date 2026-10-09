<script setup lang="ts">
import type { JobStepDto } from '@/api/types'
import { cssColor, duration, stepIcon } from '@/lib/format'

defineProps<{ steps: JobStepDto[]; now: number; selected: number | null }>()
const emit = defineEmits<{ select: [index: number | null] }>()
</script>

<template>
  <ol class="relative space-y-0.5">
    <li v-for="(s, i) in steps" :key="s.index" class="relative">
      <!-- connector line -->
      <span v-if="i < steps.length - 1" class="absolute top-6 bottom-[-6px] left-[11px] w-px bg-(--ui-border)" aria-hidden="true" />
      <button
        type="button"
        class="flex w-full items-start gap-2 rounded-md px-1 py-1 text-left transition hover:bg-elevated"
        :class="selected === s.index ? 'bg-elevated ring-1 ring-primary/40' : ''"
        :aria-pressed="selected === s.index"
        @click="emit('select', selected === s.index ? null : s.index)"
      >
        <UIcon
          :name="stepIcon(s.status).name" class="relative mt-0.5 size-4 shrink-0 rounded-full bg-default"
          :class="stepIcon(s.status).spin ? 'animate-spin' : ''" :style="{ color: cssColor(stepIcon(s.status).color) }"
        />
        <span class="min-w-0 flex-1">
          <span class="flex items-center gap-1.5">
            <span class="truncate font-mono text-xs" :class="s.status === 'Pending' ? 'text-muted' : 'text-highlighted'">{{ s.label }}</span>
            <UBadge v-if="s.kind === 'TaskCall'" label="task" size="sm" color="primary" variant="subtle" class="shrink-0" />
            <UBadge v-else-if="s.kind === 'Defer'" label="cleanup" size="sm" color="neutral" variant="subtle" class="shrink-0" />
          </span>
          <span v-if="s.vars && Object.keys(s.vars).length" class="mt-0.5 flex flex-wrap gap-1">
            <span v-for="(v, k) in s.vars" :key="k" class="rounded bg-elevated px-1 font-mono text-[10px] text-toned">{{ k }}={{ v }}</span>
          </span>
        </span>
        <span v-if="s.startedAt" class="shrink-0 text-[11px] text-muted tabular-nums">{{ duration(s.startedAt, s.finishedAt, now) }}</span>
      </button>
    </li>
  </ol>
</template>
