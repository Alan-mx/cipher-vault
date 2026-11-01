#!/usr/bin/env bash
set -euo pipefail

readonly v2_root="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
readonly probe_project="${v2_root}/tests/CipherVault.Phase7.ProcessProbe/CipherVault.Phase7.ProcessProbe.csproj"
readonly password="phase7 cross platform password"
readonly stamp="$(date +%Y%m%d-%H%M%S)"
readonly cross_root="${v2_root}/artifacts/cross-platform/phase7"
readonly windows_artifact_root="${cross_root}/windows"
readonly linux_artifact_root="${cross_root}/linux"
readonly windows_package="${windows_artifact_root}/latest-windows.cvbackup"
readonly linux_package="${linux_artifact_root}/latest-linux.cvbackup"

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
    --logger "trx;LogFileName=phase7-linux-core.trx" \
    --results-directory "$results_dir"
dotnet test tests/CipherVault.Application.Tests/CipherVault.Application.Tests.csproj \
    -c Release --no-build --no-restore "${artifact_args[@]}" \
    --logger "trx;LogFileName=phase7-linux-application.trx" \
    --results-directory "$results_dir"
dotnet test tests/CipherVault.Server.Tests/CipherVault.Server.Tests.csproj \
    -c Release --no-build --no-restore "${artifact_args[@]}" \
    --logger "trx;LogFileName=phase7-linux-server.trx" \
    --results-directory "$results_dir"

for trx in \
    "$results_dir/phase7-linux-core.trx" \
    "$results_dir/phase7-linux-application.trx" \
    "$results_dir/phase7-linux-server.trx"; do
    grep -q 'notExecuted="0"' "$trx"
    grep -q 'failed="0"' "$trx"
done

mkdir -p "$linux_artifact_root"
readonly run_root="${cross_root}/linux/run-${stamp}"
dotnet run --project "$probe_project" -c Release -- \
    create "${run_root}/source" "${run_root}/backups" \
    "$linux_package" "$password"
dotnet run --project "$probe_project" -c Release -- \
    restore "$linux_package" "${run_root}/restored" "$password"
dotnet run --project "$probe_project" -c Release -- \
    verify "${run_root}/restored" "$password"

if [[ -f "$windows_package" ]]; then
    dotnet run --project "$probe_project" -c Release -- \
        restore "$windows_package" "${run_root}/restored-from-windows" "$password"
    dotnet run --project "$probe_project" -c Release -- \
        verify "${run_root}/restored-from-windows" "$password"
fi

echo "Phase 7 Linux verification passed."
echo "TRX: $results_dir/phase7-linux-core.trx"
echo "TRX: $results_dir/phase7-linux-application.trx"
echo "TRX: $results_dir/phase7-linux-server.trx"
echo "Linux restore artifact: $linux_package"
