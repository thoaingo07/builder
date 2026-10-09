# API surface (UI ⇄ BFF ⇄ API)

The browser only talks to the **BFF** (same origin). Everything under `/api/*` and
`/hubs/ui` is proxied to the API. JSON is camelCase, enums are strings, dates are
ISO-8601. Every `/api` request from the browser must send header `X-CSRF: 1`.
Errors: `{ "title": string, "detail"?: string, "status": number }` (ProblemDetails).

## BFF

| Method | Path | Body / result |
|---|---|---|
| POST | `/bff/login` | `{ userName, password }` → `UserDto` (sets cookie) · 401 |
| POST | `/bff/logout` | — |
| GET | `/bff/user` | `UserDto` · 401 when signed out |

## Types

```ts
type UserDto = { userName: string; displayName: string; isAdmin: boolean }

type BuildStatus = 'Planning'|'Running'|'Canceling'|'Succeeded'|'Failed'|'Canceled'
type JobStatus = 'Pending'|'Queued'|'WaitingApproval'|'Assigned'|'Running'|'Succeeded'|'Failed'|'Canceled'|'Skipped'

type PipelineDto = {
  id: string; name: string; connectionId: string|null; connectionName: string|null
  repositoryUrl: string; defaultBranch: string; taskfilePath: string; entryTask: string|null
  lastBuild: BuildSummaryDto|null
}
type PipelineInput = { name; connectionId?; repositoryUrl; defaultBranch; taskfilePath; entryTask? }

type JobCounts = { total; pending; running; waitingApproval; succeeded; failed; skipped; canceled }
type BuildSummaryDto = {
  id; pipelineId; pipelineName; number: number; branch; commit: string|null; entryTask
  status: BuildStatus; requestedBy; error: string|null
  queuedAt; startedAt: string|null; finishedAt: string|null; jobCounts: JobCounts
}
type ApprovalDto = { message: string; approvers: string[]; decidedBy: string|null; decidedAt: string|null; comment: string|null }
type DeploySpecDto = { environment; compose; project; manifests; namespace; url }
type JobDto = {
  id; buildId; key; taskName; description: string|null; order: number
  dependsOn: string[]   // keys of other jobs in the same build
  labels: string[]; artifacts: string[]; status: JobStatus
  agentId: string|null; agentName: string|null; exitCode: number|null; error: string|null
  startedAt: string|null; finishedAt: string|null
  approval: ApprovalDto|null; deploy: DeploySpecDto|null
}
type ArtifactDto = { id; jobId; name; sizeBytes: number; createdAt }
type BuildDetailDto = BuildSummaryDto & {
  variables: Record<string,string>; jobs: JobDto[]; artifacts: ArtifactDto[]; deployments: DeploymentDto[]
}
type LogLineDto = { id: number; jobId: string; timestamp: string; stream: 'Out'|'Err'|'System'; text: string }

type AgentMetricsDto = {
  at: string; cpuPercent: number; cpuCount: number
  memoryTotalBytes: number; memoryUsedBytes: number
  diskTotalBytes: number; diskUsedBytes: number
  loadAverage1: number; runningJobs: number
}
type AgentDto = {
  id; name; hostName; os; version; capacity: number; labels: string[]
  enabled: boolean; online: boolean; lastSeenAt: string|null
  metrics: AgentMetricsDto|null; runningJobs: { buildId; buildNumber; pipelineName; jobId; taskName }[]
}

type EnvironmentType = 'SshDocker'|'Kubernetes'
type EnvironmentDto = {
  id; name; type: EnvironmentType; requiresApproval: boolean; approvers: string[]; agentLabels: string[]
  host: string|null; port: number; username: string|null; hasPrivateKey: boolean
  hasKubeconfig: boolean; aksTenantId; aksClientId; hasAksClientSecret: boolean
  aksSubscriptionId; aksResourceGroup; aksClusterName; aksAdmin: boolean
}
// secrets are write-only: send them to set/replace, omit (null) to keep the stored value
type EnvironmentInput = Omit<EnvironmentDto,'id'|'hasPrivateKey'|'hasKubeconfig'|'hasAksClientSecret'>
  & { privateKey?: string|null; kubeconfig?: string|null; aksClientSecret?: string|null }

type DeploymentStatus = 'Deploying'|'Active'|'Failed'|'Destroying'|'Destroyed'
type DeploymentDto = {
  id; environmentId; environmentName; pipelineId; pipelineName; buildId; buildNumber: number; jobId
  name: string; url: string|null; status: DeploymentStatus; output: string|null; createdAt; updatedAt: string|null
}

type ConnectionType = 'AzureDevOps'|'Git'
type ConnectionDto = { id; name; type: ConnectionType; url; username: string|null; hasToken: boolean }
type ConnectionInput = { name; type; url; username?; token?: string|null }
type RepositoryDto = { project: string; name: string; url: string; defaultBranch: string|null }

type PlanPreviewDto = { entryTask: string; jobs: { key; taskName; dependsOn: string[]; approval: boolean; deploy: string|null }[]; error: string|null }
type TaskfileDto = { path: string; branch: string; commit: string; content: string }
type DashboardDto = {
  agents: AgentDto[]; activeBuilds: BuildSummaryDto[]; recentBuilds: BuildSummaryDto[]
  activeDeployments: DeploymentDto[]
  last24h: { succeeded: number; failed: number; canceled: number; running: number }
}
```

## Endpoints (prefix `/api`)

| Method | Path | Body → Result |
|---|---|---|
| GET | `/dashboard` | `DashboardDto` |
| GET | `/pipelines` | `PipelineDto[]` |
| GET | `/pipelines/{id}` | `PipelineDto` |
| POST | `/pipelines` | `PipelineInput` → `PipelineDto` |
| PUT | `/pipelines/{id}` | `PipelineInput` → `PipelineDto` |
| DELETE | `/pipelines/{id}` | 204 |
| GET | `/pipelines/{id}/taskfile?branch=` | `TaskfileDto` |
| PUT | `/pipelines/{id}/taskfile` | `{ branch, content, message }` → `TaskfileDto` (commits & pushes) |
| POST | `/pipelines/plan` | `{ content, entryTask? }` → `PlanPreviewDto` (validation, no git) |
| POST | `/pipelines/{id}/builds` | `{ branch?, entryTask?, variables? }` → `BuildSummaryDto` |
| GET | `/builds?pipelineId=&status=&take=50` | `BuildSummaryDto[]` (newest first) |
| GET | `/builds/{id}` | `BuildDetailDto` |
| POST | `/builds/{id}/cancel` | `BuildSummaryDto` |
| POST | `/builds/{id}/rerun` | `BuildSummaryDto` (new build, same commit inputs) |
| DELETE | `/builds/{id}` | 204 (finished builds only) |
| POST | `/builds/{id}/jobs/{jobId}/approval` | `{ approved: boolean, comment? }` → `JobDto` |
| GET | `/builds/{id}/jobs/{jobId}/logs?after=0` | `LogLineDto[]` (max 5000, `id > after`) |
| GET | `/artifacts/{id}` | file download (tar.gz) |
| GET | `/agents` | `AgentDto[]` |
| PUT | `/agents/{id}` | `{ enabled }` → `AgentDto` |
| DELETE | `/agents/{id}` | 204 (offline only) |
| GET | `/agents/{id}/metrics` | `AgentMetricsDto[]` (last ~15 min, 5 s resolution) |
| POST | `/agents/{id}/cleanup` | `{ removeWorkspaces, dockerPrune }` → 202 |
| GET/POST | `/environments` | `EnvironmentDto[]` / `EnvironmentInput` → `EnvironmentDto` |
| PUT/DELETE | `/environments/{id}` | `EnvironmentInput` → `EnvironmentDto` / 204 |
| GET | `/deployments?environmentId=&active=true` | `DeploymentDto[]` |
| POST | `/deployments/{id}/destroy` | `DeploymentDto` (teardown on an agent) |
| GET/POST | `/connections` | `ConnectionDto[]` / `ConnectionInput` → `ConnectionDto` |
| PUT/DELETE | `/connections/{id}` | `ConnectionInput` → `ConnectionDto` / 204 |
| GET | `/connections/{id}/repositories` | `RepositoryDto[]` (Azure DevOps only) |
| POST | `/cleanup` | `{ olderThanDays, keepLastPerPipeline, removeWorkspaces, dockerPrune }` → `{ buildsDeleted, artifactsDeleted, bytesFreed, agentsNotified }` |

## Live updates — SignalR hub `/hubs/ui`

Client → server: `JoinBuild(buildId)`, `LeaveBuild(buildId)`.
Server → client (all connected clients unless noted):

| Event | Payload |
|---|---|
| `BuildUpdated` | `BuildSummaryDto` |
| `JobUpdated` | `JobDto` (only clients that joined the build) |
| `Log` | `(buildId: string, lines: LogLineDto[])` (only joined clients) |
| `AgentsUpdated` | `AgentDto[]` (every ~3 s) |
| `DeploymentUpdated` | `DeploymentDto` |
