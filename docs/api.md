# API surface (UI ⇄ BFF ⇄ API)

The browser only talks to the **BFF** (same origin). Everything under `/api/*` and `/hubs/ui` is proxied to the
API. JSON is camelCase, enums are strings, dates are ISO-8601. Every non-GET `/api` and `/bff` request from the
browser must send header `X-CSRF: 1`. Errors: `{ "title": string, "detail"?: string, "status": number }`
(ProblemDetails). 401 = signed out, 403 = role too low, 404 = not found **or not in your organization**,
409 = business rule, 502 = Azure DevOps / git host problem (message is user-facing). A missing or foreign `X-Org`
returns a ProblemDetails with `code: "org_required"` (400) or `code: "org_not_found"` (404).

## Organizations

A user belongs to one or more organizations. The UI keeps the **current organization** and sends its id in the
**`X-Org` header on every `/api` call** except `/api/me` and `POST /api/orgs`. Without it org-scoped endpoints
return 400; with an organization the user is not a member of, 404. Roles: `Member` < `Admin` < `Owner`.

- Member: view everything, run/cancel/re-run builds, approve (if listed as approver). Runner files are changed in the repository, never from Builder.
- Admin: + connections, repositories & runner mapping, environments, secrets, agents, members (not owners), cleanup.
- Owner: + manage owners. An organization always keeps at least one owner.

## Projects

Inside an organization, **projects** group repositories (and so runners, builds, deployments). Connections,
environments and secrets belong to a project, or are **shared** (`projectId: null`) by the whole organization.
A name a runner asks for (`x-deploy.environment`, `x-secrets`, `x-registries`) resolves to the project's own
item first, then to the shared one. List endpoints take `?project=<id>`: repositories, runners, builds,
deployments and the dashboard show that project only; connections, environments and secrets show the project's
own plus the shared ones. Without `?project` everything in the organization is listed.

Inputs: `projectId` on `RepositoryInput` (needed once the organization has two or more projects; a PUT with
another project moves the repository with its runners, builds and deployments), and on `ConnectionInput`,
`EnvironmentInput`, `SecretInput` (null = shared; on update, send it every time — leaving it out makes the item
shared). Runner names are unique per project; environment and secret names per scope (a project, or shared).

## BFF

| Method | Path | Body / result |
|---|---|---|
| POST | `/bff/login` | `{ userName, password }` → `UserDto` (sets cookie) · 401 |
| POST | `/bff/logout` | — |
| GET | `/bff/user` | `UserDto` · 401 when signed out |
| GET | `/bff/providers` | `{ password: boolean, google: boolean }` |
| GET | `/bff/login/google?returnUrl=/x` | browser navigation → Google → back to `returnUrl`, or `/login?error=not_allowed|google_failed` |
| GET | `/runner-guide.md` | the runner guide (`docs/runner-guide.md`) as Markdown, **no sign-in** — for coding agents; `/guide` renders it |

## Types

```ts
type UserDto = { userName: string; displayName: string; isAdmin: boolean; email?: string|null }

type OrgRole = 'Member'|'Admin'|'Owner'
type OrgDto = { id; name; slug; role: OrgRole; memberCount: number; createdAt }
type MeDto = { user: UserDto; orgs: OrgDto[] }
type OrgCreatedDto = { org: OrgDto; agentToken: string }      // token shown once
type MemberDto = { userId; userName; displayName; email: string|null; role: OrgRole; canSignInWithGoogle: boolean; joinedAt }

type ConnectionType = 'AzureDevOps'|'Git'|'Azure'|'Registry'   // Azure = ARM / ACR (service principal); Registry = Docker Hub, GHCR… (url = registry host, username + access token)
type ConnectionAuthKind = 'Pat'|'ServicePrincipal'
type ConnectionDto = { id; name; type: ConnectionType; url; username: string|null; hasToken: boolean
  authKind: ConnectionAuthKind; tenantId: string|null; clientId: string|null }
// AzureDevOps: url = https://dev.azure.com/<org> (normalized server-side), token = PAT with Code: Read, Code: Status
// (build results on commits/PRs) and Service hooks: Read & write (only for "Install in Azure DevOps"). Builder never writes code.
type ConnectionInput = { name; type; url; username?; token?: string|null; authKind?; tenantId?; clientId? }
// token = PAT, or the client secret for ServicePrincipal; write-only, omit to keep
type ConnectionTestDto = { ok: boolean; message: string }
type RemoteRepositoryDto = { project: string; name: string; url: string; defaultBranch: string|null }

type RepositoryDto = { id; name; url; connectionId: string|null; connectionName: string|null; defaultBranch; runnerCount: number; createdAt }
type RepositoryInput = { connectionId?: string|null; name?: string|null; url: string; defaultBranch?: string|null }
type RunnerFileDto = {
  path: string                 // e.g. ".builder/runners/ci.yml"
  suggestedName: string        // "ci"
  entryTask: string|null       // what a build runs by default (x-builder.entry or "default")
  tasks: string[]              // job keys reachable from the entry
  error: string|null           // the file can't be planned (shown, can't be mapped meaningfully)
  mappedRunnerId: string|null; mappedRunnerName: string|null
}
type RunnerFilesDto = { branch: string; commit: string; files: RunnerFileDto[] }
type MapRunnersInput = { runners: { path: string; name?: string|null; entryTask?: string|null }[] }

// a "runner" = one mapped runner file (API path name stays /pipelines)
type PipelineDto = {
  id; name; repositoryId; repositoryName; repositoryUrl; defaultBranch
  taskfilePath: string; entryTask: string|null; lastBuild: BuildSummaryDto|null
}
type PipelineInput = { name: string; entryTask?: string|null }

type SecretDto = { id; name: string; description: string|null; updatedAt; updatedBy: string }
type SecretInput = { name: string; value?: string|null; description?: string|null }   // value write-only; omit on update to keep

type BuildStatus = 'Planning'|'Running'|'Canceling'|'Succeeded'|'Failed'|'Canceled'
type JobStatus = 'Pending'|'Queued'|'WaitingApproval'|'Assigned'|'Running'|'Succeeded'|'Failed'|'Canceled'|'Skipped'
type JobCounts = { total; pending; running; waitingApproval; succeeded; failed; skipped; canceled }
type BuildSummaryDto = {
  id; pipelineId; pipelineName; number: number; branch; commit: string|null; entryTask
  status: BuildStatus; requestedBy; error: string|null
  queuedAt; startedAt: string|null; finishedAt: string|null; jobCounts: JobCounts
  reason: BuildReason; pullRequestId: number|null
}
type BuildReason = 'Manual'|'Push'|'PullRequest'|'Schedule'|'Rerun'
type ApprovalDto = { message: string; approvers: string[]; decidedBy: string|null; decidedAt: string|null; comment: string|null }
type DeploySpecDto = { environment; compose; project; manifests; namespace; url; container: ContainerDeployDto|null }
type ContainerDeployDto = { strategy: 'BlueGreen'|'Recreate'; service; image; network; envFile: string|null; args: string[]
  healthPath: string|null; healthPort: number|null; healthScheme: string; timeoutSeconds: number; keep: number; command: string[]|null }
type JobDto = {
  id; buildId; key; taskName; description: string|null; order: number
  dependsOn: string[]; labels: string[]; artifacts: string[]; secrets: string[]; status: JobStatus
  agentId: string|null; agentName: string|null; exitCode: number|null; error: string|null
  startedAt: string|null; finishedAt: string|null
  approval: ApprovalDto|null; deploy: DeploySpecDto|null
  steps: JobStepDto[]          // the task's cmds, in order, with live status
}
type StepKind = 'Command'|'TaskCall'|'Defer'
type StepStatus = 'Pending'|'Running'|'Succeeded'|'Failed'|'Skipped'
type JobStepDto = {
  index: number; kind: StepKind
  label: string                // first line of the command, "task: <name>", "for each: <cmd>", "defer: …"
  vars: Record<string,string>|null   // TaskCall: the vars passed to the called task
  status: StepStatus; startedAt: string|null; finishedAt: string|null
}
type RunInputDto = { name: string; enum: string[]|null; requiredBy: string[] }   // go-task requires.vars
type RunInputsDto = { branch: string; entryTask: string; inputs: RunInputDto[] }
type ArtifactDto = { id; jobId; name; sizeBytes: number; createdAt }
type BuildDetailDto = BuildSummaryDto & {
  variables: Record<string,string>; jobs: JobDto[]; artifacts: ArtifactDto[]; deployments: DeploymentDto[]
  sourceRef: string|null       // e.g. refs/pull/7/merge for pull request builds
}

// triggers (x-builder.triggers in the runner file)
type PushTrigger = { branches: string[]; paths: string[]; vars: Record<string,string> }   // branches [] = all
type PullRequestTrigger = { branches: string[]; paths: string[]; vars: Record<string,string> } // target branches
type ScheduleTrigger = { cron: string; branch: string; timeZone: string; vars: Record<string,string> }
type TriggerSpec = { push: PushTrigger|null; pullRequest: PullRequestTrigger|null; schedules: ScheduleTrigger[] }
type ScheduleDto = { cron; timeZone; branch; nextRunAt: string|null; lastRunAt: string|null }
type PipelineTriggersDto = { triggers: TriggerSpec; commit: string|null; error: string|null; schedules: ScheduleDto[] }
type HookSetupDto = { url: string; header: string; secret: string|null; installed: number|null }  // secret shown once
type LogLineDto = { id: number; jobId: string; timestamp: string; stream: 'Out'|'Err'|'System'; text: string
  step: number|null }          // index into JobDto.steps; null = setup before the first step

type AgentMetricsDto = {
  at: string; cpuPercent: number; cpuCount: number; memoryTotalBytes: number; memoryUsedBytes: number
  diskTotalBytes: number; diskUsedBytes: number; loadAverage1: number; runningJobs: number
}
type AgentDto = {
  id; shared: boolean                         // shared = serves every organization; only Builder admins change it
  name; hostName; os; version; capacity: number; labels: string[]
  enabled: boolean; online: boolean; lastSeenAt: string|null
  metrics: AgentMetricsDto|null; runningJobs: { buildId; buildNumber; pipelineName; jobId; taskName }[]  // this org's only
  workDirectory: string|null                  // set in Builder; null = the agent's own default
  defaultWorkDirectory: string|null           // from the agent's configuration
  effectiveWorkDirectory: string|null; workDirectoryError: string|null   // what it uses now / why it could not switch
}

type EnvironmentType = 'SshDocker'|'Kubernetes'   // SshDocker = a server reached over SSH that already has Docker (or Podman)
type EnvironmentDto = {
  id; name; type: EnvironmentType; requiresApproval: boolean; approvers: string[]; agentLabels: string[]
  host: string|null; port: number; username: string|null; hasPrivateKey: boolean
  hasKubeconfig: boolean; aksTenantId; aksClientId; hasAksClientSecret: boolean
  aksSubscriptionId; aksResourceGroup; aksClusterName; aksAdmin: boolean
  projectId: string|null; hasPassword: boolean   // SSH with a password instead of a private key
}
type EnvironmentInput = Omit<EnvironmentDto,'id'|'hasPrivateKey'|'hasPassword'|'hasKubeconfig'|'hasAksClientSecret'>
  & { privateKey?: string|null; password?: string|null; kubeconfig?: string|null; aksClientSecret?: string|null }
  // SSH uses a key or a password: sending one replaces the other; sending neither keeps what is stored

type DeploymentStatus = 'Deploying'|'Active'|'Failed'|'Destroying'|'Destroyed'|'Superseded'|'RollingBack'|'RolledBack'
type DeploymentDto = {
  id; environmentId; environmentName; pipelineId; pipelineName; buildId; buildNumber: number; jobId
  name: string; url: string|null; status: DeploymentStatus; output: string|null; createdAt; updatedAt: string|null
  isContainer: boolean  // x-deploy container (blue-green/recreate): rollback is possible
}

type PlanPreviewDto = { entryTask: string; jobs: { key; taskName; dependsOn: string[]; approval: boolean; deploy: string|null }[]; error: string|null }
type TaskfileDto = { path: string; branch: string; commit: string; content: string }
type DashboardDto = {
  agents: AgentDto[]; activeBuilds: BuildSummaryDto[]; recentBuilds: BuildSummaryDto[]
  activeDeployments: DeploymentDto[]; last24h: { succeeded: number; failed: number; canceled: number; running: number }
}
```

## Endpoints (prefix `/api`)

Personal (no `X-Org`):

| Method | Path | Body → Result |
|---|---|---|
| GET | `/me` | `MeDto` |
| POST | `/orgs` | `{ name }` → `OrgCreatedDto` (caller becomes Owner) |

Current organization (`X-Org` required):

| Method | Path | Body → Result | Role |
|---|---|---|---|
| PUT | `/org` | `{ name }` → `OrgDto` | Admin |
| POST | `/org/agent-token` | → `{ agentToken }` (new token, shown once; old one stops working) | Admin |
| GET | `/org/members` | `MemberDto[]` | |
| POST | `/org/members` | `{ email, role }` → `MemberDto` (unknown e-mail = invitation: can sign in with Google) | Admin (Owner to add owners) |
| PUT | `/org/members/{userId}` | `{ role }` → `MemberDto` | Admin |
| DELETE | `/org/members/{userId}` | 204 (anyone may remove themselves = leave) | Admin |
| GET | `/dashboard?project=` | `DashboardDto` (agents are always the organization's) | |
| GET/POST | `/projects` | `ProjectDto[]` / `{ name, description? }` → `ProjectDto` | /Admin |
| GET/PUT/DELETE | `/projects/{id}` | `ProjectDto` / `{ name, description? }` → `ProjectDto` / 204 (409 unless empty) | /Admin/Admin |
| GET/POST | `/connections` | `ConnectionDto[]` / `ConnectionInput` → `ConnectionDto` | /Admin |
| PUT/DELETE | `/connections/{id}` | `ConnectionInput` → `ConnectionDto` / 204 | Admin |
| POST | `/connections/{id}/test` | `ConnectionTestDto` (Azure DevOps: lists projects; Azure: Entra token; Container registry: the docker login handshake) | |
| GET | `/connections/{id}/projects` | `string[]` (Azure DevOps) | |
| GET | `/connections/{id}/repositories?project=` | `RemoteRepositoryDto[]` (Azure DevOps) | |
| GET | `/connections/{id}/branches?url=` | `string[]` (any git URL through this connection) | |
| GET/POST | `/repositories` | `RepositoryDto[]` / `RepositoryInput` → `RepositoryDto` (validates access) | /Admin |
| GET/PUT/DELETE | `/repositories/{id}` | `RepositoryDto` / `RepositoryInput` → `RepositoryDto` / 204 (deletes its runners+builds) | /Admin/Admin |
| GET | `/repositories/{id}/branches` | `string[]` (default branch first) | |
| GET | `/repositories/{id}/runner-files?branch=` | `RunnerFilesDto` (`.builder/runners/*.yml|yaml` at that branch) | |
| POST | `/repositories/{id}/runners` | `MapRunnersInput` → `PipelineDto[]` (newly mapped; already-mapped files ignored) | Admin |
| POST | `/repositories/{id}/hook?origin=` | `HookSetupDto` (new webhook secret for manual setup; old one stops working) | Admin |
| POST | `/repositories/{id}/hook/install?origin=` | `HookSetupDto` (rotates the secret and creates the Azure DevOps service hooks; needs a public https URL) | Admin |
| GET | `/pipelines/{id}/triggers` | `PipelineTriggersDto` (as read from the default branch) | |
| POST | `/pipelines/{id}/triggers/refresh` | `PipelineTriggersDto` | |
| GET | `/pipelines` | `PipelineDto[]` (runners) | |
| GET/PUT/DELETE | `/pipelines/{id}` | `PipelineDto` / `PipelineInput` → `PipelineDto` / 204 (unmap) | /Admin/Admin |
| GET | `/pipelines/{id}/taskfile?branch=` | `TaskfileDto` (404 if the file isn't on that branch) | |
| POST | `/pipelines/plan` | `{ content, entryTask? }` → `PlanPreviewDto` | |
| GET | `/pipelines/{id}/inputs?branch=&entryTask=` | `RunInputsDto` (variables the Run dialog must ask for; a build without them fails planning) | |
| POST | `/pipelines/{id}/builds` | `{ branch?, entryTask?, variables? }` → `BuildSummaryDto` | |
| GET/POST | `/secrets` | `SecretDto[]` / `SecretInput` → `SecretDto` (name `^[A-Z][A-Z0-9_]*$`, unique) | /Admin |
| PUT/DELETE | `/secrets/{id}` | `SecretInput` → `SecretDto` / 204 | Admin |
| GET | `/builds?pipelineId=&status=&take=50` | `BuildSummaryDto[]` | |
| GET | `/builds/{id}` | `BuildDetailDto` | |
| POST | `/builds/{id}/cancel` · `/rerun` | `BuildSummaryDto` | |
| DELETE | `/builds/{id}` | 204 | |
| POST | `/builds/{id}/jobs/{jobId}/approval` | `{ approved, comment? }` → `JobDto` | |
| GET | `/builds/{id}/jobs/{jobId}/logs?after=0` | `LogLineDto[]` | |
| GET | `/artifacts/{id}` | file download | |
| GET | `/agents` | `AgentDto[]` (own + shared) | |
| PUT/DELETE | `/agents/{id}` | `{ enabled, workDirectory? }` → `AgentDto` / 204. `workDirectory`: omitted = unchanged, `""` = the agent's default, else an absolute path the online agent switches to for new jobs (the result says if it could). Own agents; shared ones (PUT only) for Builder admins | Admin |
| GET | `/agents/{id}/metrics` | `AgentMetricsDto[]` | |
| POST | `/agents/{id}/cleanup` | `{ removeWorkspaces, dockerPrune }` → 202 | Admin |
| GET/POST, PUT/DELETE | `/environments`, `/environments/{id}` | as before | /Admin |
| POST | `/environments/{id}/test` | `ConnectionTestDto` — an online agent with the environment's labels checks SSH + docker (or the Kubernetes API); up to ~90 s; 409 when no agent can | Admin |
| GET | `/deployments?environmentId=&active=true` | `DeploymentDto[]` | |
| POST | `/deployments/{id}/destroy` | `DeploymentDto` | |
| POST | `/deployments/{id}/rollback` | `DeploymentDto` (container deployments: the previous container goes live again; RollingBack → RolledBack, the previous deployment → Active) | |
| POST | `/cleanup` | `{ olderThanDays, keepLastPerPipeline, removeWorkspaces, dockerPrune }` → `{ buildsDeleted, artifactsDeleted, bytesFreed, agentsNotified }` | Admin |

## Live updates — SignalR hub `/hubs/ui`

Client → server: `JoinOrg(orgId)` (call after connecting and whenever the current organization changes; also after
reconnect), `JoinBuild(buildId)`, `LeaveBuild(buildId)`. Both check membership (HubException otherwise).

| Event | Payload | Who |
|---|---|---|
| `BuildUpdated` | `BuildSummaryDto` | members of the build's organization |
| `JobUpdated` | `JobDto` | clients that joined the build |
| `Log` | `(buildId, LogLineDto[])` | clients that joined the build |
| `AgentsUpdated` | `AgentDto[]` (own + shared, every ~3 s) | the organization |
| `DeploymentUpdated` | `DeploymentDto` | the organization |

## Agents (daemons)

A daemon connects out to the API (`/hubs/agent`, header `X-Agent-Token`). An organization's token (from
`POST /api/org/agent-token`) registers that organization's agents; the system token (`Agents:Token`) registers
shared agents. Install: `Agent__ServerUrl=https://<api> Agent__Token=<token> dotnet Builder.Agent.dll` or the agent
container image.

## Webhooks (git hosts → Builder)

`POST /hooks/azure-devops/{repositoryId}` (through the public endpoint; anonymous) with header
`X-Builder-Hook: <secret>` (or Basic auth with the secret as password). Events: `git.push`,
`git.pullrequest.created`, `git.pullrequest.updated`. Answers `202 { builds: [ids] }`, `401` for a wrong secret.
