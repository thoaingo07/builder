# Builder: run one container behind a network alias on this host, switching traffic only once it is healthy.
# Sent over SSH (`bash -s`) by the agent, preceded by the inputs as shell variables:
#   MODE (deploy|rollback|destroy) STRATEGY (BlueGreen|Recreate) SERVICE IMAGE NETWORK ENV_FILE ARGS (array) CMD (array)
#   HEALTH_PATH HEALTH_PORT HEALTH_SCHEME TIMEOUT KEEP BUILD
# Traffic reaches the live container through the alias $SERVICE on $NETWORK (e.g. Caddy → portal-web:8006).
# State (which container is live / kept for rollback) is in ~/.builder/containers/$SERVICE. Needs only the docker
# CLI (Docker, or Podman's docker-compatible CLI).
set -euo pipefail

STATE_DIR="$HOME/.builder/containers"
mkdir -p "$STATE_DIR"
STATE="$STATE_DIR/$SERVICE"
PROBE_IMAGE="docker.io/curlimages/curl:8.10.1"
active=""
previous=""
[ -f "$STATE" ] && . "$STATE"

log() { echo "[deploy] $*"; }
save() { printf 'active=%q\nprevious=%q\n' "$1" "$2" > "$STATE"; }
exists() { docker inspect "$1" >/dev/null 2>&1; }
status_of() { docker inspect -f '{{.State.Status}}' "$1" 2>/dev/null || echo gone; }

aliases_of() {
  docker inspect -f "{{range \$k, \$v := .NetworkSettings.Networks}}{{if eq \$k \"$NETWORK\"}}{{range \$v.Aliases}}{{.}} {{end}}{{end}}{{end}}" "$1" 2>/dev/null
}

# running containers that answer to the alias today (on the first deploy: the compose-owned one)
holders() {
  for id in $(docker ps -q --filter "network=$NETWORK"); do
    if aliases_of "$id" | tr ' ' '\n' | grep -qx "$SERVICE"; then
      docker inspect -f '{{.Name}}' "$id" | sed 's#^/##'
    fi
  done
}

# connect $1 to the network under the service alias (reconnecting if it is attached without it)
take_alias() {
  if aliases_of "$1" | tr ' ' '\n' | grep -qx "$SERVICE"; then return 0; fi
  docker network disconnect "$NETWORK" "$1" >/dev/null 2>&1 || true
  docker network connect --alias "$SERVICE" "$NETWORK" "$1"
}

release() {  # stop serving: leave the network alias, then stop (kept for rollback)
  docker network disconnect "$NETWORK" "$1" >/dev/null 2>&1 || true
  docker stop -t 20 "$1" >/dev/null 2>&1 || true
}

wait_healthy() {
  local name=$1 deadline=$((SECONDS + TIMEOUT)) health code
  log "waiting up to ${TIMEOUT}s for $name to be healthy"
  while [ "$SECONDS" -lt "$deadline" ]; do
    if [ "$(status_of "$name")" != running ]; then log "$name is $(status_of "$name")"; return 1; fi
    if [ -n "${HEALTH_PATH:-}" ]; then
      code=$(docker run --rm --network "$NETWORK" "$PROBE_IMAGE" -ks -o /dev/null -w '%{http_code}' --max-time 5 \
        "${HEALTH_SCHEME:-http}://$name:$HEALTH_PORT$HEALTH_PATH" 2>/dev/null || true)
      if [ "$code" = 200 ]; then log "$name answers 200 on $HEALTH_PATH"; return 0; fi
    else
      health=$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$name")
      case "$health" in
        healthy) log "$name is healthy"; return 0 ;;
        unhealthy) log "$name is unhealthy"; return 1 ;;
        none) sleep 5; [ "$(status_of "$name")" = running ] && { log "$name has no health check; it is running"; return 0; }; return 1 ;;
      esac
    fi
    sleep 2
  done
  log "$name did not become healthy in ${TIMEOUT}s"
  return 1
}

fail_candidate() {
  log "keeping the current container; last log lines of $1:"
  docker logs --tail 80 "$1" 2>&1 | sed 's/^/[candidate] /' || true
  docker rm -f "$1" >/dev/null 2>&1 || true
}

prune() {  # keep $KEEP stopped Builder containers of this service besides the live one
  local keep_list
  keep_list=$(docker ps -a --filter "label=builder.service=$SERVICE" --filter status=exited --format '{{.CreatedAt}}|{{.Names}}' \
    | sort -r | cut -d'|' -f2 | grep -vx "$1" || true)
  echo "$keep_list" | tail -n +"$((KEEP + 1))" | while read -r old; do
    [ -n "$old" ] && [ "$old" != "$2" ] && { log "removing old container $old"; docker rm -f "$old" >/dev/null; }
  done
  true
}

case "$MODE" in
deploy)
  docker network inspect "$NETWORK" >/dev/null 2>&1 || { log "creating network $NETWORK"; docker network create "$NETWORK" >/dev/null; }
  log "pulling $IMAGE"
  docker pull -q "$IMAGE" >/dev/null || log "pull failed; trying the local image"
  current=$(holders | head -1)
  new="$SERVICE-b$BUILD-$(date +%s)"
  run=(docker run -d --name "$new" --network "$NETWORK" --restart unless-stopped
       --label "builder.service=$SERVICE" --label "builder.build=$BUILD")
  [ -n "${ENV_FILE:-}" ] && run+=(--env-file "$ENV_FILE")

  if [ "$STRATEGY" = Recreate ]; then
    # one at a time (e.g. background workers that must never run twice): stop first, start back on failure
    if [ -n "$current" ]; then log "stopping $current first"; release "$current"; fi
    "${run[@]}" --network-alias "$SERVICE" "${ARGS[@]}" "$IMAGE" "${CMD[@]}" >/dev/null
    log "started $new"
    if ! wait_healthy "$new"; then
      fail_candidate "$new"
      if [ -n "$current" ]; then log "starting $current again"; docker start "$current" >/dev/null; take_alias "$current"; fi
      exit 1
    fi
  else
    "${run[@]}" --network-alias "$SERVICE-candidate" "${ARGS[@]}" "$IMAGE" "${CMD[@]}" >/dev/null
    log "started $new next to ${current:-nothing}"
    if ! wait_healthy "$new"; then fail_candidate "$new"; exit 1; fi
    log "switching $SERVICE to $new"
    take_alias "$new"
    sleep 2   # both answer briefly, then the old one leaves
    for old in $(holders); do [ "$old" != "$new" ] && release "$old"; done
  fi
  save "$new" "${current:-}"
  prune "$new" "${current:-}"
  echo "BUILDER-RESULT active=$new previous=${current:-}"
  ;;

rollback)
  [ -n "$previous" ] && exists "$previous" || { log "no previous container to roll back to"; exit 1; }
  log "rolling back $SERVICE from ${active:-?} to $previous"
  docker start "$previous" >/dev/null
  if ! wait_healthy "$previous"; then log "$previous did not come back healthy; leaving ${active:-the current one} live"; docker stop "$previous" >/dev/null; exit 1; fi
  take_alias "$previous"
  sleep 2
  [ -n "$active" ] && exists "$active" && release "$active"
  save "$previous" "$active"
  echo "BUILDER-RESULT active=$previous previous=$active"
  ;;

destroy)
  for c in $(docker ps -aq --filter "label=builder.service=$SERVICE"); do docker rm -f "$c" >/dev/null; done
  rm -f "$STATE"
  log "removed Builder's containers of $SERVICE"
  ;;

*) echo "unknown MODE $MODE" >&2; exit 2 ;;
esac
