# Dev host (ulab-ser8): containers on rootless Podman

On 2026-10-09 every container on this machine moved from Docker to **rootless Podman**
(Podman 4.9.3, podman-compose 1.0.6, user socket `/run/user/1000/podman/podman.sock`).
The Docker engine is still installed but empty.

## What runs where

| Container | Purpose | Ports | Started by |
|---|---|---|---|
| `blf-dev-sqlserver`, `blf-dev-{admin,portal,blf,nt}-web` | BLF dev stack (tailnet URLs :18002–:18007) | 14330, 8002–8007 | Docker Compose v2 through the Podman socket (below) |
| `builder-dev_postgres_1` | Builder dev database | 15433 | `task db:up` (podman-compose) |
| `agentd-demo-pg` | agentd demo database (agentd daemon on :7796 / tailnet :18008) | 55441 | `podman start agentd-demo-pg` |

BLF must be driven by Docker Compose v2 against the Podman socket; podman-compose 1.0.6 ignores
`--no-deps` and `depends_on` conditions:

```bash
cd ~/tngo/tngo-repos/BLF
DOCKER_HOST=unix:///run/user/1000/podman/podman.sock \
  docker compose -p blf-dev -f docker-compose.dev.yml --env-file .env up -d --no-deps --no-build \
  sqlserver admin-web portal-web blf-web nt-web
```

The BLF network `blf-dev_blf` was created by hand on **172.30.0.0/16** with compose labels. It must stay
inside `172.16.0.0/12` (the apps' `ForwardedHeaders__KnownNetworks`), otherwise cookies behind
`tailscale serve` lose their `Secure` flag.

## ⚠️ Pending: start containers after a reboot

Rootless Podman containers do **not** come back after a reboot yet (Docker's daemon used to do it).
Not applied on 2026-10-09 because the machine was not to be restarted and the change needs the owner's
approval. Before the next reboot, run once:

```bash
mkdir -p ~/.config/systemd/user
cat > ~/.config/systemd/user/podman-dev-stack.service <<'EOF'
[Unit]
Description=Dev containers on Podman
After=network-online.target podman.socket

[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/usr/bin/podman start blf-dev-sqlserver builder-dev_postgres_1 agentd-demo-pg
ExecStart=/bin/sleep 15
ExecStart=/usr/bin/podman start blf-dev-admin-web blf-dev-portal-web blf-dev-blf-web blf-dev-nt-web

[Install]
WantedBy=default.target
EOF
systemctl --user daemon-reload
systemctl --user enable podman-dev-stack.service
sudo loginctl enable-linger ulab      # user services start at boot without a login
```

(Drop `agentd-demo-pg` from the first line if it should stay manual, as it was under Docker.)
After the next reboot, check: `podman ps` shows all seven containers and the tailnet URLs answer.

## Backups from the move

`~/tngo/backups/2026-10-09-docker-to-podman/` (mode 700): `sqlserver/CART_DEV.bak`, `postgres/*.sql`,
every former Docker volume as `volumes/*.tar.gz`, the row-count baselines, `verify-podman/`, `SHA256SUMS`.
Restores were verified: identical row counts for every table, `DBCC CHECKDB` clean, identical upload checksums.

## Still open

- Uninstalling the Docker engine (then set `DOCKER_HOST` permanently for `docker compose`, and Aspire uses
  `ASPIRE_CONTAINER_RUNTIME=podman`, which the Builder AppHost and tests already set).
- `admin-jobs` and the BLF mail sink (`mailpit`, `socat`) were stopped before the move and were not recreated;
  their images are in Podman.
