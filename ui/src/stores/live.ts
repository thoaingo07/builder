import { defineStore } from 'pinia'
import { ref, shallowRef } from 'vue'
import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import type { AgentDto, AgentMetricsDto, BuildSummaryDto, DeploymentDto, Guid, JobDto, LogLineDto } from '@/api/types'
import { api } from '@/api/client'

type Handler<T extends unknown[]> = (...args: T) => void

class Emitter<T extends unknown[]> {
  private handlers = new Set<Handler<T>>()
  on(h: Handler<T>) { this.handlers.add(h); return () => { this.handlers.delete(h) } }
  emit(...args: T) { for (const h of this.handlers) h(...args) }
}

const HISTORY = 180 // ~15 min at 5 s

/**
 * The single SignalR connection to /hubs/ui plus the live agent state shared by every view.
 * Views subscribe to build/job/log/deployment events through the `on*` helpers, which return an unsubscribe fn.
 */
export const useLiveStore = defineStore('live', () => {
  const state = ref<'disconnected' | 'connecting' | 'connected' | 'reconnecting'>('disconnected')
  const agents = ref<AgentDto[]>([])
  const history = ref<Record<Guid, AgentMetricsDto[]>>({})
  const connection = shallowRef<HubConnection | null>(null)
  const joined = new Map<Guid, number>()
  let orgId: Guid | null = null

  const buildUpdated = new Emitter<[BuildSummaryDto]>()
  const jobUpdated = new Emitter<[JobDto]>()
  const log = new Emitter<[Guid, LogLineDto[]]>()
  const deploymentUpdated = new Emitter<[DeploymentDto]>()

  function setAgents(list: AgentDto[]) {
    agents.value = list
    const h = { ...history.value }
    for (const a of list) {
      if (!a.metrics) continue
      const arr = h[a.id] ?? []
      if (arr.length === 0 || arr[arr.length - 1].at !== a.metrics.at) {
        h[a.id] = [...arr, a.metrics].slice(-HISTORY)
      }
    }
    history.value = h
  }

  async function loadHistory(agentId: Guid) {
    try {
      const samples = await api.agents.metrics(agentId)
      const current = history.value[agentId] ?? []
      const seen = new Set(samples.map(s => s.at))
      const merged = [...samples, ...current.filter(s => !seen.has(s.at))]
        .sort((a, b) => a.at.localeCompare(b.at)).slice(-HISTORY)
      history.value = { ...history.value, [agentId]: merged }
    } catch { /* history is best effort */ }
  }

  async function start() {
    if (connection.value) return
    const conn = new HubConnectionBuilder()
      .withUrl('/hubs/ui', { headers: { 'X-CSRF': '1' }, withCredentials: true })
      .withAutomaticReconnect([0, 1000, 2000, 5000, 10000, 15000, 30000])
      .configureLogging(LogLevel.Warning)
      .build()

    conn.on('AgentsUpdated', (list: AgentDto[]) => setAgents(list))
    conn.on('BuildUpdated', (b: BuildSummaryDto) => buildUpdated.emit(b))
    conn.on('JobUpdated', (j: JobDto) => jobUpdated.emit(j))
    conn.on('Log', (buildId: Guid, lines: LogLineDto[]) => log.emit(buildId, lines))
    conn.on('DeploymentUpdated', (d: DeploymentDto) => deploymentUpdated.emit(d))

    conn.onreconnecting(() => { state.value = 'reconnecting' })
    conn.onreconnected(async () => {
      state.value = 'connected'
      if (orgId) await conn.invoke('JoinOrg', orgId).catch(() => undefined)
      for (const id of joined.keys()) await conn.invoke('JoinBuild', id).catch(() => undefined)
    })
    conn.onclose(() => {
      state.value = 'disconnected'
      // automatic reconnect gave up — retry slowly while we are still the active connection
      if (connection.value === conn) window.setTimeout(() => void connect(conn), 10000)
    })

    connection.value = conn
    await connect(conn)
  }

  async function connect(conn: HubConnection) {
    if (connection.value !== conn || conn.state !== HubConnectionState.Disconnected) return
    state.value = 'connecting'
    try {
      await conn.start()
      state.value = 'connected'
      if (orgId) await conn.invoke('JoinOrg', orgId).catch(() => undefined)
      for (const id of joined.keys()) await conn.invoke('JoinBuild', id).catch(() => undefined)
    } catch {
      state.value = 'disconnected'
      window.setTimeout(() => void connect(conn), 5000)
    }
  }

  async function stop() {
    const conn = connection.value
    connection.value = null
    joined.clear()
    orgId = null
    if (conn) await conn.stop().catch(() => undefined)
    state.value = 'disconnected'
  }

  /** Switches the organization whose events this connection receives; clears org-scoped live state. */
  async function joinOrg(id: Guid | null) {
    if (id === orgId) return
    orgId = id
    agents.value = []
    history.value = {}
    joined.clear()
    const conn = connection.value
    if (id && conn?.state === HubConnectionState.Connected) await conn.invoke('JoinOrg', id).catch(() => undefined)
  }

  async function joinBuild(buildId: Guid) {
    joined.set(buildId, (joined.get(buildId) ?? 0) + 1)
    const conn = connection.value
    if (conn?.state === HubConnectionState.Connected) await conn.invoke('JoinBuild', buildId).catch(() => undefined)
  }

  async function leaveBuild(buildId: Guid) {
    const n = (joined.get(buildId) ?? 1) - 1
    if (n > 0) { joined.set(buildId, n); return }
    joined.delete(buildId)
    const conn = connection.value
    if (conn?.state === HubConnectionState.Connected) await conn.invoke('LeaveBuild', buildId).catch(() => undefined)
  }

  return {
    state, agents, history, setAgents, loadHistory, start, stop, joinOrg, joinBuild, leaveBuild,
    onBuild: (h: Handler<[BuildSummaryDto]>) => buildUpdated.on(h),
    onJob: (h: Handler<[JobDto]>) => jobUpdated.on(h),
    onLog: (h: Handler<[Guid, LogLineDto[]]>) => log.on(h),
    onDeployment: (h: Handler<[DeploymentDto]>) => deploymentUpdated.on(h),
  }
})
