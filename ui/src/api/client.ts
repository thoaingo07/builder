import type {
  AgentDto, AgentMetricsDto, BuildDetailDto, BuildStatus, BuildSummaryDto, CleanupInput, CleanupResultDto,
  ConnectionDto, ConnectionInput, DashboardDto, DeploymentDto, EnvironmentDto, EnvironmentInput, Guid, JobDto,
  LogLineDto, PipelineDto, PipelineInput, PlanPreviewDto, ProblemDetails, RepositoryDto, TaskfileDto, UserDto,
} from './types'

export class ApiError extends Error {
  constructor(public readonly status: number, public readonly problem: ProblemDetails) {
    super(problem.detail ? `${problem.title ?? 'Request failed'}: ${problem.detail}` : (problem.title ?? `HTTP ${status}`))
  }
}

/** Called on any 401 from /api — the router installs a redirect to /login. */
let onUnauthorized: (() => void) | null = null
export function setUnauthorizedHandler(handler: () => void) { onUnauthorized = handler }

type Query = Record<string, string | number | boolean | null | undefined>

function withQuery(path: string, query?: Query) {
  if (!query) return path
  const qs = new URLSearchParams()
  for (const [k, v] of Object.entries(query)) if (v !== undefined && v !== null && v !== '') qs.set(k, String(v))
  const s = qs.toString()
  return s ? `${path}?${s}` : path
}

async function request<T>(method: string, path: string, body?: unknown, query?: Query): Promise<T> {
  const headers: Record<string, string> = { 'X-CSRF': '1', Accept: 'application/json' }
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  const res = await fetch(withQuery(path, query), {
    method, headers, credentials: 'same-origin',
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (res.status === 401 && path.startsWith('/api')) onUnauthorized?.()
  if (!res.ok) {
    let problem: ProblemDetails = { title: res.statusText || `HTTP ${res.status}`, status: res.status }
    try {
      const text = await res.text()
      if (text) {
        try { problem = { ...problem, ...JSON.parse(text) } } catch { problem.detail = text.slice(0, 500) }
      }
    } catch { /* ignore */ }
    if (problem.errors && !problem.detail)
      problem.detail = Object.values(problem.errors).flat().join(' ')
    throw new ApiError(res.status, problem)
  }
  if (res.status === 204 || res.status === 202) return undefined as T
  const text = await res.text()
  return (text ? JSON.parse(text) : undefined) as T
}

const get = <T>(p: string, q?: Query) => request<T>('GET', p, undefined, q)
const post = <T>(p: string, b?: unknown) => request<T>('POST', p, b ?? {})
const put = <T>(p: string, b: unknown) => request<T>('PUT', p, b)
const del = (p: string) => request<void>('DELETE', p)

export const bff = {
  login: (userName: string, password: string) => post<UserDto>('/bff/login', { userName, password }),
  logout: () => post<void>('/bff/logout'),
  user: () => get<UserDto>('/bff/user'),
}

export const api = {
  dashboard: () => get<DashboardDto>('/api/dashboard'),

  pipelines: {
    list: () => get<PipelineDto[]>('/api/pipelines'),
    get: (id: Guid) => get<PipelineDto>(`/api/pipelines/${id}`),
    create: (input: PipelineInput) => post<PipelineDto>('/api/pipelines', input),
    update: (id: Guid, input: PipelineInput) => put<PipelineDto>(`/api/pipelines/${id}`, input),
    remove: (id: Guid) => del(`/api/pipelines/${id}`),
    taskfile: (id: Guid, branch?: string) => get<TaskfileDto>(`/api/pipelines/${id}/taskfile`, { branch }),
    saveTaskfile: (id: Guid, branch: string, content: string, message: string) =>
      put<TaskfileDto>(`/api/pipelines/${id}/taskfile`, { branch, content, message }),
    plan: (content: string, entryTask?: string | null) =>
      post<PlanPreviewDto>('/api/pipelines/plan', { content, entryTask: entryTask || null }),
    run: (id: Guid, input: { branch?: string | null; entryTask?: string | null; variables?: Record<string, string> }) =>
      post<BuildSummaryDto>(`/api/pipelines/${id}/builds`, input),
  },

  builds: {
    list: (q: { pipelineId?: Guid | null; status?: BuildStatus | '' | null; take?: number } = {}) =>
      get<BuildSummaryDto[]>('/api/builds', { take: 50, ...q }),
    get: (id: Guid) => get<BuildDetailDto>(`/api/builds/${id}`),
    cancel: (id: Guid) => post<BuildSummaryDto>(`/api/builds/${id}/cancel`),
    rerun: (id: Guid) => post<BuildSummaryDto>(`/api/builds/${id}/rerun`),
    remove: (id: Guid) => del(`/api/builds/${id}`),
    approve: (id: Guid, jobId: Guid, approved: boolean, comment?: string) =>
      post<JobDto>(`/api/builds/${id}/jobs/${jobId}/approval`, { approved, comment: comment || null }),
    logs: (id: Guid, jobId: Guid, after = 0) => get<LogLineDto[]>(`/api/builds/${id}/jobs/${jobId}/logs`, { after }),
  },

  artifactUrl: (id: Guid) => `/api/artifacts/${id}`,

  agents: {
    list: () => get<AgentDto[]>('/api/agents'),
    setEnabled: (id: Guid, enabled: boolean) => put<AgentDto>(`/api/agents/${id}`, { enabled }),
    remove: (id: Guid) => del(`/api/agents/${id}`),
    metrics: (id: Guid) => get<AgentMetricsDto[]>(`/api/agents/${id}/metrics`),
    cleanup: (id: Guid, removeWorkspaces: boolean, dockerPrune: boolean) =>
      post<void>(`/api/agents/${id}/cleanup`, { removeWorkspaces, dockerPrune }),
  },

  environments: {
    list: () => get<EnvironmentDto[]>('/api/environments'),
    create: (input: EnvironmentInput) => post<EnvironmentDto>('/api/environments', input),
    update: (id: Guid, input: EnvironmentInput) => put<EnvironmentDto>(`/api/environments/${id}`, input),
    remove: (id: Guid) => del(`/api/environments/${id}`),
  },

  deployments: {
    list: (q: { environmentId?: Guid | null; active?: boolean } = {}) => get<DeploymentDto[]>('/api/deployments', q),
    destroy: (id: Guid) => post<DeploymentDto>(`/api/deployments/${id}/destroy`),
  },

  connections: {
    list: () => get<ConnectionDto[]>('/api/connections'),
    create: (input: ConnectionInput) => post<ConnectionDto>('/api/connections', input),
    update: (id: Guid, input: ConnectionInput) => put<ConnectionDto>(`/api/connections/${id}`, input),
    remove: (id: Guid) => del(`/api/connections/${id}`),
    repositories: (id: Guid) => get<RepositoryDto[]>(`/api/connections/${id}/repositories`),
  },

  cleanup: (input: CleanupInput) => post<CleanupResultDto>('/api/cleanup', input),
}
