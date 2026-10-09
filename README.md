# Builder

Self-hosted distributed build & deploy system: .NET 10 server + agents, Vue 3 (Nuxt UI) web app,
pipelines written as [go-task](https://taskfile.dev) Taskfiles.

- Checkout from Azure DevOps Git (or any git URL)
- Taskfile `deps` run **in parallel across agents**, `cmds` run in sequence
- Approval gates (`x-approval`) and per-environment approvals
- Deploy to a VPS over SSH with Docker **or Podman** compose, or to Kubernetes / AKS (`x-deploy`)
- Live UI: builds, logs, agent CPU / RAM / disk, cancel, re-run, clean up, visual Taskfile editor
- Clean Architecture, PostgreSQL (raw SQL migrations via FluentMigrator), BFF in front of the SPA

Docs: [design](docs/design.md) · [API](docs/api.md) · [migrations](db/README.md) · [sample pipeline](samples/Taskfile.yml) · [dev host on Podman](docs/ops/dev-host-podman.md)

## Run it

Prerequisites: .NET 10 SDK, Node 22, [go-task](https://taskfile.dev/installation/), git, and Docker **or** Podman.

### 1. Everything with .NET Aspire (recommended for development)

```bash
task dev        # = dotnet run --project src/Builder.AppHost
```

Starts PostgreSQL (container), runs the migrations, then the API, BFF, two agents and the Vite dev server,
and opens the Aspire dashboard (logs, traces, resource state). Sign in with `admin` / `admin`.

### 2. Everything in containers (Podman or Docker)

```bash
cp deploy/.env.example deploy/.env          # set the secrets
podman compose -f deploy/compose.yml up -d --build      # or: docker compose …  (task up)
podman compose -f deploy/compose.yml up -d --scale agent=3
```

With Podman, prefer `podman-compose` (tested with 1.0.6, rootless): `podman compose` hands off to the
Docker Compose plugin when one is installed, which then talks to Docker instead of Podman.

UI at http://localhost:19000. Agents run inside containers with git, go-task, ssh, kubectl and the docker CLI;
point `CONTAINER_SOCKET` in `.env` at your Podman (`/run/user/1000/podman/podman.sock`) or Docker socket so
pipeline steps can build images. Add `BUILDER_AGENT_INSTALL_AZ=true` for AKS deployments.

Agents on other machines: run the agent image (or `dotnet Builder.Agent.dll`) with
`Agent__ServerUrl=http://<server>:19100` and `Agent__Token=<BUILDER_AGENT_TOKEN>`.

### 3. Processes by hand

```bash
task db:up                 # PostgreSQL on :15433
task api                   # :19100, migrates on startup
task bff                   # :19000
task agent -- agent-1
task ui:dev                # :19001
```

## Tests

```bash
task test
```

Unit tests (Taskfile planner, build state machine) plus integration tests that start the real stack through
**Aspire** (`Aspire.Hosting.Testing`): migrations up/down on PostgreSQL, the EF mapping against the SQL schema,
and end-to-end builds through BFF → API → two agents (parallel jobs, artifacts, approval, cancel), plus a real
SSH deploy / redeploy / destroy against a throwaway VPS container. Containers run on **Podman** when it is
installed (`ASPIRE_CONTAINER_RUNTIME` overrides).

## Writing a pipeline

```yaml
version: '3'
x-builder: { entry: ci }
tasks:
  ci: { deps: [deploy] }
  build:
    x-artifacts: [out/**]
    cmds: [dotnet publish -o out]
  test:
    cmds: [dotnet test]
  approve:
    deps: [build, test]          # build and test run in parallel, maybe on different agents
    x-approval: { message: Ship it? }
  deploy:
    deps: [approve]
    x-deploy: { environment: prod-vps, compose: deploy/compose.yml, project: shop, url: https://shop.example.com }
```

The same file still runs locally with `task ci`. See [docs/design.md](docs/design.md) for every extension.
