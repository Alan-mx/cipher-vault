#!/usr/bin/env bash
set -euo pipefail

readonly repo_root="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)}"
readonly v2_root="${repo_root}/v2"
readonly solution="${repo_root}/CipherVault.slnx"
readonly image="cipher-vault-phase7:${RANDOM}-$$"
readonly container="cipher-vault-phase7-${RANDOM}-$$"
readonly target_volume="cipher-vault-phase7-target-${RANDOM}-$$"
readonly config_volume="cipher-vault-phase7-config-${RANDOM}-$$"
readonly port="${PHASE7_PORT:-$((45200 + (RANDOM % 1000)))}"
readonly password="phase7 docker password"
readonly artifact_root="${v2_root}/artifacts/cross-platform/phase7/docker"
readonly package_path="${artifact_root}/docker.cvbackup"
readonly package_name="$(basename "$package_path")"

cleanup() {
    docker rm -f "$container" >/dev/null 2>&1 || true
    docker volume rm "$target_volume" "$config_volume" >/dev/null 2>&1 || true
}
trap cleanup EXIT

export PATH="${HOME}/.dotnet:${PATH}"
dotnet restore "$solution" --locked-mode -p:NuGetAudit=false
dotnet build "$solution" -c Release --no-restore -warnaserror

mkdir -p "$artifact_root"
readonly source_root="${artifact_root}/source-${RANDOM}-$$"
dotnet run \
    --project "${v2_root}/tests/CipherVault.Phase7.ProcessProbe/CipherVault.Phase7.ProcessProbe.csproj" \
    -c Release --no-build -- \
    create "${source_root}/vault" "${source_root}/backups" \
    "$package_path" "$password"

docker build \
    -f "${v2_root}/tests/CipherVault.Server.Tests/Containers/Dockerfile.phase5" \
    -t "$image" \
    "$repo_root"
docker volume create "$target_volume" >/dev/null
docker volume create "$config_volume" >/dev/null
docker run --rm --user 0 \
    -v "${target_volume}:/restore" \
    -v "${config_volume}:/config" \
    --entrypoint sh "$image" \
    -c 'chown -R 10001:10001 /restore /config'

set +e
docker run --rm \
    -e CIPHERVAULT_CONFIG_DIR=/config \
    -e CIPHERVAULT_DATA_DIR=/unused \
    -v "${artifact_root}:/backup:ro" \
    -v "${target_volume}:/restore" \
    -v "${config_volume}:/config" \
    "$image" \
    --restore-backup "/backup/${package_name}" \
    --target /restore/non-tty-target </dev/null \
    >"${artifact_root}/non-tty.log" 2>&1
non_tty_exit=$?
set -e
if [[ "$non_tty_exit" -ne 4 ]]; then
    cat "${artifact_root}/non-tty.log" >&2
    echo "Expected Docker non-TTY restore exit 4, got $non_tty_exit." >&2
    exit 1
fi
docker run --rm \
    -v "${target_volume}:/restore" \
    --entrypoint sh "$image" \
    -c 'test ! -e /restore/non-tty-target'

printf '%s\n' "$password" | script -qec \
    "docker run --rm -it \
        -e CIPHERVAULT_CONFIG_DIR=/config \
        -e CIPHERVAULT_DATA_DIR=/unused \
        -v '${artifact_root}:/backup:ro' \
        -v '${target_volume}:/restore' \
        -v '${config_volume}:/config' \
        '$image' \
        --restore-backup '/backup/${package_name}' \
        --target /restore/target" \
    /dev/null >"${artifact_root}/restore.log" 2>&1

docker run --rm \
    -v "${target_volume}:/restore" \
    --entrypoint sh "$image" \
    -c 'test "$(find /restore/target -maxdepth 1 -type f -printf "%f\n" | sort | tr "\n" " ")" = "vault.db vault.key "'

docker run -d \
    --name "$container" \
    -p "127.0.0.1:${port}:43127" \
    -e CIPHERVAULT_DATA_DIR=/restore/target \
    -e CIPHERVAULT_CONFIG_DIR=/config \
    -e CIPHERVAULT_BACKUP_DIR=/restore/backups \
    -v "${target_volume}:/restore" \
    -v "${config_volume}:/config" \
    "$image" >/dev/null

for _ in $(seq 1 60); do
    if curl -fsS "http://127.0.0.1:${port}/api/vault/status" \
        >"${artifact_root}/status.json"; then
        break
    fi
    sleep 1
done
grep -q '"state":"locked"' "${artifact_root}/status.json"

echo "Phase 7 Docker verification passed."
echo "Host binding: 127.0.0.1:${port}"
echo "Non-TTY restore rejected; TTY restore produced a Locked Vault."
