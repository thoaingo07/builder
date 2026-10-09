// Mirrors docs/api.md — keep in sync with the backend DTOs.

export type Guid = string
export type IsoDate = string

export interface UserDto { userName: string; displayName: string; isAdmin: boolean }

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
}

/** A "runner": one mapped runner file (the API keeps the /pipelines path). */
export interface PipelineDto {
  id: Guid; name: string; repositoryId: Guid; repositoryName: string; repositoryUrl: string; defaultBranch: string
  taskfilePath: string; entryTask: string | null; lastBuild: BuildSummaryDto | null
}
export interface PipelineInput { name: string; entryTask?: string | null }

export interface RepositoryDto {
  id: Guid; name: string; url: string; connectionId: Guid | null; connectionName: string | null
  defaultBranch: string; runnerCount: number; createdAt: IsoDate
}
export interface RepositoryInput { connectionId?: Guid | null; name?: string | null; url: string; defaultBranch?: string | null }
export interface RunnerFileDto {
  path: string; suggestedName: string; entryTask: string | null; tasks: string[]; error: string | null
  mappedRunnerId: Guid | null; mappedRunnerName: string | null
}
export interface RunnerFilesDto { branch: string; commit: string; files: RunnerFileDto[] }
export interface MapRunnersInput { runners: { path: string; name?: string | null; entryTask?: string | null }[] }

export interface SecretDto { id: Guid; name: string; description: string | null; updatedAt: IsoDate; updatedBy: string }
export interface SecretInput { name: string; value?: string | null; description?: string | null }

export interface ApprovalDto {
  message: string; approvers: string[]; decidedBy: string | null; decidedAt: IsoDate | null; comment: string | null
}
export interface DeploySpecDto {
  environment: string; compose: string | null; project: string | null
  manifests: string | null; namespace: string | null; url: string | null
}
export interface JobDto {
  id: Guid; buildId: Guid; key: string; taskName: string; description: string | null; order: number
  dependsOn: string[]; labels: string[]; artifacts: string[]; secrets: string[]; status: JobStatus
  agentId: Guid | null; agentName: string | null; exitCode: number | null; error: string | null
  startedAt: IsoDate | null; finishedAt: IsoDate | null
  approval: ApprovalDto | null; deploy: DeploySpecDto | null
}
export interface ArtifactDto { id: Guid; jobId: Guid; name: string; sizeBytes: number; createdAt: IsoDate }

export interface BuildDetailDto extends BuildSummaryDto {
  variables: Record<string, string>; jobs: JobDto[]; artifacts: ArtifactDto[]; deployments: DeploymentDto[]
}

export type LogStream = 'Out' | 'Err' | 'System'
export interface LogLineDto { id: number; jobId: Guid; timestamp: IsoDate; stream: LogStream; text: string }

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
}
export type EnvironmentInput = Omit<EnvironmentDto, 'id' | 'hasPrivateKey' | 'hasKubeconfig' | 'hasAksClientSecret'> & {
  privateKey?: string | null; kubeconfig?: string | null; aksClientSecret?: string | null
}

export type DeploymentStatus = 'Deploying' | 'Active' | 'Failed' | 'Destroying' | 'Destroyed'
export interface DeploymentDto {
  id: Guid; environmentId: Guid; environmentName: string; pipelineId: Guid; pipelineName: string
  buildId: Guid; buildNumber: number; jobId: Guid; name: string; url: string | null
  status: DeploymentStatus; output: string | null; createdAt: IsoDate; updatedAt: IsoDate | null
}

/** Azure = Azure Resource Manager / container registries (always a service principal). */
export type ConnectionType = 'AzureDevOps' | 'Git' | 'Azure'
export type ConnectionAuthKind = 'Pat' | 'ServicePrincipal'
export interface ConnectionDto {
  id: Guid; name: string; type: ConnectionType; url: string; username: string | null; hasToken: boolean
  authKind: ConnectionAuthKind; tenantId: string | null; clientId: string | null
}
/** `token` is the PAT, or the client secret for a service principal; write-only, omit to keep. */
export interface ConnectionInput {
  name: string; type: ConnectionType; url: string; username?: string | null; token?: string | null
  authKind?: ConnectionAuthKind; tenantId?: string | null; clientId?: string | null
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
