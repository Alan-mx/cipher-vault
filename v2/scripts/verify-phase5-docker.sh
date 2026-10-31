#!/usr/bin/env bash
set -euo pipefail

readonly repo_root="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)}"
readonly image="cipher-vault-phase5:${RANDOM}-$$"
readonly container="cipher-vault-phase5-${RANDOM}-$$"
readonly volume="cipher-vault-phase5-${RANDOM}-$$"
readonly port="${PHASE5_PORT:-$((43127 + (RANDOM % 1000)))}"
readonly password="phase5-docker-password"
temporary_dir="$(mktemp -d)"
cookie_jar="${temporary_dir}/cookies.txt"

cleanup() {
    docker rm -f "$container" >/dev/null 2>&1 || true
    docker volume rm "$volume" >/dev/null 2>&1 || true
    rm -rf "$temporary_dir"
}
trap cleanup EXIT

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
    for attempt in $(seq 1 60); do
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

curl -fsS \
    -H 'Content-Type: application/json' \
    -d "{\"password\":\"${password}\",\"riskAccepted\":true}" \
    "http://127.0.0.1:${port}/api/vault/setup" \
    >"${temporary_dir}/setup.json"
curl -fsS \
    -c "$cookie_jar" \
    -H 'Content-Type: application/json' \
    -d "{\"password\":\"${password}\"}" \
    "http://127.0.0.1:${port}/api/vault/unlock" \
    >"${temporary_dir}/unlock.json"
curl -fsS -b "$cookie_jar" -c "$cookie_jar" \
    "http://127.0.0.1:${port}/api/vault/antiforgery" \
    >"${temporary_dir}/antiforgery.json"
csrf="$(
    python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["token"])' \
        "${temporary_dir}/antiforgery.json"
)"
curl -fsS -b "$cookie_jar" -c "$cookie_jar" -X POST \
    -H "X-CSRF-TOKEN: ${csrf}" \
    "http://127.0.0.1:${port}/api/mcp/token/rotate" \
    >"${temporary_dir}/token.json"
token="$(
    python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["token"])' \
        "${temporary_dir}/token.json"
)"

mcp_initialize() {
    curl -fsS \
        -H "Authorization: Bearer ${token}" \
        -H 'Content-Type: application/json' \
        -H 'Accept: application/json, text/event-stream' \
        -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"phase5-docker","version":"1.0"}}}' \
        "http://127.0.0.1:${port}/mcp" \
        >"${temporary_dir}/mcp.json"
}
mcp_initialize
grep -q 'serverInfo' "${temporary_dir}/mcp.json"

docker rm -f "$container" >/dev/null
start_container
wait_for_status
mcp_initialize
grep -q 'serverInfo' "${temporary_dir}/mcp.json"

echo "Phase 5 Docker verification passed."
echo "Host binding: $bound_address"
echo "Persistent token survived container recreation."
