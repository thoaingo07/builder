# Builder runner guide (for people and coding agents)

This page explains how to write **runner files** for Builder, a distributed build and deploy system. If you are a
coding agent asked to "create a Builder runner" or "add a Builder pipeline" for a repository, follow it exactly:
everything Builder reads is described here, and anything not described here is plain [go-task](https://taskfile.dev)
behaviour.

## 1. What a runner file is

- A runner is a **go-task Taskfile (version '3')** stored in the repository at **`.builder/runners/<name>.yml`**
  (or `.yaml`). Only files directly in that folder are runners; sub-folders are ignored.
- Builder only **reads** the repository. It never commits, so the runner file is added and changed with a normal
  commit or pull request.
- After the file is on a branch, someone maps it in Builder's UI (Repository → Runners). It can then run on any
  branch, and the file is read at the commit being built.
- Builder settings live in keys that start with **`x-`**. go-task ignores them, so the file stays a valid
  Taskfile: you can still run `task --taskfile .builder/runners/ci.yml <task>` on a laptop.
- Commands run **from the repository root** (`task --dir <repo root>`), not from `.builder/runners/`. Write paths
  relative to the repository root.

## 2. How Builder runs a runner file

1. **Entry task:** the task chosen in the Run dialog, else `x-builder.entry`, else `default`.
2. **Jobs:** every task reachable from the entry through `deps` becomes a **job**.
   - `deps` are **edges**. A task's deps run first, **in parallel**, possibly on different machines.
   - `cmds` are the **steps** of one job, run **in order** on one agent.
3. **Job isolation:** each job runs on an agent with **its own fresh checkout** of the commit. Builder strips
   `deps` before running a job (they are separate jobs), so:
   - nothing a dependency produced exists in the job's checkout unless it was passed as an **artifact**
     (`x-artifacts`, §5.3);
   - images built in one job are only reachable from another job through a **registry**, not the local docker
     image cache.
4. **Same task, different vars:** the same task used as a dep with different `vars` becomes separate jobs, e.g.
   `image[SERVICE=web]` and `image[SERVICE=api]`. This is the way to fan out.
5. **Failure:** a job fails when any command exits non-zero. Jobs that depend on it don't run, and the build fails.
6. **Credentials** (secrets, registry logins, deploy keys) are fetched by the agent when the job starts, kept in
   memory or a per-job temp folder, masked in logs, and deleted when the job ends. Never write credentials into
   the YAML.

### go-task features: supported or not

| Feature | In Builder |
|---|---|
| `version: '3'` | required (or omitted) |
| `tasks`, `desc`/`summary`, `cmds`, `cmd`, `deps`, `vars`, `env`, `dir`, `aliases`, `silent`, `preconditions`, `status`, `sources` | yes (normal go-task inside a job) |
| `deps` entries | a task name or `{ task: name, vars: { K: "plain string" } }` |
| templated dep names (`deps: ['build-{{.X}}']`) | **no**: dep task names must be literal |
| non-string dep vars (maps, lists, `sh:`) | **no** |
| `includes:` | **no**: keep all tasks in the runner file |
| wildcard task names (`build-*`) as deps | **no**: use one task + `vars` instead |
| `cmds` entries | `- cmd string`, `- cmd: ...`, `- task: other` (runs inside the same job), `- defer: ...`, `- for: ...` |
| `requires: { vars: [...] }` | yes, and these become **run inputs** (§4) |
| dotenv, `interactive`, prompts | avoid: jobs are non-interactive (`--yes` is passed) |

## 3. Minimal example

```yaml
version: '3'

x-builder:
  entry: ci

tasks:
  ci:
    desc: Build and test
    deps: [build, lint]          # build and lint run in parallel, then ci
    cmds:
      - echo "all good for {{.BUILDER_COMMIT}}"

  build:
    cmds:
      - dotnet build -c Release

  lint:
    cmds:
      - npm ci
      - npm run lint
```

## 4. Variables and run inputs

Every job gets these environment variables (also usable as `{{.NAME}}` in go-task templates):

| Variable | Value |
|---|---|
| `BUILDER_BUILD_ID` | build id (GUID) |
| `BUILDER_BUILD_NUMBER` | build number of this runner (1, 2, …) |
| `BUILDER_PIPELINE` | runner name |
| `BUILDER_BRANCH` | branch being built |
| `BUILDER_COMMIT` | full commit SHA (use it for image tags) |
| `BUILDER_TASK` | task name of this job |
| `BUILDER_REQUESTED_BY` | who or what started the build (a user name, `push by …`, `PR !12 by …`, …) |

**Run inputs:**
- Variables entered in the Run dialog, or set by a trigger's `vars`, are passed to every job, both as environment
  variables and as go-task vars.
- Declare what a run must provide with go-task's `requires`. Builder shows these fields in the Run dialog and
  refuses to start a build that lacks them:

```yaml
tasks:
  deploy:
    requires:
      vars:
        - VERSION                                    # free text
        - { name: TARGET, enum: [staging, prod] }    # dropdown
```

## 5. Builder keys (`x-…`)

### 5.1 `x-builder` (top level)

```yaml
x-builder:
  entry: release                 # default entry task (else "default")
  triggers:
    push: [main, release/*]                          # or: true (any branch), or the full form below
    pull-request:                                    # alias: pr
      branches: [main]                               # target branches of the pull request
      paths: [src/**, .builder/**]                   # only when these paths changed
      vars: { CONFIGURATION: Debug }
    schedule:
      - { cron: "0 2 * * *", branch: main, timezone: Europe/Amsterdam, vars: { NIGHTLY: "1" } }
```

- **`push` and `pull-request`** accept `true` (all branches), `false`, a branch or list of branches (globs
  allowed), or `{ branches, paths, vars }`. They need the repository's webhook, which a Builder admin sets up
  once per repository.
- **`schedule`** is a list of `{ cron, branch, timezone?, vars? }`. `cron` takes 5 fields, or 6 with seconds;
  `timezone` is an IANA name and defaults to UTC.
- **Where triggers are read from:** the runner file on the repository's **default branch**, refreshed on pushes
  and hourly.

### 5.2 `x-agent`: where a job runs

```yaml
tasks:
  build-windows:
    x-agent: [windows, dotnet]            # or { labels: [windows, dotnet] }
```

A job runs only on an agent that has **all** the listed labels. Builder adds labels itself:
- `task` for jobs with commands;
- `ssh` or `kubectl` for deploy jobs;
- the labels configured on the deploy environment.

Leave `x-agent` out unless the job needs a specific machine.

### 5.3 `x-artifacts`: pass files to later jobs

```yaml
tasks:
  build:
    cmds: [dotnet publish -c Release -o out/app]
    x-artifacts: [out/app/**]             # globs relative to the repository root
  package:
    deps: [build]
    cmds: [tar czf app.tgz -C out/app .]  # out/app exists here: unpacked from build's artifact
```

- After a job succeeds, the files matching its globs are uploaded.
- Every job that depends on it, directly or transitively, gets them unpacked at the repository root before its
  commands run.
- The files can also be downloaded from the build page.

### 5.4 `x-approval`: wait for a person

```yaml
tasks:
  approve:
    deps: [images]
    x-approval: { message: "Deploy to production?", approvers: [alice@example.com] }
    # or simply:  x-approval: true
```

The job waits until someone approves it in the UI; rejecting fails the build. `approvers` is optional (empty means
any member) and holds Builder user names or e-mails. The message is shown as written (no `{{.VAR}}` templating). A deploy environment marked "requires approval" adds an approval to every job that deploys to it.

### 5.5 `x-secrets`: values entered in Builder's UI

```yaml
tasks:
  publish:
    x-secrets: [NUGET_API_KEY, SENTRY_TOKEN]
    cmds:
      - dotnet nuget push out/*.nupkg --api-key "$NUGET_API_KEY" --source https://api.nuget.org/v3/index.json
```

- **Names** are `UPPER_SNAKE_CASE`.
- **Values** are entered by a person under **Secrets** in Builder, either in the runner's project or shared by
  the organization. The project's own value wins over a shared one with the same name.
- **How a job gets them:** as environment variables (also `{{.NAME}}`), only in the tasks that declare them.
  They are masked in logs.
- **Missing secret:** the build fails before anything runs, naming the missing secret. Write `"$NAME"`, quoted,
  and never `echo` a secret.

### 5.6 `x-registries`: docker login for the job

```yaml
tasks:
  image:
    x-registries: [docker.io]                          # or myregistry.azurecr.io, ghcr.io, …
    # x-registries: [{ registry: docker.io, connection: "Docker Hub CI" }]   # pick a connection by name
    cmds:
      - docker build -t acme/web:{{.BUILDER_COMMIT}} .
      - docker push acme/web:{{.BUILDER_COMMIT}}
```

- Before the commands run, the agent runs `docker login` into a job-private docker config. The login disappears
  when the job ends.
- **Azure Container Registry** (`*.azurecr.io`): Builder exchanges an Azure service-principal token for a ~3 h
  registry token. This needs an **Azure** connection with AcrPush.
- **Any other registry** (Docker Hub, GHCR, …): Builder uses the **Container registry** connection whose host
  matches. `docker.io`, `index.docker.io` and `registry-1.docker.io` count as the same host.
- **Which connection:** the runner's project first, then a shared one.
- **Entries are hosts only**: no repository path, no tag.

### 5.7 `x-azure-artifacts`: private Azure DevOps feeds

```yaml
tasks:
  restore:
    x-azure-artifacts: true
    cmds: [dotnet restore]
```

The job gets a short-lived Azure DevOps token as `VSS_NUGET_ACCESSTOKEN` (used by the Azure Artifacts credential
provider for NuGet), `SYSTEM_ACCESSTOKEN` and `AZURE_DEVOPS_TOKEN` (for npm, pip or scripts). The repository must
be added through an Azure DevOps connection.

### 5.8 `x-deploy`: deploy to an environment

`environment` is the only required field. It names a deploy environment defined in Builder, in the runner's
project or shared: an **SSH + Docker** host or a **Kubernetes** cluster. The job's commands run first; then
Builder's built-in deploy runs if one is configured. The environment's credentials reach the job as:

| Variable | SSH + Docker | Kubernetes |
|---|---|---|
| `DEPLOY_ENVIRONMENT` | environment name | environment name |
| `DEPLOY_HOST`, `DEPLOY_PORT`, `DEPLOY_USER` | the host | – |
| `DEPLOY_SSH_KEY` | path of a temp private key | – |
| `DEPLOY_SSH` | ready-made `ssh … user@host` command prefix | – |
| `KUBECONFIG` | – | temp kubeconfig (also for AKS service principals) |
| `DEPLOY_NAMESPACE`, `DEPLOY_PROJECT`, `DEPLOY_URL` | when set in `x-deploy` | when set |

Pick **one** of these four styles.

**a) Your own commands.** No built-in deploy, just the credentials:

```yaml
  migrate:
    x-deploy: { environment: prod-vps }
    cmds:
      - $DEPLOY_SSH "docker run --rm --network app_net --env-file /opt/app/migrator.env acme/migrator:{{.BUILDER_COMMIT}} up"
```

**b) Docker Compose over SSH:**

```yaml
  deploy:
    x-deploy: { environment: prod-vps, compose: deploy/docker-compose.yml, project: shop, url: https://shop.example.com }
```

The compose file from the repository is copied to `~/builder/<project>/docker-compose.yml` on the host, then
pulled and started with `up -d`. Destroying the deployment runs `down`.

**c) Kubernetes manifests:**

```yaml
  deploy:
    x-deploy: { environment: aks-prod, manifests: k8s/, namespace: shop }
```

The namespace is created if missing, then `kubectl apply -n <namespace> -f <path>` runs (recursive for a
folder). Destroying the deployment deletes the namespace.

**d) One container, switched only when healthy** (SSH + Docker):

```yaml
  deploy-web:
    deps: [images]
    x-deploy:
      environment: prod-vps
      strategy: blue-green          # or: recreate
      service: web                  # network alias the proxy uses (e.g. Caddy → web:8080)
      image: acme/web:{{.BUILDER_COMMIT}}
      network: app_net              # docker network shared with the proxy (created if missing)
      env-file: /opt/app/env/web.env          # path ON THE HOST (optional)
      args: [-v, /opt/app/certs:/certs:ro, -e, ASPNETCORE_ENVIRONMENT=Production]   # extra docker run arguments
      command: []                   # optional: overrides the image's CMD
      health: { path: /health/ready, port: 8080, scheme: http, timeout: 180 }
      keep: 1                       # stopped previous containers kept for rollback (0-10)
```

Required fields are `strategy`, `service`, `image` and `network`; `{{.VAR}}` in any field is filled from the job's
variables. What each strategy does:

- **blue-green:**
  1. Start the new container next to the live one, under the alias `<service>-candidate`.
  2. Wait until it is healthy.
  3. Move the `<service>` alias to it, then stop the old container (it is kept, for rollback).
  4. If the new container never becomes healthy, it is removed and **the current one keeps serving**: the
     deploy fails and nothing changes for users.
- **recreate:** stop the old container first, then start the new one; if the new one fails, start the old one
  again. Use it for workers that must never run twice, such as queue consumers and schedulers.
- **Health**, in order of preference:
  - `health.path` + `health.port`: an HTTP(S) probe from inside the network expects 200. `scheme: https`
    accepts self-signed certificates.
  - Without `path`: the container's HEALTHCHECK (image or `--health-cmd` in `args`).
  - With neither: the container must still be running after 5 s.

  `timeout` is 5–1800 s and defaults to 120.
- **Podman hosts:** Podman drops `HEALTHCHECK` from images built in OCI format. Prefer an HTTP probe, or repeat
  the check in `args` (`--health-cmd`, `--health-interval`, `--health-retries`).
- **Rollback and destroy** are buttons on the deployment in Builder's UI. Rollback brings the kept previous
  container back after it passes the health check.

## 6. Rules for coding agents

1. **Location and format:** create `.builder/runners/<name>.yml` with `version: '3'` and a lowercase, hyphenated
   name (`ci.yml`, `deploy-prod.yml`). One runner per purpose: CI, release, nightly.
2. **Entry task:** set `x-builder.entry` and make that task depend on everything the run should do.
3. **Parallelism:** independent work goes in **parallel `deps`**; ordered work goes in **`cmds`** of one task.
   Don't chain tasks with `- task: x` in `cmds` when they could run in parallel.
4. **Jobs share nothing implicitly:**
   - files go through `x-artifacts`;
   - images go through a registry, tagged with `{{.BUILDER_COMMIT}}`, never only `latest`.
5. **No credentials in the YAML, ever.**
   - Secrets: `x-secrets` names.
   - Docker logins: `x-registries`.
   - Feeds: `x-azure-artifacts`.
   - Servers and clusters: `x-deploy.environment`.

   List in your pull request description which secrets, connections and environments a person must create in
   Builder, with their exact names.
6. **Plain task names in deps:** no templates in dep task names, no `includes:`, and dep `vars` as plain strings.
7. **Commands must be non-interactive and idempotent.** Assume a clean checkout with no tools beyond what the
   agent image provides. Install or restore dependencies in the job.
8. **Approval before production:** add an `x-approval` task, or rely on the environment's approval.
9. **Database migrations before app switches:** run them in their own job, after approval and before the deploy
   jobs. They must stay compatible with the version still running.
10. **Explain the graph:** add `desc:` to the tasks people will see; it is shown in the pipeline graph.
11. **Validate before opening the PR:**
    - `task --taskfile .builder/runners/<name>.yml --list-all` parses the file.
    - `task --taskfile .builder/runners/<name>.yml --dry <entry>` shows the commands.
    - Check that every dep name exists and that no `x-` key is misspelled. Unknown `x-` keys are ignored, not
      rejected.
12. **Hand-off:** open a pull request with the runner file. After it is merged, a Builder admin maps the file
    under Repository → Runners.

## 7. Complete example: build images, approve, migrate, deploy blue-green

```yaml
version: '3'

x-builder:
  entry: release
  triggers:
    push: [main]

vars:
  REPO: acme/shop

tasks:
  release:
    desc: Build, approve, migrate, deploy
    deps: [deploy-web, deploy-worker]

  image:
    desc: Build and push one image
    requires: { vars: [SERVICE, DOCKERFILE] }
    x-registries: [docker.io]
    cmds:
      - docker build -f {{.DOCKERFILE}} -t {{.REPO}}:{{.SERVICE}}-{{.BUILDER_COMMIT}} .
      - docker push {{.REPO}}:{{.SERVICE}}-{{.BUILDER_COMMIT}}

  images:
    deps:
      - { task: image, vars: { SERVICE: web, DOCKERFILE: Dockerfile.web } }
      - { task: image, vars: { SERVICE: worker, DOCKERFILE: Dockerfile.worker } }
      - { task: image, vars: { SERVICE: migrator, DOCKERFILE: Dockerfile.migrator } }

  approve:
    deps: [images]
    x-approval: { message: "Deploy to production?" }

  migrate:
    deps: [approve]
    x-deploy: { environment: prod-vps }
    cmds:
      - $DEPLOY_SSH "docker run --rm --network shop_net --env-file /opt/shop/env/migrator.env {{.REPO}}:migrator-{{.BUILDER_COMMIT}} up"

  deploy-web:
    deps: [migrate]
    x-deploy:
      environment: prod-vps
      strategy: blue-green
      service: web
      image: acme/shop:web-{{.BUILDER_COMMIT}}
      network: shop_net
      env-file: /opt/shop/env/web.env
      health: { path: /health, port: 8080, timeout: 120 }

  deploy-worker:
    deps: [deploy-web]
    x-deploy:
      environment: prod-vps
      strategy: recreate
      service: worker
      image: acme/shop:worker-{{.BUILDER_COMMIT}}
      network: shop_net
      env-file: /opt/shop/env/worker.env
```

To run this, a Builder admin needs to create:
- an SSH + Docker environment `prod-vps`;
- a Container registry connection for `docker.io` that can push to `acme/shop`.

## 8. Errors you may see when the build is planned

| Message | Fix |
|---|---|
| `The entry task 'x' does not exist` | set `x-builder.entry` or choose an existing task |
| `Taskfile 'includes:' are not supported` | inline the included tasks |
| `… is templated; Builder needs literal task names in deps` | use one task + `vars` |
| `Unknown deploy environment(s): …` | create the environment in Builder (project or shared) or fix the name |
| `Unknown secret(s): …` | add the secret in Builder or fix the name |
| `Registry …: add a Container registry connection for it` | create the connection, or name one with `{ registry, connection }` |
| `Missing or invalid variables: …` | provide the run inputs from `requires.vars` |
| `x-deploy.health.path needs health.port` | add `health.port` |
