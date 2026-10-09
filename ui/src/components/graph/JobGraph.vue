<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { VueFlow, MarkerType, useVueFlow, type Edge, type Node } from '@vue-flow/core'
import { Background } from '@vue-flow/background'
import { Controls } from '@vue-flow/controls'
import type { JobDto } from '@/api/types'
import { bestLayout, type Direction } from '@/lib/layout'
import { useNow } from '@/composables/useNow'
import JobNode from './JobNode.vue'

const props = defineProps<{ jobs: JobDto[]; selectedId: string | null }>()
const emit = defineEmits<{ select: [jobId: string] }>()

const now = useNow()
const W = 210
const H = 58
const flowId = `job-graph-${Math.random().toString(36).slice(2)}`
const { fitView, findNode, updateNode } = useVueFlow(flowId)
const fit = () => { void fitView({ padding: 0.15, maxZoom: 1.1 }) }

// Layout only depends on the graph shape; status updates must not move nodes around.
const shapeKey = computed(() => props.jobs.map(j => `${j.key}<${j.dependsOn.join(',')}`).sort().join('|'))
const positions = ref<Record<string, { x: number; y: number }>>({})
const direction = ref<Direction>('LR')
const host = ref<HTMLElement | null>(null)
function relayout() {
  const byKey = new Map(props.jobs.map(j => [j.key, j]))
  const result = bestLayout(
    props.jobs.map(j => ({ id: j.id, width: W, height: H })),
    props.jobs.flatMap(j => j.dependsOn.map(k => byKey.get(k)).filter((d): d is JobDto => !!d).map(d => ({ source: d.id, target: j.id }))),
    host.value?.clientWidth ?? 0, host.value?.clientHeight ?? 0,
  )
  positions.value = result.positions
  direction.value = result.direction
  for (const [id, position] of Object.entries(result.positions)) if (findNode(id)) updateNode(id, { position })
  void nextTick(fit)
}
watch(shapeKey, relayout, { immediate: true })
onMounted(relayout)

const nodes = computed<Node[]>(() => props.jobs.map(j => ({
  id: j.id,
  type: 'job',
  position: positions.value[j.id] ?? { x: 0, y: 0 },
  data: { job: j },
  draggable: false,
  connectable: false,
})))

const edges = computed<Edge[]>(() => {
  const byKey = new Map(props.jobs.map(j => [j.key, j]))
  return props.jobs.flatMap(j => j.dependsOn.map(k => byKey.get(k)).filter((d): d is JobDto => !!d).map(d => {
    const active = d.status === 'Succeeded' && (j.status === 'Running' || j.status === 'Assigned')
    return {
      id: `${d.id}->${j.id}`,
      source: d.id,
      target: j.id,
      type: 'smoothstep',
      animated: active,
      markerEnd: MarkerType.ArrowClosed,
      style: d.status === 'Succeeded' ? { stroke: 'var(--ui-success)' } : d.status === 'Failed' ? { stroke: 'var(--ui-error)' } : undefined,
    }
  }))
})

</script>

<template>
  <div ref="host" class="h-full min-h-72 w-full">
    <VueFlow
      :id="flowId" :nodes="nodes" :edges="edges" :min-zoom="0.2" :max-zoom="1.75"
      :nodes-draggable="false" :nodes-connectable="false" :elements-selectable="false"
      @nodes-initialized="fit" @node-click="({ node }) => emit('select', node.id)"
    >
      <template #node-job="{ data }">
        <JobNode :job="data.job" :selected="data.job.id === selectedId" :now="now" :direction="direction" />
      </template>
      <Background :gap="18" pattern-color="var(--ui-border)" />
      <Controls :show-interactive="false" position="bottom-right" />
    </VueFlow>
  </div>
</template>
