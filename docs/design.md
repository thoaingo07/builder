# Builder — design

A self-hosted distributed build & deploy system. A central **server** schedules
work; **agents** on any number of machines pull jobs, run them, stream logs and
report hardware metrics. A **Vue** web UI, served through a BFF, shows everything live. State lives in
**PostgreSQL**.

```
 Browser (Vue 3 SPA)
    │  same-origin, HttpOnly cookie only — no tokens in the browser
    ▼
 Builder.Bff  ── serves the SPA, login/logout, cookie session,
    │            YARP proxy /api/* and /hubs/ui → API with a short-lived JWT
    ▼
 Builder.Api  ── REST, SignalR (/hubs/ui, /hubs/agent), scheduler, planner
    │   ├── PostgreSQL (EF Core / Npgsql; schema = FluentMigrator raw SQL in db/migrations)
    │   └── git clone (plan builds, commit Taskfile edits) ──► Azure DevOps Git
    ▲
    │ SignalR /hubs/agent (agent token): jobs, logs, metrics, cancel, cleanup
 Builder.Agent × N  ── git ─► Azure DevOps   ssh ─► VPS (docker compose)   kubectl ─► AKS
```

## Solution layout (Clean Architecture)

Dependencies point inward only: `Api/Bff/Infrastructure → Application → Domain`.
`Contracts` is a plain DTO library the Application layer uses to talk to agents.

| Project | Responsibility |
|---|---|
| `Builder.Domain` | Entities (Pipeline, Build, BuildJob, Agent, Environment, Deployment, Connection, Approval, User), enums, state transitions (`Build.Cancel()`, `BuildJob.Complete()`, graph promotion rules). No framework references. |
| `Builder.Application` | Use cases (one class per command/query), ports (`IBuilderDbContext`, `IGitService`, `ITaskfilePlanner`, `IAgentGateway`, `IUiNotifier`, `ISecretProtector`, `IAzureDevOpsClient`, `IClock`), DTOs, the scheduler algorithm. |
| `Builder.Infrastructure` | EF Core + Npgsql (data access only, snake_case), git CLI, Taskfile parser (YamlDotNet), Data Protection secrets, Azure DevOps REST client, artifact file store. |
| `Builder.Api` | Composition root: minimal-API endpoints, SignalR hubs, JWT bearer auth (issued by the BFF), agent-token auth, hosted scheduler. |
| `Builder.Bff` | Backend-for-frontend: cookie auth, login against the API, YARP reverse proxy that swaps the cookie for a signed JWT, serves the built Vue app. |
| `Builder.Migrations` | Schema owner: FluentMigrator running raw SQL pairs `db/migrations/{000001}_{title}.{up,down}.sql` (embedded). Console (`up`, `down <version>`, `rollback [n]`, `status`) and library (the API migrates on startup). |
| `Builder.Contracts` | Agent ⇄ API wire protocol (shared by Application, Api and Agent). |
| `Builder.Agent` | Worker service: metrics, checkout, run go-task, deploy actions, artifacts, cleanup. |
| `ui/` | Vue 3 + Vite + TypeScript + Nuxt UI (components only, no Nuxt server) + Pinia + Vue Flow. Talks only to the BFF. |
| `Builder.AppHost` | .NET Aspire orchestration for local dev and integration tests (PostgreSQL, migrator, API, BFF, agents, Vite). |

Runtime packaging: `deploy/containers/*.Containerfile` + `deploy/compose.yml` (Podman or Docker).

## Deployment model: cloud control plane, daemons on your servers

The UI, BFF, API and PostgreSQL run in the cloud. **Daemons** (`Builder.Agent`) run on your own servers and dial
**out** to the API over HTTPS/WebSockets (no inbound ports, NAT is fine). The cloud never clones a repository:
for Azure DevOps it reads branches, runner files and Taskfiles through the REST API and commits Taskfile edits
with the pushes API. Daemons do the work: fetch the repository at the build's commit, run the steps, fetch the
job's secrets at run time, deploy, upload artifacts, stop on cancel, and clean up workspaces / container images.

## Organizations

Every user can create organizations and invite people by e-mail (an invited e-mail may sign in with Google).
Connections, repositories, runners, builds, environments, deployments, secrets and agents belong to one
organization; EF Core global query filters scope every query to the organization in the `X-Org` header after a
membership check, and background work (scheduler, planner) scopes explicitly by the build's organization.
Agents register with their organization's token, or with the system token as **shared** agents that serve every
organization (an organization's own agents are preferred).

## Projects

An organization's work is grouped in projects (one product, one customer). Repositories belong to one project and
their runners, builds and deployments with them. Connections, environments and secrets belong to a project or are
shared by the organization; a runner's names resolve in its project first, then in the shared ones, so two projects
can each have their own `docker.io` login or `vps` environment while a common one is shared. Agents stay per
organization (+ the shared pool). Projects are grouping, not a permission boundary: members see every project.

## Repositories and runners

A repository is added once per organization (Azure DevOps picker: project → repository → branch). Builder lists
`.builder/runners/*.yml|yaml` on any branch; you pick which files to **map** into runners. Running a runner runs
that file on the branch you choose, **from the repository root** (`task --dir <root> --taskfile <runner>`), so
locally the equivalent is `task -d . -t .builder/runners/ci.yml`.

## Secrets

Organization secrets are encrypted at rest and write-only in the UI/API. A task declares what it needs:

```yaml
deploy:
  x-secrets: [REGISTRY_TOKEN, DB_PASSWORD]
  cmds:
    - echo "$REGISTRY_TOKEN" | docker login -u ci --password-stdin registry.example.com
    - ./migrate --password {{.DB_PASSWORD}}
```

Planning fails early when a declared secret does not exist. At run time the daemon asks the API for the job's
secrets over its authenticated connection; the API only answers the agent the job is assigned to, while the job
runs, and only with the declared names. The daemon passes them to go-task as variables (`NAME=value` arguments)
and environment variables, and masks the values (≥ 4 characters) in every log line it ships.

## What daemons keep (nothing)

- **Credentials are fetched, not shipped**: the job message carries no credentials. When a job starts the daemon
  asks the API for that job's credentials and secrets (only the assigned agent, only while the job runs).
- **Short-lived where possible**: connections can use an **Entra service principal** instead of a PAT. The API
  then mints per-job tokens: Azure DevOps (git fetch, Azure Artifacts) ~1 h; ACR ~3 h (`x-registries`, via the
  `/oauth2/exchange` of the registry). PAT connections hand out the PAT for that job.
- **Memory and the job sandbox only**: credentials live in the daemon's memory and the job's environment; git
  auth uses `GIT_CONFIG_*` environment variables, secrets are environment variables, nothing is on a command line.
  Each job has its own `HOME`, `DOCKER_CONFIG`, `AZURE_CONFIG_DIR`, `TMPDIR`…, deleted when the job ends, so
  `docker login`, `dotnet nuget add source`, `az login` leave nothing behind. Leftovers from a crash are swept on
  start.
- **No source left**: the checkout is deleted when the build finishes (`Agent:KeepWorkspaces=true` to keep).
  Package caches (NuGet/npm) stay shared: packages, not credentials.

```yaml
publish:
  x-azure-artifacts: true                 # $VSS_NUGET_ACCESSTOKEN / $AZURE_DEVOPS_TOKEN for Azure Artifacts feeds
  x-registries: [shop.azurecr.io]         # docker login done for you (short-lived ACR token)
  cmds:
    - dotnet restore                      # private feed via the Azure Artifacts credential provider
    - docker build -t shop.azurecr.io/web:{{.BUILDER_BUILD_NUMBER}} .
    - docker push shop.azurecr.io/web:{{.BUILDER_BUILD_NUMBER}}
```

Requirements: the service principal is a user of the Azure DevOps organization (for git / Artifacts) and has
`AcrPush` on the registry. NuGet restores need the Azure Artifacts Credential Provider on the daemon
(`NUGET_PLUGIN_PATHS`), or use `$AZURE_DEVOPS_TOKEN` with `dotnet nuget add source` inside the job.

## Pipelines are Taskfiles

Pipelines are plain [go-task](https://taskfile.dev) `Taskfile.yml` files kept in
the repository, so every pipeline can also be run locally with `task <name>`.
Builder reads the same file and distributes it:

| Taskfile concept | Builder meaning |
|---|---|
| task | one **job** (a node in the build graph) |
| `deps:` | edges of the graph — deps run **in parallel**, possibly on different agents |
| `cmds:` | **sequential** steps inside the job (`- task: x` calls run inline, in order) |
| `vars`, `env`, `dir`, templating, `status`, `preconditions` | handled by go-task itself on the agent |

Go-task ignores keys prefixed with `x-`, so Builder's extensions live there:

```yaml
version: '3'

x-builder:
  entry: ci                 # task started when a build is triggered (default: "default")

tasks:
  build-api:
    x-agent: { labels: [linux, dotnet] }   # agent must have all labels
    x-artifacts: [out/api/**]              # relative to the Taskfile's directory; uploaded after success,
                                           # unpacked into the same place for downstream jobs
    cmds: [dotnet publish src/Api -o out/api]

  approve-prod:
    x-approval:
      message: Ship to production?
      approvers: [admin]                   # empty = any signed-in user

  deploy-prod:
    deps: [approve-prod, build-api]
    x-deploy:
      environment: prod-vps                # defined in the UI (Environments)
      compose: deploy/docker-compose.yml   # ssh-docker: copied to the host, `docker compose up -d`
      project: shop
      url: https://shop.example.com        # shown as "Open app" in the UI
```

### How a job runs on an agent

1. Checkout: `git fetch --depth 1 <repo> <commit>` into `<workdir>/builds/<buildId>/src`
   (Azure DevOps PAT passed as an `http.extraHeader`, never written to disk).
2. Download artifacts of upstream jobs (`x-artifacts`) from the server.
3. Write `.builder.Taskfile.yml` next to the real Taskfile: identical except the
   job's `deps:` are removed (Builder already ran them), then run
   `task -t .builder.Taskfile.yml <task>`. go-task does all templating.
4. If `x-deploy` is set, run the built-in deploy action for the environment type.
5. Upload `x-artifacts`, report the result.

Builder env vars available to every command: `BUILDER_BUILD_ID`, `BUILDER_BUILD_NUMBER`,
`BUILDER_COMMIT`, `BUILDER_BRANCH`, `BUILDER_PIPELINE`, plus deploy vars
(`DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_SSH_KEY` (file path), `KUBECONFIG`).

## Environments (deploy targets)

### Container deployments (blue-green / recreate)

```yaml
deploy-portal:
  x-registries: [docker.io]                     # Container registry connection (user + token)
  x-deploy:
    environment: vps
    strategy: blue-green                        # or recreate: stop the old one first (single-instance workers)
    service: portal-web                         # the network alias the reverse proxy targets
    image: thoaingo07/my-apps:portal-web-{{.BUILDER_COMMIT}}
    network: blf_default
    env-file: /opt/blf/env/portal.env           # stays on the server
    args: [--memory, 1g]
    command: []                                 # optional: arguments after the image
    health: { path: /health/ready, port: 8006, scheme: https, timeout: 120 }   # omit path: the image's HEALTHCHECK
    keep: 1                                     # stopped previous containers kept for rollback
```

The agent sends `deploy-container.sh` over SSH: pull, start the candidate next to the live container (alias
`<service>-candidate`), wait until healthy (image HEALTHCHECK, or an HTTP probe from a curl container on the same
network), then move the `<service>` alias to it and stop the old one (kept for rollback). Unhealthy: the candidate
is removed and its last log lines go to the build log; the live container never stopped serving. Recreate stops
the old container first and starts it again if the new one fails. Rollback and destroy are deployment actions.
State lives in `~/.builder/containers/<service>` on the host; containers carry `builder.service` labels.


| Type | Config | Built-in action | Teardown |
|---|---|---|---|
| `ssh-docker` (Docker **or Podman** host) | host, port, user, private key | scp compose file to `~/builder/<project>/` → `pull` (best effort) + `up -d` with the first of `docker compose`, `podman-compose`, `podman compose` found on the host | `<compose> down`, remove the folder |
| `kubernetes` | kubeconfig **or** AKS (tenant, client id/secret, subscription, resource group, cluster) | `kubectl apply -n <namespace> -f <manifests>` | `kubectl delete namespace <namespace>` |

Environments can require approval for every deploy (on top of task-level
`x-approval`). Secrets are encrypted with ASP.NET Data Protection.

## Job lifecycle

`Pending → Queued → (WaitingApproval) → Assigned → Running → Succeeded | Failed | Canceled | Skipped`

- A job becomes `Queued` when all deps succeeded; dependents of a failed or
  rejected job become `Skipped`.
- The scheduler assigns queued jobs to online, enabled agents whose labels match
  and that have a free slot (`capacity`, default 2).
- Cancel: pending jobs → `Canceled`, running jobs get a `CancelJob` (process tree killed).
- Agent disconnect: its running jobs fail with "agent lost".

## UI

Dashboard (agents with CPU / RAM / disk, running builds) · Builds (graph with live
status, logs, cancel, approve, re-run) · Pipelines (create from Azure DevOps repo,
run with branch/task/vars, visual + YAML Taskfile editor that commits back to git) ·
Agents · Environments & Deployments (open app, destroy) · Connections · Cleanup
(old builds, artifacts, agent workspaces, docker prune).

## Deploy test harness

`deploy/test-vps/` is a stand-in VPS (sshd + compose CLI) that drives the local Podman/Docker through its socket.
The Aspire AppHost adds it when `Builder:TestVpsAuthorizedKey` is set; `DeployTests` uses it to deploy, redeploy
(supersede) and destroy a real nginx app end to end.

## Not in the MVP

Taskfile `includes:` (planning only sees the root file), OIDC / Entra ID login in the
BFF (local users for now), multi-user RBAC beyond admin/approver, Windows agent metrics.
