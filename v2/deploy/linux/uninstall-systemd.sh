#!/usr/bin/env bash
set -euo pipefail

readonly install_root="/opt/cipher-vault"
readonly previous_root="/opt/cipher-vault.previous"
readonly unit_path="/etc/systemd/system/cipher-vault.service"
readonly service_name="cipher-vault.service"

if [[ $# -ne 0 ]]; then
    echo "uninstall_arguments_invalid" >&2
    exit 2
fi
if [[ "${EUID}" -ne 0 ]]; then
    echo "uninstall_requires_root" >&2
    exit 3
fi
if ! command -v systemctl >/dev/null 2>&1; then
    echo "systemctl_unavailable" >&2
    exit 4
fi

systemctl disable --now "$service_name" >/dev/null 2>&1 || true
rm -f -- "$unit_path"
rm -rf -- "$install_root" "$previous_root"
find /opt -maxdepth 1 -type d -name '.cipher-vault.new.*' \
    -exec rm -rf -- {} + 2>/dev/null || true
systemctl daemon-reload

echo "uninstall_completed"
echo "Persistent Vault data remains in /var/lib/cipher-vault."
