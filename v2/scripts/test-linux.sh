#!/usr/bin/env bash
set -euo pipefail

readonly v2_root="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
readonly repo_root="$(cd "${v2_root}/.." && pwd)"
readonly solution="${repo_root}/CipherVault.slnx"
readonly log_prefix="${2:-phase1-linux}"
readonly results_dir="${v2_root}/artifacts/test-results/linux"
artifact_args=()

if [[ -n "${CV_DOTNET_ARTIFACTS_PATH:-}" ]]; then
    artifact_args+=(--artifacts-path "$CV_DOTNET_ARTIFACTS_PATH")
fi

export PATH="${HOME}/.dotnet:${PATH}"

cd "$v2_root"
dotnet restore "$solution" --locked-mode "${artifact_args[@]}"
dotnet build "$solution" -c Release --no-restore "${artifact_args[@]}"
rm -rf "$results_dir"
mkdir -p "$results_dir"
dotnet test tests/CipherVault.Core.Tests/CipherVault.Core.Tests.csproj \
    -c Release --no-build --no-restore "${artifact_args[@]}" \
    --logger "trx;LogFileName=${log_prefix}-core.trx" \
    --results-directory "$results_dir"
dotnet test tests/CipherVault.Application.Tests/CipherVault.Application.Tests.csproj \
    -c Release --no-build --no-restore "${artifact_args[@]}" \
    --logger "trx;LogFileName=${log_prefix}-application.trx" \
    --results-directory "$results_dir"

echo "Test results: $results_dir"
