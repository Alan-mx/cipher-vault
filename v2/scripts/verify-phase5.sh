#!/usr/bin/env bash
set -euo pipefail

readonly v2_root="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
readonly repo_root="$(cd "${v2_root}/.." && pwd)"
readonly solution="${repo_root}/CipherVault.slnx"
readonly application_root="${v2_root}/src/CipherVault.Application"
readonly core_root="${v2_root}/src/CipherVault.Core"
readonly server_root="${v2_root}/src/CipherVault.Server"
readonly application_project="${application_root}/CipherVault.Application.csproj"
readonly core_project="${core_root}/CipherVault.Core.csproj"
readonly server_project="${server_root}/CipherVault.Server.csproj"

readonly legacy_pattern='Grant|Approval|Broker|Named Pipe|Recovery|IntegrityKey|HMAC|Console\.Write|Serilog|NLog'
readonly application_pattern='Microsoft\.Data\.Sqlite|SQLitePCL|Microsoft\.AspNetCore|ModelContextProtocol|SqliteConnection|SqliteCommand|SqliteDataReader|SqliteTransaction'
readonly core_pattern='Microsoft\.AspNetCore|ModelContextProtocol'
readonly server_pattern='Microsoft\.Data\.Sqlite|SQLitePCL|CipherVault\.Core\.Storage'

mapfile -d '' application_files < <(
    find "$application_root" -type f -name '*.cs' \
        -not -path '*/bin/*' -not -path '*/obj/*' -print0
)
mapfile -d '' core_files < <(
    find "$core_root" -type f -name '*.cs' \
        -not -path '*/bin/*' -not -path '*/obj/*' -print0
)
mapfile -d '' server_files < <(
    find "$server_root" -type f -name '*.cs' \
        -not -path '*/bin/*' -not -path '*/obj/*' -print0
)

if grep -n -E -i "$legacy_pattern|$application_pattern" "${application_files[@]}"; then
    echo "Forbidden Application reference found." >&2
    exit 1
fi
if grep -n -E -i "$legacy_pattern|$core_pattern" "${core_files[@]}"; then
    echo "Forbidden Core reference found." >&2
    exit 1
fi
if grep -n -E -i "$legacy_pattern|$server_pattern" "${server_files[@]}"; then
    echo "Forbidden Server reference found." >&2
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

tool_count=$(
    grep -h -E '\[McpServerTool\(Name\s*=\s*McpToolNames\.[A-Za-z]+\)\]' \
        "${server_files[@]}" | wc -l
)
if [[ "$tool_count" -ne 15 ]]; then
    echo "Expected 15 MCP tools but found $tool_count." >&2
    exit 1
fi
if ! grep -R -q 'CryptographicOperations.FixedTimeEquals' "$server_root"; then
    echo "MCP token comparison must use FixedTimeEquals." >&2
    exit 1
fi

artifact_args=()
if [[ -n "${CV_DOTNET_ARTIFACTS_PATH:-}" ]]; then
    artifact_args+=(--artifacts-path "$CV_DOTNET_ARTIFACTS_PATH")
fi

export PATH="${HOME}/.dotnet:${PATH}"
cd "$v2_root"
dotnet restore "$solution" --locked-mode -p:NuGetAudit=false \
    "${artifact_args[@]}"
dotnet format "$solution" --verify-no-changes --no-restore
dotnet build "$solution" -c Release --no-restore \
    -warnaserror "${artifact_args[@]}"

readonly results_dir="${v2_root}/artifacts/test-results/linux"
mkdir -p "$results_dir"
dotnet test tests/CipherVault.Core.Tests/CipherVault.Core.Tests.csproj \
    -c Release --no-build --no-restore "${artifact_args[@]}" \
    --logger "trx;LogFileName=phase5-linux-core.trx" \
    --results-directory "$results_dir"
dotnet test tests/CipherVault.Application.Tests/CipherVault.Application.Tests.csproj \
    -c Release --no-build --no-restore "${artifact_args[@]}" \
    --logger "trx;LogFileName=phase5-linux-application.trx" \
    --results-directory "$results_dir"
dotnet test tests/CipherVault.Server.Tests/CipherVault.Server.Tests.csproj \
    -c Release --no-build --no-restore "${artifact_args[@]}" \
    --logger "trx;LogFileName=phase5-linux-server.trx" \
    --results-directory "$results_dir"

for trx in \
    "$results_dir/phase5-linux-core.trx" \
    "$results_dir/phase5-linux-application.trx" \
    "$results_dir/phase5-linux-server.trx"; do
    if ! grep -q 'notExecuted="0"' "$trx"; then
        echo "Phase 5 TRX contains skipped tests: $trx" >&2
        exit 1
    fi
done

echo "Phase 5 Linux verification passed."
echo "TRX: $results_dir/phase5-linux-core.trx"
echo "TRX: $results_dir/phase5-linux-application.trx"
echo "TRX: $results_dir/phase5-linux-server.trx"
