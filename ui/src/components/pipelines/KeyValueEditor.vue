<script setup lang="ts">
import type { KeyValue } from '@/lib/taskfile'

/** Editable key/value rows; dynamic (`sh:`) entries are shown read-only but can be removed. */
const props = withDefaults(defineProps<{ modelValue: KeyValue[]; keyPlaceholder?: string; valuePlaceholder?: string; addLabel?: string }>(), {
  keyPlaceholder: 'NAME', valuePlaceholder: 'value', addLabel: 'Add',
})
const emit = defineEmits<{ 'update:modelValue': [KeyValue[]] }>()

function update(i: number, patch: Partial<KeyValue>) {
  emit('update:modelValue', props.modelValue.map((e, j) => (j === i ? { ...e, ...patch } : e)))
}
function remove(i: number) { emit('update:modelValue', props.modelValue.filter((_, j) => j !== i)) }
function add() { emit('update:modelValue', [...props.modelValue, { key: '', value: '' }]) }
</script>

<template>
  <div class="space-y-1.5">
    <div v-for="(e, i) in modelValue" :key="i" class="flex items-center gap-1">
      <UInput :model-value="e.key" size="sm" class="w-36 font-mono" :placeholder="keyPlaceholder" @update:model-value="v => update(i, { key: String(v) })" />
      <UTooltip v-if="e.dynamic" text="Computed value — edit it in the YAML tab">
        <UInput :model-value="e.value" size="sm" class="min-w-0 flex-1 font-mono" disabled icon="i-lucide-terminal" />
      </UTooltip>
      <UInput v-else :model-value="e.value" size="sm" class="min-w-0 flex-1 font-mono" :placeholder="valuePlaceholder" @update:model-value="v => update(i, { value: String(v) })" />
      <UButton icon="i-lucide-x" size="xs" color="neutral" variant="ghost" aria-label="Remove" @click="remove(i)" />
    </div>
    <UButton icon="i-lucide-plus" :label="addLabel" size="xs" color="neutral" variant="outline" @click="add" />
  </div>
</template>
