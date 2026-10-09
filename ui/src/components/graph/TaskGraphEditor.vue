<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { VueFlow, MarkerType, useVueFlow, type Connection, type Edge, type EdgeChange, type Node, type NodeDragEvent } from '@vue-flow/core'
import { Background } from '@vue-flow/background'
import { Controls } from '@vue-flow/controls'
import { MiniMap } from '@vue-flow/minimap'
import type { TaskfileModel } from '@/lib/taskfile'
import { bestLayout, type Direction } from '@/lib/layout'
import TaskNode from './TaskNode.vue'

const props = defineProps<{ model: TaskfileModel; entry: string; reachable: Set<string>; selected: string | null }>()
const emit = defineEmits<{
  select: [name: string | null]
  connect: [dep: string, task: string]
  disconnect: [dep: string, task: string]
}>()

const W = 200
const H = 64
const flowId = `task-graph-${Math.random().toString(36).slice(2)}`
const flow = useVueFlow(flowId)
const fitView = () => { void flow.fitView({ padding: 0.2, maxZoom: 1.1 }) }
const positions = ref<Record<string, { x: number; y: number }>>({})
const host = ref<HTMLElement | null>(null)
let fitPending = true

const direction = ref<Direction>('LR')

function layout(all: boolean) {
  const tasks = props.model.tasks
  const result = bestLayout(
    tasks.map(t => ({ id: t.name, width: W, height: H })),
    tasks.flatMap(t => t.deps.map(d => ({ source: d, target: t.name }))),
    host.value?.clientWidth ?? 0, host.value?.clientHeight ?? 0,
  )
  if (all) direction.value = result.direction
  const next: typeof positions.value = {}
  for (const t of tasks) next[t.name] = (!all && positions.value[t.name]) || result.positions[t.name]
  positions.value = next
  // Vue Flow keeps its own position for nodes it already knows, so move them explicitly.
  for (const [id, position] of Object.entries(next)) if (flow.findNode(id)) flow.updateNode(id, { position })
}

// Until the user arranges nodes by hand, every shape change re-runs the auto layout;
// afterwards new tasks get a position and dragged tasks keep theirs.
let userDragged = false
watch(() => props.model.tasks.map(t => t.name).join('\n'), (_now, before) => {
  const fresh = !before
  layout(fresh || !userDragged)
  if (fresh) fitPending = true
}, { immediate: true })

function autoLayout() {
  userDragged = false
  layout(true)
  setTimeout(fitView, 0)
}
defineExpose({ autoLayout })

const nodes = computed<Node[]>(() => props.model.tasks.map(t => ({
  id: t.name,
  type: 'task',
  position: positions.value[t.name] ?? { x: 0, y: 0 },
  data: { task: t },
  deletable: false,
})))

const edgeId = (dep: string, task: string) => `${dep}→${task}`
const edges = computed<Edge[]>(() => {
  const names = new Set(props.model.tasks.map(t => t.name))
  return props.model.tasks.flatMap(t => t.deps.filter(d => names.has(d)).map(d => ({
    id: edgeId(d, t.name),
    source: d,
    target: t.name,
    type: 'smoothstep',
    markerEnd: MarkerType.ArrowClosed,
    deletable: true,
    data: { dep: d, task: t.name },
    style: props.reachable.has(t.name) ? undefined : { opacity: 0.4 },
  })))
})

function onConnect(c: Connection) {
  if (c.source && c.target && c.source !== c.target) emit('connect', c.source, c.target)
}

function onEdgesChange(changes: EdgeChange[]) {
  for (const ch of changes) {
    if (ch.type !== 'remove') continue
    const [dep, task] = ch.id.split('→')
    if (dep && task) emit('disconnect', dep, task)
  }
}

function onDragStop(e: NodeDragEvent) {
  userDragged = true
  const next = { ...positions.value }
  for (const n of e.nodes) next[n.id] = { ...n.position }
  positions.value = next
}

// Fit once nodes have been measured (first load / reload), not on every edit; the surrounding
// layout may still be settling, so also refit on resizes until the user pans/zooms.
let userMoved = false
function onNodesInitialized() {
  if (!fitPending) return
  fitPending = false
  userMoved = false
  requestAnimationFrame(() => requestAnimationFrame(fitView))
}
let observer: ResizeObserver | null = null
onMounted(() => {
  layout(true) // the viewport size is known now; pick the better direction
  observer = new ResizeObserver(() => { if (!userMoved) fitView() })
  if (host.value) observer.observe(host.value)
})
onBeforeUnmount(() => observer?.disconnect())
</script>

<template>
  <div ref="host" class="h-full">
  <VueFlow
    :id="flowId" :nodes="nodes" :edges="edges" :min-zoom="0.2" :max-zoom="1.75"
    :delete-key-code="['Delete', 'Backspace']" :connection-radius="30" class="h-full"
    @nodes-initialized="onNodesInitialized" @connect="onConnect" @edges-change="onEdgesChange" @node-drag-stop="onDragStop"
    @node-click="({ node }) => emit('select', node.id)" @pane-click="emit('select', null)" @move-start="userMoved = true"
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
