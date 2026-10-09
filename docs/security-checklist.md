# Security checklist

Living list of security decisions and open work. ✅ done · ⬜ open · ⚠️ accepted risk (decided, revisit later).

## Decided for now

- ⚠️ **Container builds use the host's Podman socket** (2026-10-09). The agent container mounts the server's
  Podman (or Docker) socket so jobs can `docker build/run/push`. Whoever can use that socket controls the
  containers of that user: with **rootless Podman** (recommended) that is everything the daemon's user owns, with
  Docker's root socket it is root on the host. Jobs from one repository can affect containers of other jobs on the
  same server.
  - Mitigation today: run daemons with rootless Podman; only run trusted repositories on a socket-enabled server.
  - ⬜ Later: isolated builds without the socket: Podman/Buildah **inside** the agent container (rootless, needs
    `/dev/fuse` and user-namespace settings; slower), selectable per agent (label `isolated-builds`).
  - ⬜ Later: trust levels via agent labels (e.g. pull-request builds only on `untrusted` agents without socket).

## Credentials and secrets

- ✅ Secrets are encrypted at rest (ASP.NET Data Protection) and write-only through the API/UI.
- ✅ The daemon fetches a job's secrets and credentials at run time; only the agent the job is assigned to, only
  while the job runs, only the names the job declared.
- ✅ Nothing on command lines: secrets are environment variables, git auth uses `GIT_CONFIG_*` env (agents and server).
- ✅ Per-job sandbox (`HOME`, `DOCKER_CONFIG`, `AZURE_CONFIG_DIR`, `TMPDIR`…) deleted after each job; crash
  leftovers swept on daemon start; checkout deleted when the build finishes.
- ✅ Short-lived credentials with an Entra service principal: Azure DevOps ~1 h, ACR ~3 h.
- ⚠️ PAT connections still hand the PAT to the daemon for the job (in memory + job env). Prefer service principals.
- ⚠️ Log masking only covers values of ≥ 4 characters, and only exact matches (a task that base64-encodes or
  splits a secret can still print it).
- ✅ Deploy credentials (SSH key, kubeconfig, AKS client secret) are no longer pushed: the deploying agent gets
  them with the job's credentials, the tearing-down agent via `GetTeardownCredentials` (only the agent asked, only
  while the deployment is being destroyed). Job and teardown messages carry no credentials at all.
- ⬜ AKS with a service principal runs `az login` on the daemon; mint the kubeconfig server-side instead
  (ARM `listClusterUserCredential`) so daemons need neither `az` nor the client secret.
- ⬜ Rotate the Data Protection key ring and document backup of `data/keys` (losing it makes every stored
  secret unreadable).
- ⬜ Secret scoping beyond the organization (per runner / per environment / branch-protected secrets, e.g.
  production secrets only on the default branch).

## Agents

- ✅ Each organization has its own agent token (SHA-256 stored, shown once, regenerable); agents dial out over
  HTTPS through the public endpoint, no inbound ports.
- ⚠️ **Shared agents** (system token) run jobs of every organization: one organization's job can see another's
  leftovers if the sandbox cleanup failed, and shares the container socket. Use shared agents only for trusted
  tenants, or not at all in multi-tenant setups.
- ⬜ Per-agent tokens (instead of one token per organization) so a single server can be revoked without
  re-enrolling the others.
- ⬜ SSH deploys trust the host key on first use (`StrictHostKeyChecking=accept-new`, per-agent `known_hosts`).
  Store the expected host key with the environment and pin it.

## Users and access

- ✅ BFF pattern: the browser holds only an HttpOnly, SameSite=Strict session cookie; the API trusts 5-minute JWTs
  from the BFF; every mutating `/api` call needs `X-CSRF: 1`.
- ✅ Organization isolation: EF global query filters + membership check per request; non-members get 404.
  Background work scopes by the build's organization explicitly.
- ✅ Google sign-in only for Google-verified e-mails that belong to a predefined or invited user.
- ✅ Change-password screen (user menu; 12+ characters) and `Auth:PasswordSignIn=false` to turn password
  sign-in off (BFF hides and refuses it, API refuses it) once Google sign-in works. The API logs a warning at
  start while the bootstrap admin still has the default password.
- ⬜ **Change the bootstrap `admin`/`admin` password** on every instance you expose (the tailnet dev instance too).
- ⬜ Audit log (who ran/approved/canceled what, secret and member changes).
- ⬜ Rate limiting on `/bff/login` and the agent hub.
- ⬜ Session revocation when a member is removed (today the cookie stays valid up to 12 h, though API calls
  for that organization fail immediately).

## Platform

- ⬜ TLS everywhere in the cloud deployment (BFF behind a TLS proxy with `Auth:PublicOrigin`; API not exposed
  except through the BFF).
- ⬜ The API runs as a single instance (in-process build lock); scale-out needs a distributed lock.
- ⬜ Log and artifact retention policy (logs may contain unmasked derived secrets; artifacts may contain private code).
- ⬜ Dependency and image scanning in CI (agent image ships git, ssh, kubectl, docker CLI).
