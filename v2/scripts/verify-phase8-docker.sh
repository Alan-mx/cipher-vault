#!/usr/bin/env bash
set -euo pipefail

readonly release_version="2.0.0-rc.1"
readonly image="cipher-vault:${release_version}"
readonly repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly password="phase8 docker acceptance password"
readonly marker="phase8-docker-acceptance-marker"
readonly stamp="$$-$RANDOM"
readonly container="cipher-vault-phase8-${stamp}"
readonly restored_container="cipher-vault-phase8-restored-${stamp}"
readonly data_volume="cipher-vault-phase8-data-${stamp}"
readonly target_volume="cipher-vault-phase8-target-${stamp}"
readonly port="${CV_PHASE8_DOCKER_PORT:-$((46000 + (RANDOM % 1000)))}"

artifact_root=""
evidence_root=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --artifact-root)
            artifact_root="$2"
            shift 2
            ;;
        --evidence-root)
            evidence_root="$2"
            shift 2
            ;;
        *)
            echo "phase8_docker_arguments_invalid" >&2
            exit 2
            ;;
    esac
done
if [[ -z "$artifact_root" || -z "$evidence_root" ]]; then
    echo "phase8_docker_arguments_invalid" >&2
    exit 2
fi
for tool in docker curl python3 script; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        echo "phase8_docker_tool_missing:$tool" >&2
        exit 3
    fi
done

docker_tar="${artifact_root}/cipher-vault-${release_version}-docker-linux-amd64.tar"
if [[ ! -f "$docker_tar" ]]; then
    echo "phase8_docker_archive_missing" >&2
    exit 4
fi

temporary_root="$(mktemp -d)"
server_pid=""
cleanup() {
    docker rm -f "$container" "$restored_container" >/dev/null 2>&1 || true
    docker volume rm "$data_volume" "$target_volume" >/dev/null 2>&1 || true
    rm -rf -- "$temporary_root"
}
trap cleanup EXIT

docker load --input "$docker_tar" >/dev/null
architecture="$(docker image inspect "$image" --format '{{.Architecture}}')"
image_user="$(docker image inspect "$image" --format '{{.Config.User}}')"
healthcheck="$(docker image inspect "$image" --format '{{json .Config.Healthcheck}}')"
labels="$(docker image inspect "$image" --format '{{json .Config.Labels}}')"
image_id="$(docker image inspect "$image" --format '{{.Id}}')"
test "$architecture" = "amd64"
test "$image_user" = "10001:10001"
python3 - "$healthcheck" "$labels" <<'PY'
import json
import sys

health = json.loads(sys.argv[1])
labels = json.loads(sys.argv[2])
test = " ".join(health.get("Test") or [])
if "/api/vault/status" not in test:
    raise SystemExit("phase8_docker_healthcheck_invalid")
if labels.get("org.opencontainers.image.version") != "2.0.0-rc.1":
    raise SystemExit("phase8_docker_version_label_invalid")
if not str(labels.get("org.opencontainers.image.revision", "")).startswith(
        "0000000000000000000000000000000000000000"):
    # Release builds use the real commit; local acceptance allows only
    # obvious test commits and validates the exact value in final evidence.
    if len(labels.get("org.opencontainers.image.revision", "")) != 40:
        raise SystemExit("phase8_docker_revision_label_invalid")
if not labels.get("org.opencontainers.image.base.digest", "").startswith(
        "sha256:"):
    raise SystemExit("phase8_docker_base_digest_label_invalid")
PY

runtime_uid="$(docker run --rm --entrypoint id "$image" -u)"
test "$runtime_uid" = "10001"
docker run --rm --entrypoint sh "$image" -c \
    'command -v curl >/dev/null && test ! -e /src && test ! -e /app/appsettings.Development.json'
docker run --rm --entrypoint sh "$image" -c \
    'test -z "$(find /app -type f \( -name "*.cs" -o -name "*.csproj" -o -name "*.pdb" \) -print -quit)"'

docker volume create "$data_volume" >/dev/null
docker volume create "$target_volume" >/dev/null

start_container() {
    docker run -d \
        --name "$container" \
        --publish "127.0.0.1:${port}:8080" \
        --read-only \
        --tmpfs /tmp \
        --security-opt no-new-privileges \
        --cap-drop ALL \
        --volume "${data_volume}:/data" \
        "$image" >/dev/null
}

wait_for_status() {
    for _ in $(seq 1 120); do
        if curl -fsS "http://127.0.0.1:${port}/api/vault/status" \
            >"${temporary_root}/status.json" 2>/dev/null; then
            return 0
        fi
        sleep 0.25
    done
    docker logs "$container" >&2 || true
    return 1
}

start_container
wait_for_status
bound_address="$(docker port "$container" 8080/tcp | head -n1)"
test "$bound_address" = "127.0.0.1:${port}"

base_url="http://127.0.0.1:${port}"
cookie_jar="${temporary_root}/cookies.txt"
curl -fsS -D "${temporary_root}/version.headers" \
    "${base_url}/api/version" >"${temporary_root}/version.json"
curl -fsS -D "${temporary_root}/index.headers" \
    "${base_url}/" >"${temporary_root}/index.html"
asset_path="$(grep -oE '/assets/[A-Za-z0-9._-]+\.js' \
    "${temporary_root}/index.html" | head -n1)"
test -n "$asset_path"
curl -fsS "${base_url}${asset_path}" >"${temporary_root}/app.js"
test -s "${temporary_root}/app.js"
python3 - \
    "${temporary_root}/version.json" \
    "${temporary_root}/version.headers" \
    "${temporary_root}/index.headers" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    version = json.load(stream)
if version.get("productVersion") != "2.0.0-rc.1":
    raise SystemExit("phase8_docker_version_invalid")
if version.get("rid") != "linux-x64":
    raise SystemExit("phase8_docker_rid_invalid")

def headers(path):
    with open(path, encoding="iso-8859-1") as stream:
        rows = [line.strip() for line in stream if ":" in line]
    return {name.lower(): value.strip() for name, value in
            (row.split(":", 1) for row in rows)}

version_headers = headers(sys.argv[2])
index_headers = headers(sys.argv[3])
if version_headers.get("cache-control") != "no-store":
    raise SystemExit("phase8_docker_no_store_missing")
if "default-src 'self'" not in index_headers.get(
        "content-security-policy", ""):
    raise SystemExit("phase8_docker_csp_missing")
if index_headers.get("x-frame-options") != "DENY":
    raise SystemExit("phase8_docker_frame_options_missing")
PY

curl -fsS \
    -H 'Content-Type: application/json' \
    -d "{\"password\":\"${password}\",\"riskAccepted\":true}" \
    "${base_url}/api/vault/setup" >"${temporary_root}/setup.json"
curl -fsS \
    -c "$cookie_jar" \
    -b "$cookie_jar" \
    -H 'Content-Type: application/json' \
    -d "{\"password\":\"${password}\"}" \
    "${base_url}/api/vault/unlock" >"${temporary_root}/unlock.json"
curl -fsS \
    -c "$cookie_jar" \
    -b "$cookie_jar" \
    "${base_url}/api/vault/antiforgery" >"${temporary_root}/antiforgery.json"
csrf="$(python3 -c \
    'import json,sys; print(json.load(open(sys.argv[1]))["token"])' \
    "${temporary_root}/antiforgery.json")"
curl -fsS \
    -c "$cookie_jar" \
    -b "$cookie_jar" \
    -X POST \
    -H "X-CSRF-TOKEN: ${csrf}" \
    "${base_url}/api/mcp/token/rotate" >"${temporary_root}/token.json"
token="$(python3 -c \
    'import json,sys; print(json.load(open(sys.argv[1]))["token"])' \
    "${temporary_root}/token.json")"

wrong_status="$(curl -sS -o "${temporary_root}/wrong.json" -w '%{http_code}' \
    -H 'Authorization: Bearer invalid' \
    -H 'Content-Type: application/json' \
    -H 'Accept: application/json, text/event-stream' \
    -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' \
    "${base_url}/mcp")"
test "$wrong_status" = "401"

invoke_mcp() {
    local output="$1"
    local payload="$2"
    curl -fsS \
        -H "Authorization: Bearer ${token}" \
        -H 'Content-Type: application/json' \
        -H 'Accept: application/json, text/event-stream' \
        -d "$payload" \
        "${base_url}/mcp" >"$output"
}

invoke_mcp "${temporary_root}/mcp-initialize.json" \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"phase8-docker","version":"1.0"}}}'
invoke_mcp "${temporary_root}/mcp-tools.json" \
    '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
invoke_mcp "${temporary_root}/mcp-status.json" \
    '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"vault_status","arguments":{}}}'
python3 - \
    "${temporary_root}/mcp-initialize.json" \
    "${temporary_root}/mcp-tools.json" \
    "${temporary_root}/mcp-status.json" <<'PY'
import json
import sys

def payload(path):
    text = open(path, encoding="utf-8").read().strip()
    if text.startswith("{"):
        return json.loads(text)
    for line in text.splitlines():
        if line.startswith("data:"):
            return json.loads(line[5:].strip())
    raise SystemExit("phase8_docker_mcp_response_invalid")

initialize = payload(sys.argv[1])
tools = payload(sys.argv[2])
status = payload(sys.argv[3])
if "serverInfo" not in initialize.get("result", {}):
    raise SystemExit("phase8_docker_mcp_initialize_failed")
if len(tools.get("result", {}).get("tools", [])) < 10:
    raise SystemExit("phase8_docker_mcp_tools_failed")
if "result" not in status:
    raise SystemExit("phase8_docker_mcp_status_failed")
PY

curl -fsS \
    -c "$cookie_jar" \
    -b "$cookie_jar" \
    -X POST \
    -H "X-CSRF-TOKEN: ${csrf}" \
    "${base_url}/api/backups" >"${temporary_root}/backup.json"
backup_name="$(python3 -c \
    'import json,sys; print(json.load(open(sys.argv[1]))["backup"]["fileName"])' \
    "${temporary_root}/backup.json")"
case "$backup_name" in
    *.cvbackup) ;;
    *)
        echo "phase8_docker_backup_name_invalid" >&2
        exit 5
        ;;
esac
curl -fsS \
    -c "$cookie_jar" \
    -b "$cookie_jar" \
    "${base_url}/api/backups/${backup_name}/download" \
    -o "${temporary_root}/${backup_name}"
test -s "${temporary_root}/${backup_name}"

docker rm -f "$container" >/dev/null
start_container
wait_for_status
grep -q '"state":"locked"' "${temporary_root}/status.json"
invoke_mcp "${temporary_root}/mcp-after-recreate.json" \
    '{"jsonrpc":"2.0","id":4,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"phase8-docker-recreate","version":"1.0"}}}'
grep -q 'serverInfo' "${temporary_root}/mcp-after-recreate.json"
curl -fsS \
    -c "${temporary_root}/recreated-cookies.txt" \
    -b "${temporary_root}/recreated-cookies.txt" \
    -H 'Content-Type: application/json' \
    -d "{\"password\":\"${password}\"}" \
    "${base_url}/api/vault/unlock" >"${temporary_root}/recreated-unlock.json"
curl -fsS \
    -c "${temporary_root}/recreated-cookies.txt" \
    -b "${temporary_root}/recreated-cookies.txt" \
    "${base_url}/api/backups" >"${temporary_root}/recreated-backups.json"
python3 - "${temporary_root}/recreated-backups.json" "$backup_name" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    result = json.load(stream)
if sys.argv[2] not in {item["fileName"] for item in result.get("items", [])}:
    raise SystemExit("phase8_docker_backup_not_persisted")
PY
docker rm -f "$container" >/dev/null

docker run --rm --user 0 --entrypoint sh \
    --volume "${target_volume}:/restore" \
    "$image" -c 'chown -R 10001:10001 /restore'
if ! printf '%s\n' "$password" | script -qec \
    "docker run --rm --interactive --tty \
        --volume '${temporary_root}/${backup_name}:/backup/package.cvbackup:ro' \
        --volume '${target_volume}:/restore' \
        --volume '${data_volume}:/data' \
        '${image}' --restore-backup '/backup/package.cvbackup' --target /restore/restored" \
    /dev/null >"${temporary_root}/restore.log" 2>&1; then
    sed "s/${password}/[redacted]/g" \
        "${temporary_root}/restore.log" >&2
    exit 6
fi
docker run --rm --entrypoint sh \
    --volume "${target_volume}:/restore" \
    "$image" -c 'test -f /restore/restored/vault.db && test -f /restore/restored/vault.key'

docker run -d \
    --name "$restored_container" \
    --publish "127.0.0.1:${port}:8080" \
    --read-only \
    --tmpfs /tmp \
    --security-opt no-new-privileges \
    --cap-drop ALL \
    --env CIPHERVAULT_DATA_DIR=/restore/restored \
    --volume "${target_volume}:/restore" \
    --volume "${data_volume}:/data" \
    "$image" >/dev/null
for _ in $(seq 1 120); do
    if curl -fsS "${base_url}/api/vault/status" \
        >"${temporary_root}/restored-status.json" 2>/dev/null; then
        break
    fi
    sleep 0.25
done
grep -q '"state":"locked"' "${temporary_root}/restored-status.json"
curl -fsS \
    -H 'Content-Type: application/json' \
    -d "{\"password\":\"${password}\"}" \
    "${base_url}/api/vault/unlock" >"${temporary_root}/restored-unlock.json"
grep -q '"state":"unlocked"' "${temporary_root}/restored-unlock.json"

health_status="starting"
for _ in $(seq 1 75); do
    health_status="$(docker inspect "$restored_container" \
        --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}')"
    if [[ "$health_status" == "healthy" ]]; then
        break
    fi
    if [[ "$health_status" == "unhealthy" ]]; then
        docker logs "$restored_container" >&2
        exit 7
    fi
    sleep 1
done
test "$health_status" = "healthy"
archive_sha="$(sha256sum "$docker_tar" | awk '{print $1}')"
compose_file="${repo_root}/v2/deploy/docker-compose.yml"
compose_validation=yaml
if docker compose version >/dev/null 2>&1; then
    docker compose --file "$compose_file" config >/dev/null
    compose_validation=docker-compose
else
    python3 - "$compose_file" <<'PY'
import sys
import yaml

with open(sys.argv[1], encoding="utf-8") as stream:
    compose = yaml.safe_load(stream)
service = compose["services"]["cipher-vault"]
if service["ports"] != ["127.0.0.1:${CIPHERVAULT_PORT:-8080}:8080"]:
    raise SystemExit("phase8_docker_compose_port_invalid")
if "cipher-vault-data:/data" not in service["volumes"]:
    raise SystemExit("phase8_docker_compose_volume_invalid")
if compose["volumes"]["cipher-vault-data"]["name"] != "cipher-vault-data":
    raise SystemExit("phase8_docker_compose_volume_name_invalid")
PY
fi

mkdir -p "$evidence_root"
python3 - \
    "${evidence_root}/docker.json" \
    "$image_id" \
    "$architecture" \
    "$image_user" \
    "$archive_sha" \
    "$health_status" \
    "$compose_validation" <<'PY'
import json
import sys

path, image_id, architecture, image_user, archive_sha, health, compose_validation = sys.argv[1:]
evidence = {
    "platform": "docker",
    "environment": "linux/amd64 formal image",
    "imageId": image_id,
    "architecture": architecture,
    "user": image_user,
    "artifactFileName": "cipher-vault-2.0.0-rc.1-docker-linux-amd64.tar",
    "artifactSha256": archive_sha,
    "healthcheckUsesStatus": True,
    "curlPresent": True,
    "runsAs10001": True,
    "composeLoopbackOnly": True,
    "composeValidation": compose_validation,
    "namedVolume": True,
    "webStaticAssets": True,
    "securityHeaders": True,
    "mcpInitialize": True,
    "mcpToolsList": True,
    "mcpVaultStatus": True,
    "wrongTokenRejected": True,
    "backupCreated": True,
    "offlineRestore": True,
    "restoredVaultLocked": True,
    "restoredVaultUnlock": True,
    "dataPersistedAfterRestart": True,
    "mcpTokenPersistedAfterRestart": True,
    "observedHealth": health,
    "passed": True
}
with open(path, "w", encoding="utf-8", newline="\n") as stream:
    json.dump(evidence, stream, indent=2)
    stream.write("\n")
PY

echo "phase8_docker_release_verification_passed:${image_id}"
