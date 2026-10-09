import { Graph, layout } from '@dagrejs/dagre'

export interface LayoutNode { id: string; width: number; height: number }
export interface LayoutEdge { source: string; target: string }
export type Direction = 'LR' | 'TB'
export interface LayoutResult { positions: Record<string, { x: number; y: number }>; width: number; height: number; direction: Direction }

/** DAG layout; returns top-left positions keyed by node id plus the graph's bounding size. */
export function dagreLayout(nodes: LayoutNode[], edges: LayoutEdge[], rankdir: Direction = 'LR'): LayoutResult {
  const g = new Graph()
  g.setGraph({ rankdir, nodesep: 28, ranksep: rankdir === 'LR' ? 70 : 50, marginx: 10, marginy: 10 })
  g.setDefaultEdgeLabel(() => ({}))
  for (const n of nodes) g.setNode(n.id, { width: n.width, height: n.height })
  for (const e of edges) if (g.hasNode(e.source) && g.hasNode(e.target)) g.setEdge(e.source, e.target)
  layout(g)
  const positions: LayoutResult['positions'] = {}
  for (const n of nodes) {
    const p = g.node(n.id)
    positions[n.id] = { x: (p?.x ?? 0) - n.width / 2, y: (p?.y ?? 0) - n.height / 2 }
  }
  const graph = g.graph()
  return { positions, width: graph.width ?? 0, height: graph.height ?? 0, direction: rankdir }
}

/**
 * Lays the graph out left-to-right and top-to-bottom and keeps whichever fits the viewport at the larger zoom,
 * so long linear pipelines don't shrink to unreadable size in a wide-but-short panel (and vice versa).
 */
export function bestLayout(nodes: LayoutNode[], edges: LayoutEdge[], viewWidth: number, viewHeight: number): LayoutResult {
  const lr = dagreLayout(nodes, edges, 'LR')
  if (!viewWidth || !viewHeight || nodes.length < 3) return lr
  const tb = dagreLayout(nodes, edges, 'TB')
  const zoom = (r: LayoutResult) => Math.min(viewWidth / Math.max(1, r.width), viewHeight / Math.max(1, r.height))
  return zoom(tb) > zoom(lr) * 1.15 ? tb : lr
}
