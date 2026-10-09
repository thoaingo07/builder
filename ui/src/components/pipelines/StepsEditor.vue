<script setup lang="ts">
import { ref, watch } from 'vue'
import type { Document } from 'yaml'
import * as tf from '@/lib/taskfile'
import { debounce } from '@/lib/collections'
import KeyValueEditor from './KeyValueEditor.vue'

/** Ordered `cmds` editor: commands, task calls with vars, and deferred cleanup. */
const props = defineProps<{ taskName: string; steps: tf.StepModel[]; otherTasks: string[] }>()
const emit = defineEmits<{ mutate: [fn: (doc: Document) => void] }>()

// local copy so typing isn't interrupted by the YAML round trip
const local = ref<tf.StepModel[]>([])
watch(() => props.steps, s => { local.value = s.map(x => ({ ...x, vars: x.vars.map(v => ({ ...v })) })) }, { immediate: true, deep: true })

const kindItems = [
  { label: 'Command', value: 'cmd', icon: 'i-lucide-terminal' },
  { label: 'Run task', value: 'task', icon: 'i-lucide-corner-down-right' },
  { label: 'Cleanup (defer)', value: 'defer', icon: 'i-lucide-undo-2' },
]
const taskItems = () => props.otherTasks.map(t => ({ label: t, value: t }))

const task = () => props.taskName
const pending = new Map<number, () => void>()
/** debounced write of one step (keyed by index, so typing in two steps doesn't drop an edit) */
function save(i: number) {
  let fn = pending.get(i)
  if (!fn) {
    fn = debounce(() => {
      const step = local.value[i]
      if (step && !step.readonly) emit('mutate', d => tf.setStep(d, task(), i, step))
    }, 350)
    pending.set(i, fn)
  }
  fn()
}
function saveNow(i: number) {
  const step = local.value[i]
  if (step && !step.readonly) emit('mutate', d => tf.setStep(d, task(), i, step))
}

function changeKind(i: number, kind: tf.StepKindModel) {
  const s = local.value[i]
  s.kind = kind
  if (kind === 'task' && !s.task) s.task = props.otherTasks[0] ?? ''
  if (kind !== 'task' && !s.cmd) s.cmd = kind === 'defer' ? 'echo cleanup' : 'echo "step"'
  saveNow(i)
}

function onKind(i: number, k: string) { changeKind(i, k as tf.StepKindModel) }

function move(i: number, delta: number) {
  pending.clear()
  emit('mutate', d => tf.moveStep(d, task(), i, i + delta))
}
function remove(i: number) {
  pending.clear()
  emit('mutate', d => tf.removeStep(d, task(), i))
}
function add(kind: tf.StepKindModel) {
  emit('mutate', d => tf.addStep(d, task(), kind, kind === 'task' ? (props.otherTasks[0] ?? '') : ''))
}
</script>

<template>
  <div class="space-y-2">
    <div
      v-for="(s, i) in local" :key="i"
      class="rounded-md border border-default bg-default p-2"
      :class="s.readonly ? 'border-dashed' : ''"
    >
      <div class="flex items-center gap-1">
        <span class="w-5 shrink-0 text-center text-xs text-muted tabular-nums">{{ i + 1 }}</span>
        <USelect
          v-if="!s.readonly" :model-value="s.kind" :items="kindItems" size="xs" class="w-40"
          @update:model-value="(k: string) => onKind(i, k)"
        />
        <UBadge v-else label="Edit in YAML" icon="i-lucide-file-code" color="warning" variant="subtle" size="sm" />
        <span class="flex-1" />
        <UButton icon="i-lucide-chevron-up" size="xs" color="neutral" variant="ghost" :disabled="i === 0" aria-label="Move up" @click="move(i, -1)" />
        <UButton icon="i-lucide-chevron-down" size="xs" color="neutral" variant="ghost" :disabled="i === local.length - 1" aria-label="Move down" @click="move(i, 1)" />
        <UButton icon="i-lucide-x" size="xs" color="neutral" variant="ghost" aria-label="Remove step" @click="remove(i)" />
      </div>

      <div class="mt-1.5 pl-6">
        <template v-if="s.readonly">
          <pre class="overflow-x-auto rounded bg-elevated p-1.5 font-mono text-[11px] whitespace-pre-wrap">{{ s.raw }}</pre>
          <p class="mt-1 text-[11px] text-muted">Uses keys the form doesn't edit (for:, platforms:, …); it is kept as written.</p>
        </template>

        <template v-else-if="s.kind === 'cmd'">
          <UTextarea v-model="s.cmd" :rows="1" autoresize class="w-full" :ui="{ base: 'font-mono text-xs' }" placeholder="shell command" @update:model-value="save(i)" />
          <div class="mt-1 flex gap-3">
            <UCheckbox v-model="s.silent" label="silent" size="xs" @update:model-value="saveNow(i)" />
            <UCheckbox v-model="s.ignoreError" label="ignore_error" size="xs" @update:model-value="saveNow(i)" />
          </div>
        </template>

        <template v-else-if="s.kind === 'task'">
          <USelect
            v-model="s.task" :items="taskItems()" size="sm" class="w-full font-mono" placeholder="Pick a task"
            icon="i-lucide-corner-down-right" @update:model-value="saveNow(i)"
          />
          <div class="mt-1.5 text-[11px] font-medium text-muted">Vars passed to the task</div>
          <KeyValueEditor v-model="s.vars" add-label="Add var" class="mt-1" @update:model-value="save(i)" />
          <div class="mt-1 flex gap-3">
            <UCheckbox v-model="s.silent" label="silent" size="xs" @update:model-value="saveNow(i)" />
            <UCheckbox v-model="s.ignoreError" label="ignore_error" size="xs" @update:model-value="saveNow(i)" />
          </div>
        </template>

        <template v-else>
          <UInput v-model="s.cmd" size="sm" class="w-full" :ui="{ base: 'font-mono text-xs' }" placeholder="runs when the task ends, even on failure" @update:model-value="save(i)" />
        </template>
      </div>
    </div>

    <div class="flex flex-wrap gap-1">
      <UButton icon="i-lucide-terminal" label="Command" size="xs" color="neutral" variant="outline" @click="add('cmd')" />
      <UButton icon="i-lucide-corner-down-right" label="Run task" size="xs" color="neutral" variant="outline" :disabled="!otherTasks.length" @click="add('task')" />
      <UButton icon="i-lucide-undo-2" label="Cleanup" size="xs" color="neutral" variant="outline" @click="add('defer')" />
    </div>
  </div>
</template>
