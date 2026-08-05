#!/bin/bash
# Build and smoke the four application images under the same least-privilege
# controls required by Compose. Creates no volumes and always removes its
# temporary containers and network.
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"

# Load only the committed development JWT contract. Do not source the whole
# env template or print values; the private key is forwarded only to Backend.
jwt_setting_count=0
while IFS='=' read -r name value; do
  case "$name" in
    JWT_ISSUER|JWT_AUDIENCE|JWT_ACTIVE_KID|JWT_PRIVATE_KEY_PEM_BASE64|JWT_PUBLIC_KEY_RING_JSON)
      if [[ "$value" == \'*\' || "$value" == \"*\" ]]; then
        value="${value:1:${#value}-2}"
      fi
      export "$name=$value"
      jwt_setting_count=$((jwt_setting_count + 1))
      ;;
  esac
done < infra/.env.example
[ "$jwt_setting_count" -eq 5 ] || { echo "Development JWT contract is incomplete" >&2; exit 1; }

docker build -t springaitest-ci-platform ./platform
docker build -t springaitest-ci-backend ./backend
docker build -t springaitest-ci-workflow ./workflow
docker build -t springaitest-ci-frontend ./frontend

images=(
  springaitest-ci-platform
  springaitest-ci-backend
  springaitest-ci-workflow
  springaitest-ci-frontend
)
for image in "${images[@]}"; do
  user="$(docker image inspect --format '{{.Config.User}}' "$image")"
  case "$user" in
    ''|0|0:*|root|root:*)
      echo "$image has an unsafe runtime user: ${user:-<empty>}" >&2
      exit 1
      ;;
  esac
done

run_id="${GITHUB_RUN_ID:-local}-$(date +%s)-$$"
prefix="hardening-$run_id"
network="$prefix"
containers=()
cleanup() {
  if [ ${#containers[@]} -gt 0 ]; then
    docker rm -f "${containers[@]}" >/dev/null 2>&1 || true
  fi
  docker network rm "$network" >/dev/null 2>&1 || true
}
trap cleanup EXIT
docker network create "$network" >/dev/null

common=(
  --detach
  --read-only
  --tmpfs /tmp:rw,noexec,nosuid,nodev,size=64m
  --cap-drop ALL
  --security-opt no-new-privileges
  --network "$network"
)

backend="$prefix-backend"
containers+=("$backend")
docker run "${common[@]}" --name "$backend" --network-alias backend \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e DB_PROVIDER=inmemory \
  -e EMBEDDINGS_PROVIDER=fake \
  -e INTERNAL_API_TOKEN=ci-internal-token \
  -e JWT_ISSUER -e JWT_AUDIENCE -e JWT_ACTIVE_KID -e JWT_PRIVATE_KEY_PEM_BASE64 \
  -e RABBITMQ_URL=amqp://guest:guest@127.0.0.1:1 \
  -p 127.0.0.1::8080 springaitest-ci-backend >/dev/null

workflow="$prefix-workflow"
containers+=("$workflow")
docker run "${common[@]}" --name "$workflow" --network-alias workflow \
  -e APP_ENVIRONMENT=Development \
  -e LLM_MODEL=mock-gpt \
  -e LANGFUSE_ENABLED=false \
  -e BACKEND_BASE_URL=http://backend:8080 \
  -e INTERNAL_API_TOKEN=ci-internal-token \
  -p 127.0.0.1::8000 springaitest-ci-workflow >/dev/null

platform="$prefix-platform"
containers+=("$platform")
docker run "${common[@]}" --name "$platform" --network-alias platform \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e MEM0_MODE=inmemory \
  -e OTEL_MODE=console \
  -e CHAT_MODEL=mock-gpt \
  -e BACKEND_BASE_URL=http://backend:8080 \
  -e WORKFLOW_BASE_URL=http://workflow:8000 \
  -e INTERNAL_API_TOKEN=ci-internal-token \
  -e JWT_ISSUER -e JWT_AUDIENCE -e JWT_PUBLIC_KEY_RING_JSON \
  -e RABBITMQ_URL=amqp://guest:guest@127.0.0.1:1 \
  -p 127.0.0.1::8080 springaitest-ci-platform >/dev/null

frontend="$prefix-frontend"
containers+=("$frontend")
docker run "${common[@]}" --name "$frontend" \
  -p 127.0.0.1::8080 springaitest-ci-frontend >/dev/null

port_of() {
  docker port "$1" "$2/tcp" | awk -F: 'NR == 1 { print $NF }'
}
wait_http() {
  local name="$1" port="$2" path="$3"
  for _ in $(seq 1 60); do
    if curl --fail --silent --show-error --max-time 2 "http://127.0.0.1:$port$path" >/dev/null; then
      return 0
    fi
    sleep 1
  done
  echo "$name did not become healthy" >&2
  docker logs "$name" >&2 || true
  return 1
}

wait_http "$backend" "$(port_of "$backend" 8080)" /health
wait_http "$workflow" "$(port_of "$workflow" 8000)" /health
wait_http "$platform" "$(port_of "$platform" 8080)" /actuator/health
wait_http "$frontend" "$(port_of "$frontend" 8080)" /

for container in "${containers[@]}"; do
  [ "$(docker inspect --format '{{.HostConfig.ReadonlyRootfs}}' "$container")" = true ]
  docker inspect --format '{{json .HostConfig.CapDrop}}' "$container" | grep -q 'ALL'
  docker inspect --format '{{json .HostConfig.SecurityOpt}}' "$container" | grep -q 'no-new-privileges'
  docker inspect --format '{{json .HostConfig.Tmpfs}}' "$container" | grep -q '"/tmp"'
  docker exec "$container" sh -c '[ "$(id -u)" -ne 0 ]'
done

echo "Container hardening smoke passed for platform, backend, workflow, and frontend."
