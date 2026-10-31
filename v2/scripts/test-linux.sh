#!/usr/bin/env bash
set -euo pipefail

readonly v2_root="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
readonly results_dir="${v2_root}/artifacts/test-results"

export PATH="${HOME}/.dotnet:${PATH}"

cd "$v2_root"
dotnet restore CipherVault.V2.slnx --locked-mode
dotnet build CipherVault.V2.slnx -c Release --no-restore
rm -rf "$results_dir"
mkdir -p "$results_dir"
dotnet test CipherVault.V2.slnx -c Release --no-build --no-restore \
    --logger "trx;LogFileName=phase1-linux.trx" \
    --results-directory "$results_dir"

echo "Test results: $results_dir"
