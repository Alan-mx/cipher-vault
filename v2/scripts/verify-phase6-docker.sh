#!/usr/bin/env bash
set -euo pipefail

readonly repo_root="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)}"
readonly image="cipher-vault-phase6:${RANDOM}-$$"
readonly container="cipher-vault-phase6-${RANDOM}-$$"
readonly volume="cipher-vault-phase6-${RANDOM}-$$"
readonly port="${PHASE6_PORT:-$((44200 + (RANDOM % 1000)))}"
readonly password="phase6-docker-password"
temporary_dir="$(mktemp -d)"
cookie_jar="${temporary_dir}/cookies.txt"

cleanup() {
    docker rm -f "$container" >/dev/null 2>&1 || true
    docker volume rm "$volume" >/dev/null 2>&1 || true
    rm -rf "$temporary_dir"
}
trap cleanup EXIT

if [[ ! -f "${repo_root}/v2/src/CipherVault.Server/wwwroot/index.html" ]]; then
    echo "Frontend build is missing. Run npm run build first." >&2
    exit 1
fi

docker build \
    -f "${repo_root}/v2/tests/CipherVault.Server.Tests/Containers/Dockerfile.phase5" \
    -t "$image" \
    "$repo_root"
docker volume create "$volume" >/dev/null

start_container() {
    docker run -d \
        --name "$container" \
        -p "127.0.0.1:${port}:43127" \
        -v "${volume}:/data" \
        "$image" >/dev/null
}

wait_for_status() {
    for _ in $(seq 1 60); do
        if curl -fsS "http://127.0.0.1:${port}/api/vault/status" \
            >"${temporary_dir}/status.json"; then
            return 0
        fi
        sleep 1
    done
    docker logs "$container" >&2 || true
    echo "CipherVault Docker container did not become ready." >&2
    exit 1
}

start_container
wait_for_status
bound_address="$(docker port "$container" 43127/tcp | head -n1)"
if [[ "$bound_address" != "127.0.0.1:${port}" ]]; then
    echo "Unexpected Docker host binding: $bound_address" >&2
    exit 1
fi

curl -fsS "http://127.0.0.1:${port}/" \
    >"${temporary_dir}/index.html"
grep -q '<div id="app"></div>' "${temporary_dir}/index.html"
asset_path="$(
    grep -o '/assets/[^"]*\.js' "${temporary_dir}/index.html" | head -n1
)"
test -n "$asset_path"
curl -fsS "http://127.0.0.1:${port}${asset_path}" \
    >"${temporary_dir}/app.js"
test -s "${temporary_dir}/app.js"

docker rm -f "$container" >/dev/null
start_container
wait_for_status
curl -fsS "http://127.0.0.1:${port}/" \
    | grep -q '<div id="app"></div>'

echo "Phase 6 Docker verification passed."
echo "Host binding: $bound_address"
echo "Built SPA and hashed assets are served from the container."
