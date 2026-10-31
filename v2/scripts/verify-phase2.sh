#!/usr/bin/env bash
set -euo pipefail

readonly v2_root="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
readonly application_root="${v2_root}/src/CipherVault.Application"
readonly application_project="${application_root}/CipherVault.Application.csproj"
readonly runtime_file="${v2_root}/src/CipherVault.Core/Database/VaultDatabaseRuntime.cs"
readonly phase2_core_files=(
    "$runtime_file"
    "${v2_root}/src/CipherVault.Core/Database/VaultOperationContext.cs"
    "${v2_root}/src/CipherVault.Core/Database/VaultOperationResultGuard.cs"
    "${v2_root}/src/CipherVault.Core/Database/VaultCommandLease.cs"
)

readonly legacy_pattern='SQLCipher|bundle_e_sqlcipher|RecoveryKey|RecoveryVerificationRequired|recovery_slot|Agent Grant|Approval Token|Broker|Named Pipe|HardLifetime|Console\.Write|Serilog|NLog'
readonly application_pattern='Microsoft\.Data\.Sqlite|SQLitePCL|Microsoft\.AspNetCore|ModelContextProtocol|SqliteConnection|SqliteCommand|SqliteDataReader|SqliteTransaction'

mapfile -d '' application_files < <(
    find "$application_root" -type f \
        \( -name '*.cs' -o -name '*.csproj' \) \
        -not -path '*/bin/*' \
        -not -path '*/obj/*' \
        -print0
)

if grep -n -E -i "$legacy_pattern|$application_pattern" "${application_files[@]}"; then
    echo "Forbidden Application dependency or legacy reference found." >&2
    exit 1
fi

if grep -n -E -i "$application_pattern" "$application_project"; then
    echo "Forbidden Application project dependency found." >&2
    exit 1
fi

if grep -n -E -i "$legacy_pattern" "${phase2_core_files[@]}"; then
    echo "Forbidden legacy reference in Phase 2 Core." >&2
    exit 1
fi

if grep -n -E 'SqliteCommand\.Cancel|\.Command\.Cancel' "${phase2_core_files[@]}"; then
    echo "SqliteCommand.Cancel is a no-op and cannot implement Vault interrupt." >&2
    exit 1
fi

if ! grep -q 'raw\.sqlite3_interrupt(session\.Connection\.Handle)' "$runtime_file"; then
    echo "Vault interrupt must use SQLitePCL raw.sqlite3_interrupt with SqliteConnection.Handle." >&2
    exit 1
fi

bash "${v2_root}/scripts/test-linux.sh" "$v2_root" "phase2-linux"
