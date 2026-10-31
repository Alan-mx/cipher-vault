#!/usr/bin/env bash
set -euo pipefail

readonly v2_root="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
readonly source_root="${v2_root}/src/CipherVault.Core"

if grep -R -n -E \
    'SQLCipher|bundle_e_sqlcipher|RecoveryKey|RecoveryVerificationRequired|recovery_slot|ILogger|Console\\.Write|Serilog|NLog' \
    "$source_root"; then
    echo "Forbidden legacy crypto reference found." >&2
    exit 1
fi

bash "${v2_root}/scripts/test-linux.sh" "$v2_root"
