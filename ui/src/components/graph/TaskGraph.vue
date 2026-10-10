<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { VueFlow, MarkerType, useVueFlow, type Edge, type Node } from '@vue-flow/core'
import { Background } from '@vue-flow/background'
import { Controls } from '@vue-flow/controls'
import { MiniMap } from '@vue-flow/minimap'
import type { TaskfileModel } from '@/lib/taskfile'
import { bestLayout, type Direction } from '@/lib/layout'
import TaskNode from './TaskNode.vue'

/** Read-only dependency graph of a runner file's tasks; clicking a task selects it. */
const props = defineProps<{ model: TaskfileModel; entry: string; reachable: Set<string>; selected: string | null }>()
const emit = defineEmits<{ select: [name: string | null] }>()

const W = 200
const H = 64
const flowId = `task-graph-${Math.random().toString(36).slice(2)}`
const flow = useVueFlow(flowId)
const fitView = () => { void flow.fitView({ padding: 0.2, maxZoom: 1.1 }) }
const host = ref<HTMLElement | null>(null)
const positions = ref<Record<string, { x: number; y: number }>>({})
const direction = ref<Direction>('LR')

function layout() {
  const tasks = props.model.tasks
  const result = bestLayout(
    tasks.map(t => ({ id: t.name, width: W, height: H })),
    tasks.flatMap(t => t.deps.map(d => ({ source: d, target: t.name }))),
    host.value?.clientWidth ?? 0, host.value?.clientHeight ?? 0,
  )
  positions.value = result.positions
  direction.value = result.direction
  // Vue Flow keeps its own position for nodes it already knows, so move them explicitly.
  for (const [id, position] of Object.entries(result.positions)) if (flow.findNode(id)) flow.updateNode(id, { position })
  requestAnimationFrame(fitView)
}
watch(() => props.model.tasks.map(t => `${t.name}<${t.deps.join(',')}`).join('\n'), layout, { immediate: true })

const nodes = computed<Node[]>(() => props.model.tasks.map(t => ({
  id: t.name, type: 'task', position: positions.value[t.name] ?? { x: 0, y: 0 },
  data: { task: t }, draggable: false, connectable: false, deletable: false,
})))

const edges = computed<Edge[]>(() => {
  const names = new Set(props.model.tasks.map(t => t.name))
  return props.model.tasks.flatMap(t => t.deps.filter(d => names.has(d)).map(d => ({
    id: `${d}→${t.name}`, source: d, target: t.name, type: 'smoothstep', markerEnd: MarkerType.ArrowClosed,
    selectable: false, deletable: false,
    style: props.reachable.has(t.name) ? undefined : { opacity: 0.4 },
  })))
})

// refit while the layout settles, until the user pans or zooms
let userMoved = false
function onMoveStart() { userMoved = true }
let observer: ResizeObserver | null = null
onMounted(() => {
  layout()
  observer = new ResizeObserver(() => { if (!userMoved) fitView() })
  if (host.value) observer.observe(host.value)
})
onBeforeUnmount(() => observer?.disconnect())
</script>

<template>
  <div ref="host" class="h-full">
    <VueFlow
      :id="flowId" :nodes="nodes" :edges="edges" :min-zoom="0.2" :max-zoom="1.75"
      :nodes-draggable="false" :nodes-connectable="false" :edges-updatable="false" :delete-key-code="null" class="h-full"
      @node-click="({ node }) => emit('select', node.id)" @pane-click="emit('select', null)" @move-start="onMoveStart"
    >
      <template #node-task="{ data }">
        <TaskNode
          :task="data.task" :selected="data.task.name === selected" :entry="data.task.name === entry"
          :reachable="reachable.has(data.task.name)" :direction="direction"
        />
      </template>
      <Background :gap="18" pattern-color="var(--ui-border)" />
      <Controls :show-interactive="false" position="bottom-left" />
      <MiniMap
        pannable zoomable position="bottom-right" :width="160" :height="100"
        node-color="var(--ui-primary)" mask-color="color-mix(in srgb, var(--ui-bg-muted) 70%, transparent)"
      />
    </VueFlow>
  </div>
</template>
