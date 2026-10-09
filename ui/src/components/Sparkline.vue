<script setup lang="ts">
import { computed } from 'vue'

const props = withDefaults(defineProps<{ values: number[]; max?: number; height?: number; color?: string }>(), {
  max: 100, height: 36, color: 'var(--ui-primary)',
})

const W = 200
const points = computed(() => {
  const v = props.values
  if (v.length < 2) return ''
  const step = W / (v.length - 1)
  return v.map((y, i) => `${(i * step).toFixed(1)},${(props.height - (Math.min(y, props.max) / props.max) * (props.height - 2) - 1).toFixed(1)}`).join(' ')
})
const area = computed(() => points.value ? `0,${props.height} ${points.value} ${W},${props.height}` : '')
</script>

<template>
  <svg :viewBox="`0 0 ${W} ${height}`" preserveAspectRatio="none" class="block w-full" :style="{ height: `${height}px` }">
    <polygon v-if="area" :points="area" :fill="color" opacity="0.12" />
    <polyline v-if="points" :points="points" fill="none" :stroke="color" stroke-width="1.5" vector-effect="non-scaling-stroke" />
    <line v-else x1="0" :y1="height - 1" :x2="W" :y2="height - 1" stroke="var(--ui-border)" stroke-dasharray="3 3" />
  </svg>
</template>
