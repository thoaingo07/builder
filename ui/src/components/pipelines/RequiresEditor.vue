<script setup lang="ts">
import type { RequiredVar } from '@/lib/taskfile'

/** go-task `requires.vars`: inputs the Run dialog asks for, optionally limited to a list of values. */
const props = defineProps<{ modelValue: RequiredVar[] }>()
const emit = defineEmits<{ 'update:modelValue': [RequiredVar[]] }>()

function update(i: number, patch: Partial<RequiredVar>) {
  emit('update:modelValue', props.modelValue.map((e, j) => (j === i ? { ...e, ...patch } : e)))
}
function remove(i: number) { emit('update:modelValue', props.modelValue.filter((_, j) => j !== i)) }
function add() { emit('update:modelValue', [...props.modelValue, { name: '', enum: [] }]) }
</script>

<template>
  <div class="space-y-1.5">
    <div v-for="(r, i) in modelValue" :key="i" class="flex items-start gap-1">
      <UInput
        :model-value="r.name" size="sm" class="w-36 font-mono" placeholder="VERSION"
        @update:model-value="v => update(i, { name: String(v).toUpperCase().replace(/[^A-Z0-9_]/g, '_') })"
      />
      <UInputTags
        :model-value="r.enum" size="sm" class="min-w-0 flex-1 font-mono" placeholder="any value (or list allowed values)"
        @update:model-value="v => update(i, { enum: v as string[] })"
      />
      <UButton icon="i-lucide-x" size="xs" color="neutral" variant="ghost" aria-label="Remove input" @click="remove(i)" />
    </div>
    <UButton icon="i-lucide-plus" label="Add input" size="xs" color="neutral" variant="outline" @click="add" />
  </div>
</template>
