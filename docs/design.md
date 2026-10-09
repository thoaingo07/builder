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
    x-artifacts: [out/api/**]              # uploaded to the server after success
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

| Type | Config | Built-in action | Teardown |
|---|---|---|---|
| `ssh-docker` | host, port, user, private key | scp compose file → `docker compose -p <project> up -d --pull always` | `docker compose -p <project> down` |
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

## Not in the MVP

Taskfile `includes:` (planning only sees the root file), OIDC / Entra ID login in the
BFF (local users for now), multi-user RBAC beyond admin/approver, Windows agent metrics.
