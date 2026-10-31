#!/usr/bin/env bash
set -euo pipefail

readonly v2_root="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
readonly application_root="${v2_root}/src/CipherVault.Application"
readonly application_project="${application_root}/CipherVault.Application.csproj"
readonly core_root="${v2_root}/src/CipherVault.Core"
readonly legacy_pattern='Recovery|IntegrityKey|HMAC|Access Grant|Approval|Broker|Named Pipe|HardLifetime'
readonly logging_pattern='Console\.Write|ILogger|Serilog|NLog'
readonly application_pattern='Microsoft\.Data\.Sqlite|SQLitePCL|Microsoft\.AspNetCore|ModelContextProtocol|SqliteConnection|SqliteCommand|SqliteDataReader|SqliteTransaction'

mapfile -d '' application_files < <(
    find "$application_root" -type f -name '*.cs' \
        -not -path '*/bin/*' \
        -not -path '*/obj/*' \
        -print0
)
mapfile -d '' domain_files < <(
    find \
        "$application_root" \
        "$core_root/Audit" \
        "$core_root/Documents" \
        "$core_root/Import" \
        "$core_root/KnowledgeGraph" \
        "$core_root/Search" \
        "$core_root/Storage" \
        -type f -name '*.cs' \
        -not -path '*/bin/*' \
        -not -path '*/obj/*' \
        -print0
)

if grep -n -E -i "$legacy_pattern|$logging_pattern|$application_pattern" \
    "${application_files[@]}"; then
    echo "Forbidden Phase 3 Application reference found." >&2
    exit 1
fi

if grep -n -E -i "$application_pattern" "$application_project"; then
    echo "Forbidden Application project dependency found." >&2
    exit 1
fi

if grep -n -E -i "$legacy_pattern|$logging_pattern" "${domain_files[@]}"; then
    echo "Forbidden Phase 3 domain reference found." >&2
    exit 1
fi

artifact_args=()
if [[ -n "${CV_DOTNET_ARTIFACTS_PATH:-}" ]]; then
    artifact_args+=(--artifacts-path "$CV_DOTNET_ARTIFACTS_PATH")
fi

export PATH="${HOME}/.dotnet:${PATH}"
cd "$v2_root"
dotnet restore CipherVault.V2.slnx --locked-mode "${artifact_args[@]}"
dotnet format CipherVault.V2.slnx --verify-no-changes --no-restore
dotnet build CipherVault.V2.slnx -c Release --no-restore \
    -warnaserror "${artifact_args[@]}"

readonly results_dir="${v2_root}/artifacts/test-results/linux"
mkdir -p "$results_dir"
dotnet test tests/CipherVault.Core.Tests/CipherVault.Core.Tests.csproj \
    -c Release --no-build --no-restore "${artifact_args[@]}" \
    --logger "trx;LogFileName=phase3-linux-core.trx" \
    --results-directory "$results_dir"
dotnet test tests/CipherVault.Application.Tests/CipherVault.Application.Tests.csproj \
    -c Release --no-build --no-restore "${artifact_args[@]}" \
    --logger "trx;LogFileName=phase3-linux-application.trx" \
    --results-directory "$results_dir"

for trx in \
    "$results_dir/phase3-linux-core.trx" \
    "$results_dir/phase3-linux-application.trx"; do
    if ! grep -q 'notExecuted="0"' "$trx"; then
        echo "Phase 3 TRX contains skipped tests: $trx" >&2
        exit 1
    fi
done

echo "Phase 3 Linux verification passed."
echo "TRX: $results_dir/phase3-linux-core.trx"
echo "TRX: $results_dir/phase3-linux-application.trx"
