<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { basicSetup } from 'codemirror'
import { Compartment, EditorState } from '@codemirror/state'
import { EditorView, keymap } from '@codemirror/view'
import { indentWithTab } from '@codemirror/commands'
import { indentUnit } from '@codemirror/language'
import { yaml } from '@codemirror/lang-yaml'
import { oneDark } from '@codemirror/theme-one-dark'
import { useIsDark } from '@/composables/useIsDark'

const props = withDefaults(defineProps<{ modelValue: string; readonly?: boolean }>(), { readonly: false })
const emit = defineEmits<{ 'update:modelValue': [string] }>()

const host = ref<HTMLElement | null>(null)
const view = shallowRef<EditorView | null>(null)
const isDark = useIsDark()
const theme = new Compartment()
const readOnly = new Compartment()
const readOnlyExt = (r: boolean) => [EditorState.readOnly.of(r), EditorView.editable.of(!r)]

const lightTheme = EditorView.theme({
  '&': { backgroundColor: 'var(--ui-bg)', color: 'var(--ui-text)' },
  '.cm-gutters': { backgroundColor: 'var(--ui-bg-muted)', color: 'var(--ui-text-dimmed)', borderRight: '1px solid var(--ui-border)' },
  '.cm-activeLine, .cm-activeLineGutter': { backgroundColor: 'color-mix(in srgb, var(--ui-primary) 6%, transparent)' },
})

onMounted(() => {
  view.value = new EditorView({
    parent: host.value!,
    state: EditorState.create({
      doc: props.modelValue,
      extensions: [
        basicSetup,
        keymap.of([indentWithTab]),
        indentUnit.of('  '),
        EditorState.tabSize.of(2),
        yaml(),
        theme.of(isDark.value ? oneDark : lightTheme),
        readOnly.of(readOnlyExt(props.readonly)),
        EditorView.updateListener.of(u => {
          if (u.docChanged) emit('update:modelValue', u.state.doc.toString())
        }),
      ],
    }),
  })
})

onBeforeUnmount(() => view.value?.destroy())

// External changes (visual editor, reload) replace the document; our own edits round-trip unchanged.
watch(() => props.modelValue, v => {
  const ed = view.value
  if (!ed || v === ed.state.doc.toString()) return
  ed.dispatch({ changes: { from: 0, to: ed.state.doc.length, insert: v } })
})

watch(isDark, d => view.value?.dispatch({ effects: theme.reconfigure(d ? oneDark : lightTheme) }))
watch(() => props.readonly, r => view.value?.dispatch({ effects: readOnly.reconfigure(readOnlyExt(r)) }))

function goToLine(line: number) {
  const ed = view.value
  if (!ed || line < 1 || line > ed.state.doc.lines) return
  const pos = ed.state.doc.line(line).from
  ed.dispatch({ selection: { anchor: pos }, scrollIntoView: true })
  ed.focus()
}
defineExpose({ goToLine })
</script>

<template>
  <div ref="host" class="h-full min-h-0 overflow-hidden" />
</template>
