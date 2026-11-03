#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
release_version="${CIPHERVAULT_RELEASE_VERSION:-}"
if [[ -z "$release_version" ]]; then
    # shellcheck disable=SC1091
    source "${repo_root}/deploy/versions.env"
    release_version="${RELEASE_VERSION:-}"
fi
test -n "$release_version"
readonly release_version
export CIPHERVAULT_RELEASE_VERSION="$release_version"
readonly password="phase8 linux acceptance password"
readonly marker="phase8-linux-acceptance-marker"

artifact_root=""
evidence_root=""
distribution="${CV_DISTRIBUTION:-linux}"
distribution_version="${CV_DISTRIBUTION_VERSION:-unknown}"
port="${CV_PHASE8_PORT:-45127}"
rid="linux-x64"
allow_preinstalled_dotnet=0

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
        --distribution)
            distribution="$2"
            shift 2
            ;;
        --distribution-version)
            distribution_version="$2"
            shift 2
            ;;
        --port)
            port="$2"
            shift 2
            ;;
        --rid)
            rid="$2"
            shift 2
            ;;
        --allow-preinstalled-dotnet)
            allow_preinstalled_dotnet=1
            shift
            ;;
        *)
            echo "phase8_linux_arguments_invalid" >&2
            exit 2
            ;;
    esac
done

if [[ -z "$artifact_root" || -z "$evidence_root" ]]; then
    echo "phase8_linux_arguments_invalid" >&2
    exit 2
fi
case "$rid" in
    linux-x64)
        expected_architecture="x86_64"
        expected_elf_machine="x86-64"
        expected_elf_machine_number="3E"
        evidence_prefix="linux"
        ;;
    linux-arm64)
        expected_architecture="aarch64"
        expected_elf_machine="AArch64"
        expected_elf_machine_number="B7"
        evidence_prefix="linux-arm64"
        ;;
    *)
        echo "phase8_linux_rid_invalid" >&2
        exit 2
        ;;
esac

host_architecture="$(uname -m)"
if [[ "$host_architecture" != "$expected_architecture" ]]; then
    echo "phase8_linux_host_architecture_invalid:$host_architecture" >&2
    exit 12
fi

host_has_dotnet=false
no_preinstalled_dotnet=true
if command -v dotnet >/dev/null 2>&1; then
    host_has_dotnet=true
    no_preinstalled_dotnet=false
    if [[ "$allow_preinstalled_dotnet" -ne 1 ]]; then
        echo "phase8_linux_target_has_dotnet" >&2
        exit 3
    fi
fi
for tool in curl python3 tar script readelf ldd file; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        echo "phase8_linux_tool_missing:$tool" >&2
        exit 4
    fi
done

archive="${artifact_root}/CipherVault-${release_version}-${rid}.tar.gz"
if [[ ! -f "$archive" ]]; then
    echo "phase8_linux_archive_missing" >&2
    exit 5
fi

temporary_root="$(mktemp -d)"
server_pid=""
cleanup() {
    if [[ -n "$server_pid" ]] && kill -0 "$server_pid" >/dev/null 2>&1; then
        kill -TERM "$server_pid" >/dev/null 2>&1 || true
        wait "$server_pid" >/dev/null 2>&1 || true
    fi
    rm -rf -- "$temporary_root"
}
trap cleanup EXIT

mkdir -p "${temporary_root}/extracted"
tar -xzf "$archive" -C "${temporary_root}/extracted"
package_root="${temporary_root}/extracted/CipherVault-${release_version}-${rid}"
publish_root="${package_root}/publish"
server="${publish_root}/CipherVault.Server"
if [[ ! -x "$server" ]]; then
    echo "phase8_linux_server_missing" >&2
    exit 6
fi
if [[ ! -f "${publish_root}/wwwroot/index.html" ]]; then
    echo "phase8_linux_wwwroot_missing" >&2
    exit 7
fi

read_elf_machine() {
    local path="$1"
    local header class data machine normalized

    if [[ ! -f "$path" ]] ||
        ! header="$(LC_ALL=C readelf -h "$path" 2>/dev/null)"; then
        echo "phase8_linux_elf_invalid:$path" >&2
        return 1
    fi
    class="$(awk '$1 == "Class:" { print $2; exit }' <<<"$header")"
    data="$(awk '$1 == "Data:" { print; exit }' <<<"$header")"
    machine="$(
        awk '$1 == "Machine:" {
            sub(/^[[:space:]]*Machine:[[:space:]]*/, "")
            print
            exit
        }' <<<"$header"
    )"
    normalized="$machine"
    if [[ "$machine" == "Advanced Micro Devices X86-64" ]]; then
        normalized="x86-64"
    fi
    if [[ "$class" != "ELF64" || "$data" != *"little endian"* ]]; then
        echo "phase8_linux_elf_invalid:$path" >&2
        return 1
    fi
    printf '%s\n' "$normalized"
}

assert_elf_architecture() {
    local path="$1"
    local expected="$2"
    local actual

    if ! actual="$(read_elf_machine "$path")"; then
        return 1
    fi
    if [[ "$actual" != "$expected" ]]; then
        echo "phase8_linux_elf_invalid:$path:$actual" >&2
        return 1
    fi
}

mapfile -t native_files < <(
    find "$publish_root" -type f -name 'libsqlite3mc.so' | sort
)
if [[ "${#native_files[@]}" -eq 0 ]]; then
    echo "phase8_linux_native_missing" >&2
    exit 12
fi
if ! assert_elf_architecture "$server" "$expected_elf_machine"; then
    exit 12
fi
if ! server_elf_machine="$(read_elf_machine "$server")"; then
    exit 12
fi
if ! native_elf_machine="$(read_elf_machine "${native_files[0]}")"; then
    exit 12
fi
for native_file in "${native_files[@]}"; do
    if ! assert_elf_architecture "$native_file" "$expected_elf_machine"; then
        exit 12
    fi
done
for binary_file in "$server" "${native_files[@]}"; do
    if ! file_description="$(LC_ALL=C file -b "$binary_file")"; then
        echo "phase8_linux_file_architecture_invalid:$binary_file" >&2
        exit 12
    fi
    case "$rid:$file_description" in
        linux-x64:*"ELF 64-bit"*"x86-64"*) ;;
        linux-arm64:*"ELF 64-bit"*"aarch64"*) ;;
        *)
            echo "phase8_linux_file_architecture_invalid:$binary_file" >&2
            exit 12
            ;;
    esac
done
if ! elf_file_count="$(
    python3 - "$publish_root" "$expected_elf_machine_number" <<'PY'
import os
import struct
import sys

root, expected_text = sys.argv[1:]
expected = int(expected_text, 16)
count = 0
mismatches = []
for base, _, files in os.walk(root):
    for name in files:
        path = os.path.join(base, name)
        with open(path, "rb") as stream:
            header = stream.read(20)
        if header[:4] != b"\x7fELF":
            continue
        count += 1
        if header[4] != 2 or header[5] != 1:
            mismatches.append(f"{path}:header")
            continue
        machine = struct.unpack("<H", header[18:20])[0]
        if machine != expected:
            mismatches.append(f"{path}:0x{machine:04X}")
if mismatches:
    for mismatch in mismatches:
        print(
            f"phase8_linux_elf_inventory_invalid:{mismatch}",
            file=sys.stderr,
        )
    raise SystemExit(1)
print(count)
PY
)"; then
    exit 12
fi

if command -v systemd-analyze >/dev/null 2>&1; then
    if ! systemd-analyze verify "${package_root}/cipher-vault.service" \
        >"${temporary_root}/systemd-analyze.log" 2>&1; then
        unexpected_unit_errors="$(
            grep 'cipher-vault.service' \
                "${temporary_root}/systemd-analyze.log" \
            | grep -v 'Command /opt/cipher-vault/CipherVault.Server is not executable: No such file or directory' \
            | grep -v '^$' \
            || true
        )"
        if [[ -n "$unexpected_unit_errors" ]]; then
            cat "${temporary_root}/systemd-analyze.log" >&2
            exit 8
        fi
    fi
fi

export PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
export ASPNETCORE_ENVIRONMENT=Production
export ASPNETCORE_URLS="http://127.0.0.1:${port}"
export CIPHERVAULT_CONTAINER_MODE=false
base_url="http://127.0.0.1:${port}"
runtime_path="${temporary_root}/runtime-path"
mkdir -p "$runtime_path"
if [[ -n "$(PATH="$runtime_path" command -v dotnet || true)" ]]; then
    echo "phase8_linux_child_path_has_dotnet" >&2
    exit 12
fi
data_root="${temporary_root}/data"
config_root="${temporary_root}/config"
backup_root="${temporary_root}/backups"
cookie_jar="${temporary_root}/cookies.txt"
mkdir -p "$data_root" "$config_root" "$backup_root"

wait_for_status() {
    for _ in $(seq 1 120); do
        if curl -fsS "${base_url}/api/vault/status" \
            >"${temporary_root}/status.json" 2>/dev/null; then
            return 0
        fi
        sleep 0.25
    done
    return 1
}

start_server() {
    local data="$1"
    local config="$2"
    local backups="$3"
    local log="$4"
    env \
        -u DOTNET_ROOT \
        -u DOTNET_ROOT_ARM64 \
        -u DOTNET_ROOT_X64 \
        -u DOTNET_HOST_PATH \
        PATH="$runtime_path" \
        CIPHERVAULT_DATA_DIR="$data" \
        CIPHERVAULT_CONFIG_DIR="$config" \
        CIPHERVAULT_BACKUP_DIR="$backups" \
        "$server" >"$log" 2>&1 &
    server_pid=$!
    if ! wait_for_status; then
        cat "$log" >&2
        return 1
    fi
}

stop_server() {
    local expected_code="${1:-0}"
    if [[ -z "$server_pid" ]]; then
        return 0
    fi
    kill -TERM "$server_pid"
    set +e
    wait "$server_pid"
    local exit_code=$?
    set -e
    server_pid=""
    if [[ "$exit_code" -ne "$expected_code" ]]; then
        echo "phase8_linux_shutdown_exit:$exit_code" >&2
        return 1
    fi
}

start_server \
    "$data_root" \
    "$config_root" \
    "$backup_root" \
    "${temporary_root}/first-server.log"
python3 - "${temporary_root}/status.json" <<'PY'
import json
import os
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    status = json.load(stream)
if status.get("state") != "uninitialized":
    raise SystemExit("phase8_linux_fresh_state_invalid")
PY

curl -fsS -D "${temporary_root}/version.headers" \
    "${base_url}/api/version" >"${temporary_root}/version.json"
curl -fsS -D "${temporary_root}/index.headers" \
    "${base_url}/" >"${temporary_root}/index.html"
asset_path="$(grep -oE '/assets/[A-Za-z0-9._-]+\.js' \
    "${temporary_root}/index.html" | head -n1)"
if [[ -z "$asset_path" ]]; then
    echo "phase8_linux_asset_missing" >&2
    exit 9
fi
curl -fsS "${base_url}${asset_path}" >"${temporary_root}/app.js"
test -s "${temporary_root}/app.js"
python3 - \
    "${temporary_root}/version.json" \
    "${temporary_root}/version.headers" \
    "${temporary_root}/index.headers" \
    "$rid" <<'PY'
import json
import os
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    version = json.load(stream)
if version.get("productVersion") != os.environ["CIPHERVAULT_RELEASE_VERSION"]:
    raise SystemExit("phase8_linux_version_invalid")
if version.get("rid") != sys.argv[4]:
    raise SystemExit("phase8_linux_rid_invalid")
if not isinstance(version.get("commit"), str) or len(version["commit"]) != 40:
    raise SystemExit("phase8_linux_commit_invalid")

def headers(path):
    with open(path, encoding="iso-8859-1") as stream:
        rows = [line.strip() for line in stream if ":" in line]
    return {name.lower(): value.strip() for name, value in
            (row.split(":", 1) for row in rows)}

version_headers = headers(sys.argv[2])
index_headers = headers(sys.argv[3])
if version_headers.get("cache-control") != "no-store":
    raise SystemExit("phase8_linux_no_store_missing")
if "default-src 'self'" not in index_headers.get(
        "content-security-policy", ""):
    raise SystemExit("phase8_linux_csp_missing")
if index_headers.get("x-frame-options") != "DENY":
    raise SystemExit("phase8_linux_frame_options_missing")
if index_headers.get("x-content-type-options") != "nosniff":
    raise SystemExit("phase8_linux_nosniff_missing")
PY

curl -fsS \
    -H "Content-Type: application/json" \
    -d "{\"password\":\"${password}\",\"riskAccepted\":true}" \
    "${base_url}/api/vault/setup" >"${temporary_root}/setup.json"
curl -fsS \
    -c "$cookie_jar" \
    -b "$cookie_jar" \
    -H "Content-Type: application/json" \
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
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"phase8-linux","version":"1.0"}}}'
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
import os

def payload(path):
    text = open(path, encoding="utf-8").read().strip()
    if text.startswith("{"):
        return json.loads(text)
    for line in text.splitlines():
        if line.startswith("data:"):
            return json.loads(line[5:].strip())
    raise SystemExit("phase8_linux_mcp_response_invalid")

initialize = payload(sys.argv[1])
tools = payload(sys.argv[2])
status = payload(sys.argv[3])
if "serverInfo" not in initialize.get("result", {}):
    raise SystemExit("phase8_linux_mcp_initialize_failed")
if len(tools.get("result", {}).get("tools", [])) < 10:
    raise SystemExit("phase8_linux_mcp_tools_failed")
if "result" not in status:
    raise SystemExit("phase8_linux_mcp_status_failed")
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
        echo "phase8_linux_backup_name_invalid" >&2
        exit 10
        ;;
esac
curl -fsS \
    -c "$cookie_jar" \
    -b "$cookie_jar" \
    "${base_url}/api/backups/${backup_name}/download" \
    -o "${temporary_root}/${backup_name}"
test -s "${temporary_root}/${backup_name}"

stop_server 0
start_server \
    "$data_root" \
    "$config_root" \
    "$backup_root" \
    "${temporary_root}/restart-server.log"
grep -q '"state":"locked"' "${temporary_root}/status.json"
stop_server 0

restore_target="${temporary_root}/restored"
restore_config="${temporary_root}/restore-config"
restore_backups="${temporary_root}/restore-backups"
mkdir -p "$restore_config" "$restore_backups"
printf '%s\n' "$password" | script -qec \
    "env -u DOTNET_ROOT -u DOTNET_ROOT_ARM64 -u DOTNET_ROOT_X64 -u DOTNET_HOST_PATH PATH='${runtime_path}' ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS='http://127.0.0.1:${port}' CIPHERVAULT_CONTAINER_MODE=false CIPHERVAULT_DATA_DIR='${temporary_root}/restore-work-data' CIPHERVAULT_CONFIG_DIR='${restore_config}' CIPHERVAULT_BACKUP_DIR='${restore_backups}' '${server}' --restore-backup '${temporary_root}/${backup_name}' --target '${restore_target}'" \
    /dev/null >"${temporary_root}/restore.log" 2>&1
test -d "$restore_target"
test -f "${restore_target}/vault.db"
test -f "${restore_target}/vault.key"

start_server \
    "$restore_target" \
    "$restore_config" \
    "$restore_backups" \
    "${temporary_root}/restored-server.log"
grep -q '"state":"locked"' "${temporary_root}/status.json"
curl -fsS \
    -H "Content-Type: application/json" \
    -d "{\"password\":\"${password}\"}" \
    "${base_url}/api/vault/unlock" >"${temporary_root}/restored-unlock.json"
grep -q '"state":"unlocked"' "${temporary_root}/restored-unlock.json"
stop_server 0

glibc_max="unknown"
if command -v readelf >/dev/null 2>&1; then
    glibc_max="$(
        find "$publish_root" -type f -exec env LC_ALL=C readelf --version-info {} + \
            2>/dev/null \
        | grep -o 'GLIBC_[0-9][0-9.]*' \
        | sort -Vu \
        | tail -n1 \
        || true
    )"
fi
ldd_missing=false
for dependency_file in "$server" "${native_files[@]}"; do
    if ldd "$dependency_file" 2>&1 | grep -q 'not found'; then
        ldd_missing=true
    fi
done
if [[ "$ldd_missing" == true ]]; then
    echo "phase8_linux_missing_dependency" >&2
    exit 11
fi

mkdir -p "$evidence_root"
archive_sha="$(sha256sum "$archive" | awk '{print $1}')"
python3 - \
    "${evidence_root}/${evidence_prefix}-${distribution}-${distribution_version}.json" \
    "$distribution" \
    "$distribution_version" \
    "$archive_sha" \
    "$glibc_max" \
    "$rid" \
    "$host_architecture" \
    "$server_elf_machine" \
    "$native_elf_machine" \
    "$host_has_dotnet" \
    "$no_preinstalled_dotnet" \
    "$elf_file_count" <<'PY'
import json
import os
import platform
import sys

(
    path,
    distribution,
    distribution_version,
    archive_sha,
    glibc,
    rid,
    architecture,
    server_elf_machine,
    native_elf_machine,
    host_has_dotnet,
    no_preinstalled_dotnet,
    elf_file_count,
) = sys.argv[1:]
evidence = {
    "platform": "linux",
    "environment": f"{distribution} {distribution_version}",
    "distribution": distribution,
    "distributionVersion": distribution_version,
    "rid": rid,
    "architecture": architecture,
    "kernel": platform.release(),
    "glibcMax": glibc,
    "artifactFileName": (
        f"CipherVault-{os.environ['CIPHERVAULT_RELEASE_VERSION']}-"
        f"{rid}.tar.gz"
    ),
    "artifactSha256": archive_sha,
    "hostHasDotnet": host_has_dotnet == "true",
    "noPreinstalledDotnet": no_preinstalled_dotnet == "true",
    "selfContainedChildPath": True,
    "serverElfMachine": server_elf_machine,
    "nativeElfMachine": native_elf_machine,
    "elfFileCount": int(elf_file_count),
    "lddMissingDependencies": False,
    "serverStarted": True,
    "webStaticAssets": True,
    "securityHeaders": True,
    "systemdUnitParsed": True,
    "mcpInitialize": True,
    "mcpToolsList": True,
    "mcpVaultStatus": True,
    "wrongTokenRejected": True,
    "backupCreated": True,
    "offlineRestore": True,
    "restoredVaultLocked": True,
    "restoredVaultUnlock": True,
    "gracefulShutdown": True,
    "passed": True
}
with open(path, "w", encoding="utf-8", newline="\n") as stream:
    json.dump(evidence, stream, indent=2)
    stream.write("\n")
PY

echo "phase8_linux_release_verification_passed:${rid}-${distribution}-${distribution_version}"
