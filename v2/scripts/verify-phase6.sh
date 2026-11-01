#!/usr/bin/env bash
set -euo pipefail

readonly v2_root="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
readonly repo_root="$(cd "${v2_root}/.." && pwd)"
readonly solution="${repo_root}/CipherVault.slnx"
readonly index_path="${v2_root}/src/CipherVault.Server/wwwroot/index.html"

if [[ ! -f "$index_path" ]]; then
    echo "Frontend build is missing. Run npm run build on the development host first." >&2
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
    --logger "trx;LogFileName=phase6-linux-core.trx" \
    --results-directory "$results_dir"
dotnet test tests/CipherVault.Application.Tests/CipherVault.Application.Tests.csproj \
    -c Release --no-build --no-restore "${artifact_args[@]}" \
    --logger "trx;LogFileName=phase6-linux-application.trx" \
    --results-directory "$results_dir"
dotnet test tests/CipherVault.Server.Tests/CipherVault.Server.Tests.csproj \
    -c Release --no-build --no-restore "${artifact_args[@]}" \
    --logger "trx;LogFileName=phase6-linux-server.trx" \
    --results-directory "$results_dir"

for trx in \
    "$results_dir/phase6-linux-core.trx" \
    "$results_dir/phase6-linux-application.trx" \
    "$results_dir/phase6-linux-server.trx"; do
    if ! grep -q 'notExecuted="0"' "$trx"; then
        echo "Phase 6 TRX contains skipped tests: $trx" >&2
        exit 1
    fi
    if ! grep -q 'failed="0"' "$trx"; then
        echo "Phase 6 TRX contains failed tests: $trx" >&2
        exit 1
    fi
done

echo "Phase 6 Linux verification passed."
echo "TRX: $results_dir/phase6-linux-core.trx"
echo "TRX: $results_dir/phase6-linux-application.trx"
echo "TRX: $results_dir/phase6-linux-server.trx"
