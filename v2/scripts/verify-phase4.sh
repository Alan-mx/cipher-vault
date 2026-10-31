#!/usr/bin/env bash
set -euo pipefail

readonly v2_root="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
readonly application_root="${v2_root}/src/CipherVault.Application"
readonly core_root="${v2_root}/src/CipherVault.Core"
readonly server_root="${v2_root}/src/CipherVault.Server"
readonly endpoints_root="${server_root}/Api/Endpoints"
readonly application_project="${application_root}/CipherVault.Application.csproj"
readonly core_project="${core_root}/CipherVault.Core.csproj"
readonly server_project="${server_root}/CipherVault.Server.csproj"

readonly legacy_pattern='Recovery|IntegrityKey|HMAC|Access Grant|Approval|Broker|Named Pipe|HardLifetime'
readonly logging_pattern='Console\.Write|Serilog|NLog'
readonly application_pattern='Microsoft\.Data\.Sqlite|SQLitePCL|Microsoft\.AspNetCore|ModelContextProtocol|SqliteConnection|SqliteCommand|SqliteDataReader|SqliteTransaction'
readonly core_pattern='Microsoft\.AspNetCore|ModelContextProtocol'
readonly server_pattern='Microsoft\.Data\.Sqlite|SQLitePCL|CipherVault\.Core\.Storage|ModelContextProtocol|MapMcp|BackupService|/api/backups'

mapfile -d '' application_files < <(
    find "$application_root" -type f -name '*.cs' \
        -not -path '*/bin/*' \
        -not -path '*/obj/*' \
        -print0
)
mapfile -d '' core_files < <(
    find "$core_root" -type f -name '*.cs' \
        -not -path '*/bin/*' \
        -not -path '*/obj/*' \
        -print0
)
mapfile -d '' server_files < <(
    find "$server_root" -type f -name '*.cs' \
        -not -path '*/bin/*' \
        -not -path '*/obj/*' \
        -print0
)

if grep -n -E -i "$legacy_pattern|$logging_pattern|$application_pattern" \
    "${application_files[@]}"; then
    echo "Forbidden Application reference found." >&2
    exit 1
fi

if grep -n -E -i "$legacy_pattern|$logging_pattern|$core_pattern" \
    "${core_files[@]}"; then
    echo "Forbidden Core reference found." >&2
    exit 1
fi

if grep -n -E -i "$application_pattern" "$application_project"; then
    echo "Forbidden Application project dependency found." >&2
    exit 1
fi

if grep -n -E -i "$core_pattern" "$core_project"; then
    echo "Forbidden Core project dependency found." >&2
    exit 1
fi

if grep -n -E -i "$legacy_pattern|$server_pattern" "${server_files[@]}"; then
    echo "Forbidden Server reference found." >&2
    exit 1
fi

route_count=$(
    grep -h -E '\.Map(Get|Post|Put|Patch|Delete)\(' \
        "$endpoints_root"/*.cs | wc -l
)
if [[ "$route_count" -ne 28 ]]; then
    echo "Expected 28 Phase 4 routes but found $route_count." >&2
    exit 1
fi

artifact_args=()
if [[ -n "${CV_DOTNET_ARTIFACTS_PATH:-}" ]]; then
    artifact_args+=(--artifacts-path "$CV_DOTNET_ARTIFACTS_PATH")
fi

export PATH="${HOME}/.dotnet:${PATH}"
cd "$v2_root"
dotnet restore CipherVault.V2.slnx --locked-mode -p:NuGetAudit=false \
    "${artifact_args[@]}"
dotnet format CipherVault.V2.slnx --verify-no-changes --no-restore
dotnet build CipherVault.V2.slnx -c Release --no-restore \
    -warnaserror "${artifact_args[@]}"

readonly results_dir="${v2_root}/artifacts/test-results/linux"
mkdir -p "$results_dir"
dotnet test tests/CipherVault.Core.Tests/CipherVault.Core.Tests.csproj \
    -c Release --no-build --no-restore "${artifact_args[@]}" \
    --logger "trx;LogFileName=phase4-linux-core.trx" \
    --results-directory "$results_dir"
dotnet test tests/CipherVault.Application.Tests/CipherVault.Application.Tests.csproj \
    -c Release --no-build --no-restore "${artifact_args[@]}" \
    --logger "trx;LogFileName=phase4-linux-application.trx" \
    --results-directory "$results_dir"
dotnet test tests/CipherVault.Server.Tests/CipherVault.Server.Tests.csproj \
    -c Release --no-build --no-restore "${artifact_args[@]}" \
    --logger "trx;LogFileName=phase4-linux-server.trx" \
    --results-directory "$results_dir"

for trx in \
    "$results_dir/phase4-linux-core.trx" \
    "$results_dir/phase4-linux-application.trx" \
    "$results_dir/phase4-linux-server.trx"; do
    if ! grep -q 'notExecuted="0"' "$trx"; then
        echo "Phase 4 TRX contains skipped tests: $trx" >&2
        exit 1
    fi
done

echo "Phase 4 Linux verification passed."
echo "TRX: $results_dir/phase4-linux-core.trx"
echo "TRX: $results_dir/phase4-linux-application.trx"
echo "TRX: $results_dir/phase4-linux-server.trx"
