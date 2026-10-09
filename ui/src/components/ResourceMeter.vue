<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{ label: string; percent: number; detail?: string }>()
const color = computed(() => (props.percent >= 90 ? 'error' : props.percent >= 75 ? 'warning' : 'primary'))
</script>

<template>
  <div>
    <div class="mb-1 flex justify-between text-xs">
      <span class="font-medium text-muted">{{ label }}</span>
      <span class="font-semibold tabular-nums">{{ percent.toFixed(0) }}%</span>
    </div>
    <UProgress :model-value="percent" :max="100" size="sm" :color="color" />
    <div v-if="detail" class="mt-1 text-[11px] text-dimmed tabular-nums">{{ detail }}</div>
  </div>
</template>
