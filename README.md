# Builder

Self-hosted distributed build & deploy system: .NET 10 server + agents, Vue 3 UI,
pipelines written as [go-task](https://taskfile.dev) Taskfiles.

- Checkout from Azure DevOps Git (or any git URL)
- Taskfile `deps` run in parallel across agents, `cmds` run in sequence
- Approval gates (`x-approval`) and per-environment approvals
- Deploy to a VPS over SSH with Docker Compose, or to Kubernetes / AKS
- Live UI: builds, logs, agent CPU / RAM / disk, cancel, re-run, clean up
- Clean Architecture (.NET 10), PostgreSQL, BFF in front of the Vue SPA

See [docs/design.md](docs/design.md) for the architecture and the Taskfile extensions,
and [samples/Taskfile.yml](samples/Taskfile.yml) for an example pipeline.

> Status: under construction.
