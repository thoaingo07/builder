import type {
  AgentDto, AgentMetricsDto, BuildDetailDto, BuildStatus, BuildSummaryDto, CleanupInput, CleanupResultDto,
  ConnectionDto, ConnectionInput, ConnectionTestDto, DashboardDto, DeploymentDto, EnvironmentDto, EnvironmentInput,
  Guid, JobDto, LogLineDto, MapRunnersInput, MeDto, MemberDto, OrgCreatedDto, OrgDto, OrgRole, PipelineDto,
  PipelineInput, PipelineTriggersDto, ProjectDto, ProjectInput, PlanPreviewDto, RunInputsDto, HookSetupDto, ProblemDetails, RemoteRepositoryDto, RepositoryDto, RepositoryInput,
  RunnerFilesDto, SecretDto, SecretInput, TaskfileDto, UserDto,
} from './types'

export class ApiError extends Error {
  constructor(public readonly status: number, public readonly problem: ProblemDetails) {
    super(problem.detail ? `${problem.title ?? 'Request failed'}: ${problem.detail}` : (problem.title ?? `HTTP ${status}`))
  }
}

/** Called on any 401 from /api — the router installs a redirect to /login. */
let onUnauthorized: (() => void) | null = null
export function setUnauthorizedHandler(handler: () => void) { onUnauthorized = handler }

/** The current organization, sent as `X-Org` on org-scoped calls. Set by the org store. */
let currentOrgId: string | null = null
export function setCurrentOrg(id: string | null) { currentOrgId = id }

/** Called when the API says the X-Org organization is missing or not ours (400/404 about the organization). */
let onOrgInvalid: (() => void) | null = null
export function setOrgInvalidHandler(handler: () => void) { onOrgInvalid = handler }

type Query = Record<string, string | number | boolean | null | undefined>

function withQuery(path: string, query?: Query) {
  if (!query) return path
  const qs = new URLSearchParams()
  for (const [k, v] of Object.entries(query)) if (v !== undefined && v !== null && v !== '') qs.set(k, String(v))
  const s = qs.toString()
  return s ? `${path}?${s}` : path
}

/** Personal endpoints that must not carry X-Org. */
function isPersonal(method: string, path: string) {
  return path === '/api/me' || path === '/api/me/password' || (method === 'POST' && path === '/api/orgs')
}


async function request<T>(method: string, path: string, body?: unknown, query?: Query): Promise<T> {
  const headers: Record<string, string> = { 'X-CSRF': '1', Accept: 'application/json' }
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  const orgScoped = path.startsWith('/api/') && !isPersonal(method, path)
  if (orgScoped && currentOrgId) headers['X-Org'] = currentOrgId
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
    if (orgScoped && (problem.code === 'org_required' || problem.code === 'org_not_found'))
      onOrgInvalid?.()
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
  me: () => get<MeDto>('/api/me'),
  createOrg: (name: string) => post<OrgCreatedDto>('/api/orgs', { name }),
  changePassword: (currentPassword: string, newPassword: string) => post<void>('/api/me/password', { currentPassword, newPassword }),

  org: {
    rename: (name: string) => put<OrgDto>('/api/org', { name }),
    regenerateAgentToken: () => post<{ agentToken: string }>('/api/org/agent-token'),
    members: () => get<MemberDto[]>('/api/org/members'),
    addMember: (email: string, role: OrgRole) => post<MemberDto>('/api/org/members', { email, role }),
    setRole: (userId: Guid, role: OrgRole) => put<MemberDto>(`/api/org/members/${userId}`, { role }),
    removeMember: (userId: Guid) => del(`/api/org/members/${userId}`),
  },

  dashboard: (project?: Guid | null) => get<DashboardDto>('/api/dashboard', { project }),

  /** Projects group repositories/runners and own connections, environments and secrets. */
  projects: {
    list: () => get<ProjectDto[]>('/api/projects'),
    get: (id: Guid) => get<ProjectDto>(`/api/projects/${id}`),
    create: (input: ProjectInput) => post<ProjectDto>('/api/projects', input),
    update: (id: Guid, input: ProjectInput) => put<ProjectDto>(`/api/projects/${id}`, input),
    /** 409 unless the project is empty */
    remove: (id: Guid) => del(`/api/projects/${id}`),
  },

  /** Runners (one mapped runner file each); the API path stays /pipelines. */
  pipelines: {
    list: (project?: Guid | null) => get<PipelineDto[]>('/api/pipelines', { project }),
    get: (id: Guid) => get<PipelineDto>(`/api/pipelines/${id}`),
    update: (id: Guid, input: PipelineInput) => put<PipelineDto>(`/api/pipelines/${id}`, input),
    remove: (id: Guid) => del(`/api/pipelines/${id}`),
    taskfile: (id: Guid, branch?: string) => get<TaskfileDto>(`/api/pipelines/${id}/taskfile`, { branch }),
    plan: (content: string, entryTask?: string | null) =>
      post<PlanPreviewDto>('/api/pipelines/plan', { content, entryTask: entryTask || null }),
    triggers: (id: Guid) => get<PipelineTriggersDto>(`/api/pipelines/${id}/triggers`),
    refreshTriggers: (id: Guid) => post<PipelineTriggersDto>(`/api/pipelines/${id}/triggers/refresh`),
    inputs: (id: Guid, branch?: string | null, entryTask?: string | null) =>
      get<RunInputsDto>(`/api/pipelines/${id}/inputs`, { branch, entryTask }),
    run: (id: Guid, input: { branch?: string | null; entryTask?: string | null; variables?: Record<string, string> }) =>
      post<BuildSummaryDto>(`/api/pipelines/${id}/builds`, input),
  },

  repositories: {
    list: (project?: Guid | null) => get<RepositoryDto[]>('/api/repositories', { project }),
    get: (id: Guid) => get<RepositoryDto>(`/api/repositories/${id}`),
    create: (input: RepositoryInput) => post<RepositoryDto>('/api/repositories', input),
    update: (id: Guid, input: RepositoryInput) => put<RepositoryDto>(`/api/repositories/${id}`, input),
    remove: (id: Guid) => del(`/api/repositories/${id}`),
    branches: (id: Guid) => get<string[]>(`/api/repositories/${id}/branches`),
    runnerFiles: (id: Guid, branch?: string | null) => get<RunnerFilesDto>(`/api/repositories/${id}/runner-files`, { branch }),
    /** new webhook secret for manual setup (the old one stops working) */
    hook: (id: Guid, origin: string) => request<HookSetupDto>('POST', `/api/repositories/${id}/hook`, {}, { origin }),
    /** rotates the secret and creates the Azure DevOps service hooks */
    installHook: (id: Guid, origin: string) => request<HookSetupDto>('POST', `/api/repositories/${id}/hook/install`, {}, { origin }),
    mapRunners: (id: Guid, input: MapRunnersInput) => post<PipelineDto[]>(`/api/repositories/${id}/runners`, input),
  },

  secrets: {
    /** with a project: its own secrets plus the shared ones */
    list: (project?: Guid | null) => get<SecretDto[]>('/api/secrets', { project }),
    create: (input: SecretInput) => post<SecretDto>('/api/secrets', input),
    update: (id: Guid, input: SecretInput) => put<SecretDto>(`/api/secrets/${id}`, input),
    remove: (id: Guid) => del(`/api/secrets/${id}`),
  },

  builds: {
    list: (q: { pipelineId?: Guid | null; status?: BuildStatus | '' | null; take?: number; project?: Guid | null } = {}) =>
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
    /** with a project: its own environments plus the shared ones */
    list: (project?: Guid | null) => get<EnvironmentDto[]>('/api/environments', { project }),
    /** an online agent with the environment's labels tries SSH + docker / the Kubernetes API (up to ~90 s) */
    test: (id: Guid) => post<ConnectionTestDto>(`/api/environments/${id}/test`),
    create: (input: EnvironmentInput) => post<EnvironmentDto>('/api/environments', input),
    update: (id: Guid, input: EnvironmentInput) => put<EnvironmentDto>(`/api/environments/${id}`, input),
    remove: (id: Guid) => del(`/api/environments/${id}`),
  },

  deployments: {
    list: (q: { environmentId?: Guid | null; active?: boolean; project?: Guid | null } = {}) => get<DeploymentDto[]>('/api/deployments', q),
    destroy: (id: Guid) => post<DeploymentDto>(`/api/deployments/${id}/destroy`),
    /** the previous (kept) container goes live again; this one is stopped and kept */
    rollback: (id: Guid) => post<DeploymentDto>(`/api/deployments/${id}/rollback`),
  },

  connections: {
    /** with a project: its own connections plus the shared ones */
    list: (project?: Guid | null) => get<ConnectionDto[]>('/api/connections', { project }),
    create: (input: ConnectionInput) => post<ConnectionDto>('/api/connections', input),
    update: (id: Guid, input: ConnectionInput) => put<ConnectionDto>(`/api/connections/${id}`, input),
    remove: (id: Guid) => del(`/api/connections/${id}`),
    test: (id: Guid) => post<ConnectionTestDto>(`/api/connections/${id}/test`),
    projects: (id: Guid) => get<string[]>(`/api/connections/${id}/projects`),
    repositories: (id: Guid, project?: string | null) =>
      get<RemoteRepositoryDto[]>(`/api/connections/${id}/repositories`, { project }),
    branches: (id: Guid, url: string) => get<string[]>(`/api/connections/${id}/branches`, { url }),
  },

  cleanup: (input: CleanupInput) => post<CleanupResultDto>('/api/cleanup', input),
}
