// Mirrors docs/api.md — keep in sync with the backend DTOs.

export type Guid = string
export type IsoDate = string

export interface UserDto { userName: string; displayName: string; isAdmin: boolean }

export interface ProjectDto {
  id: Guid; name: string; slug: string; description: string | null
  repositoryCount: number; runnerCount: number; connectionCount: number; environmentCount: number; secretCount: number
  createdAt: IsoDate
}
export interface ProjectInput { name: string; description?: string | null }

export type OrgRole = 'Member' | 'Admin' | 'Owner'
export interface OrgDto { id: Guid; name: string; slug: string; role: OrgRole; memberCount: number; createdAt: IsoDate }
export interface MeDto { user: UserDto; orgs: OrgDto[] }
export interface OrgCreatedDto { org: OrgDto; agentToken: string }
export interface MemberDto {
  userId: Guid; userName: string; displayName: string; email: string | null; role: OrgRole
  canSignInWithGoogle: boolean; joinedAt: IsoDate
}

export type BuildStatus = 'Planning' | 'Running' | 'Canceling' | 'Succeeded' | 'Failed' | 'Canceled'
export type JobStatus =
  | 'Pending' | 'Queued' | 'WaitingApproval' | 'Assigned' | 'Running'
  | 'Succeeded' | 'Failed' | 'Canceled' | 'Skipped'

export interface JobCounts {
  total: number; pending: number; running: number; waitingApproval: number
  succeeded: number; failed: number; skipped: number; canceled: number
}

export interface BuildSummaryDto {
  id: Guid; pipelineId: Guid; pipelineName: string; number: number; branch: string
  commit: string | null; entryTask: string; status: BuildStatus; requestedBy: string
  error: string | null; queuedAt: IsoDate; startedAt: IsoDate | null; finishedAt: IsoDate | null
  jobCounts: JobCounts
  reason: BuildReason; pullRequestId: number | null
  projectId: Guid
}
export type BuildReason = 'Manual' | 'Push' | 'PullRequest' | 'Schedule' | 'Rerun'

// triggers (x-builder.triggers in the runner file)
/** branches [] = all branches */
export interface PushTrigger { branches: string[]; paths: string[]; vars: Record<string, string> }
/** branches = target branches */
export interface PullRequestTrigger { branches: string[]; paths: string[]; vars: Record<string, string> }
export interface ScheduleTrigger { cron: string; branch: string; timeZone: string; vars: Record<string, string> }
export interface TriggerSpec { push: PushTrigger | null; pullRequest: PullRequestTrigger | null; schedules: ScheduleTrigger[] }
export interface ScheduleDto { cron: string; timeZone: string; branch: string; nextRunAt: IsoDate | null; lastRunAt: IsoDate | null }
export interface PipelineTriggersDto { triggers: TriggerSpec; commit: string | null; error: string | null; schedules: ScheduleDto[] }
/** secret is shown once */
export interface HookSetupDto { url: string; header: string; secret: string | null; installed: number | null }

/** A "runner": one mapped runner file (the API keeps the /pipelines path). */
export interface PipelineDto {
  id: Guid; name: string; repositoryId: Guid; repositoryName: string; repositoryUrl: string; defaultBranch: string
  taskfilePath: string; entryTask: string | null; lastBuild: BuildSummaryDto | null
  projectId: Guid
}
export interface PipelineInput { name: string; entryTask?: string | null }

export interface RepositoryDto {
  id: Guid; name: string; url: string; connectionId: Guid | null; connectionName: string | null
  defaultBranch: string; runnerCount: number; createdAt: IsoDate
  projectId: Guid
}
/** projectId: required when the org has 2+ projects; moving a repository = PUT with another projectId */
export interface RepositoryInput { connectionId?: Guid | null; name?: string | null; url: string; defaultBranch?: string | null; projectId?: Guid | null }
export interface RunnerFileDto {
  path: string; suggestedName: string; entryTask: string | null; tasks: string[]; error: string | null
  mappedRunnerId: Guid | null; mappedRunnerName: string | null
}
export interface RunnerFilesDto { branch: string; commit: string; files: RunnerFileDto[] }
export interface MapRunnersInput { runners: { path: string; name?: string | null; entryTask?: string | null }[] }

/** projectId null = shared by every project of the organization */
export interface SecretDto { id: Guid; name: string; description: string | null; updatedAt: IsoDate; updatedBy: string; projectId: Guid | null }
/** projectId: always send it (null = shared); omitting it on update makes the secret shared */
export interface SecretInput { name: string; value?: string | null; description?: string | null; projectId: Guid | null }

export interface ApprovalDto {
  message: string; approvers: string[]; decidedBy: string | null; decidedAt: IsoDate | null; comment: string | null
}
export interface DeploySpecDto {
  environment: string; compose: string | null; project: string | null
  manifests: string | null; namespace: string | null; url: string | null
  /** single-container deploy (x-deploy.strategy) */
  container: ContainerDeployDto | null
}
export type ContainerStrategy = 'BlueGreen' | 'Recreate'
/** one container on an SSH host behind a network alias; traffic switches once healthy */
export interface ContainerDeployDto {
  strategy: ContainerStrategy; service: string; image: string; network: string; envFile: string | null
  args: string[]; command: string[] | null
  healthPath: string | null; healthPort: number | null; healthScheme: string; timeoutSeconds: number; keep: number
}
export interface JobDto {
  id: Guid; buildId: Guid; key: string; taskName: string; description: string | null; order: number
  dependsOn: string[]; labels: string[]; artifacts: string[]; secrets: string[]; status: JobStatus
  agentId: Guid | null; agentName: string | null; exitCode: number | null; error: string | null
  startedAt: IsoDate | null; finishedAt: IsoDate | null
  approval: ApprovalDto | null; deploy: DeploySpecDto | null
  /** the task's cmds, in order, with live status */
  steps: JobStepDto[]
}
export type StepKind = 'Command' | 'TaskCall' | 'Defer'
export type StepStatus = 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Skipped'
export interface JobStepDto {
  index: number; kind: StepKind
  /** first line of the command, "task: <name>", "for each: <cmd>", "defer: …" */
  label: string
  /** TaskCall: the vars passed to the called task */
  vars: Record<string, string> | null
  status: StepStatus; startedAt: IsoDate | null; finishedAt: IsoDate | null
}
/** a go-task `requires.vars` entry the Run dialog must ask for */
export interface RunInputDto { name: string; enum: string[] | null; requiredBy: string[] }
export interface RunInputsDto { branch: string; entryTask: string; inputs: RunInputDto[] }
export interface ArtifactDto { id: Guid; jobId: Guid; name: string; sizeBytes: number; createdAt: IsoDate }

export interface BuildDetailDto extends BuildSummaryDto {
  /** e.g. refs/pull/7/merge for pull request builds */
  sourceRef: string | null
  variables: Record<string, string>; jobs: JobDto[]; artifacts: ArtifactDto[]; deployments: DeploymentDto[]
}

export type LogStream = 'Out' | 'Err' | 'System'
export interface LogLineDto {
  id: number; jobId: Guid; timestamp: IsoDate; stream: LogStream; text: string
  /** index into JobDto.steps; null = setup before the first step */
  step: number | null
}

export interface AgentMetricsDto {
  at: IsoDate; cpuPercent: number; cpuCount: number
  memoryTotalBytes: number; memoryUsedBytes: number
  diskTotalBytes: number; diskUsedBytes: number
  loadAverage1: number; runningJobs: number
}
export interface AgentRunningJob { buildId: Guid; buildNumber: number; pipelineName: string; jobId: Guid; taskName: string }
export interface AgentDto {
  id: Guid
  /** serves every organization; not manageable from an org */
  shared: boolean
  name: string; hostName: string; os: string; version: string; capacity: number
  labels: string[]; enabled: boolean; online: boolean; lastSeenAt: IsoDate | null
  metrics: AgentMetricsDto | null; runningJobs: AgentRunningJob[]
}

export type EnvironmentType = 'SshDocker' | 'Kubernetes'
export interface EnvironmentDto {
  id: Guid; name: string; type: EnvironmentType; requiresApproval: boolean
  approvers: string[]; agentLabels: string[]
  host: string | null; port: number; username: string | null; hasPrivateKey: boolean
  hasKubeconfig: boolean; aksTenantId: string | null; aksClientId: string | null; hasAksClientSecret: boolean
  aksSubscriptionId: string | null; aksResourceGroup: string | null; aksClusterName: string | null; aksAdmin: boolean
  /** null = shared by every project */
  projectId: Guid | null
  /** SSH logs in with a stored password instead of a private key */
  hasPassword: boolean
}
export type EnvironmentInput = Omit<EnvironmentDto, 'id' | 'hasPrivateKey' | 'hasPassword' | 'hasKubeconfig' | 'hasAksClientSecret'> & {
  /** a new key replaces a stored password and the other way round */
  privateKey?: string | null; password?: string | null; kubeconfig?: string | null; aksClientSecret?: string | null
}

export type DeploymentStatus = 'Deploying' | 'Active' | 'Failed' | 'Destroying' | 'Destroyed' | 'Superseded' | 'RollingBack' | 'RolledBack'
export interface DeploymentDto {
  id: Guid; environmentId: Guid; environmentName: string; pipelineId: Guid; pipelineName: string
  buildId: Guid; buildNumber: number; jobId: Guid; name: string; url: string | null
  status: DeploymentStatus; output: string | null; createdAt: IsoDate; updatedAt: IsoDate | null
  /** a container deploy (blue-green/recreate): can be rolled back to the kept container */
  isContainer: boolean
  projectId: Guid
}

/** Azure = Azure Resource Manager / ACR (always a service principal); Registry = Docker Hub, GHCR… (user + token). */
export type ConnectionType = 'AzureDevOps' | 'Git' | 'Azure' | 'Registry'
export type ConnectionAuthKind = 'Pat' | 'ServicePrincipal'
export interface ConnectionDto {
  id: Guid; name: string; type: ConnectionType; url: string; username: string | null; hasToken: boolean
  authKind: ConnectionAuthKind; tenantId: string | null; clientId: string | null
  /** null = shared by every project */
  projectId: Guid | null
}
/** `token` is the PAT, or the client secret for a service principal; write-only, omit to keep. */
export interface ConnectionInput {
  name: string; type: ConnectionType; url: string; username?: string | null; token?: string | null
  authKind?: ConnectionAuthKind; tenantId?: string | null; clientId?: string | null
  /** always send it (null = shared); omitting it on update makes the connection shared */
  projectId: Guid | null
}
export interface ConnectionTestDto { ok: boolean; message: string }
export interface RemoteRepositoryDto { project: string; name: string; url: string; defaultBranch: string | null }

export interface PlanJobDto { key: string; taskName: string; dependsOn: string[]; approval: boolean; deploy: string | null }
export interface PlanPreviewDto { entryTask: string; jobs: PlanJobDto[]; error: string | null }
export interface TaskfileDto { path: string; branch: string; commit: string; content: string }

export interface DashboardDto {
  agents: AgentDto[]; activeBuilds: BuildSummaryDto[]; recentBuilds: BuildSummaryDto[]
  activeDeployments: DeploymentDto[]
  last24h: { succeeded: number; failed: number; canceled: number; running: number }
}

export interface CleanupInput { olderThanDays: number; keepLastPerPipeline: number; removeWorkspaces: boolean; dockerPrune: boolean }
export interface CleanupResultDto { buildsDeleted: number; artifactsDeleted: number; bytesFreed: number; agentsNotified: number }

export interface ProblemDetails { title?: string; detail?: string; status?: number; code?: string; errors?: Record<string, string[]> }
