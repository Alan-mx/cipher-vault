#!/usr/bin/env bash
set -euo pipefail

readonly install_root="/opt/cipher-vault"
readonly previous_root="/opt/cipher-vault.previous"
readonly state_root="/var/lib/cipher-vault"
readonly unit_path="/etc/systemd/system/cipher-vault.service"
readonly service_name="cipher-vault.service"
readonly service_user="cipher-vault"

if [[ $# -ne 0 ]]; then
    echo "install_arguments_invalid" >&2
    exit 2
fi
if [[ "${EUID}" -ne 0 ]]; then
    echo "install_requires_root" >&2
    exit 3
fi
if ! command -v systemctl >/dev/null 2>&1; then
    echo "systemctl_unavailable" >&2
    exit 4
fi

script_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source_root="${script_root}/publish"
if [[ ! -x "${source_root}/CipherVault.Server" ]]; then
    echo "install_publish_missing" >&2
    exit 5
fi

if ! getent group "$service_user" >/dev/null 2>&1; then
    groupadd --system "$service_user"
fi
if ! id "$service_user" >/dev/null 2>&1; then
    useradd \
        --system \
        --gid "$service_user" \
        --home-dir /nonexistent \
        --shell /usr/sbin/nologin \
        "$service_user"
fi

install -d -o root -g root -m 0755 /opt
new_root="/opt/.cipher-vault.new.$$"
rm -rf -- "$new_root"
install -d -o root -g root -m 0755 "$new_root"
cp -a "${source_root}/." "$new_root/"
chown -R root:root "$new_root"
chmod -R go-w "$new_root"
find "$new_root" -type d -exec chmod 0755 {} +
find "$new_root" -type f ! -perm -u+x -exec chmod 0644 {} +
find "$new_root" -type f -perm -u+x -exec chmod 0755 {} +

if systemctl is-active --quiet "$service_name"; then
    systemctl stop "$service_name"
fi

rm -rf -- "$previous_root"
previous_available=0
if [[ -d "$install_root" ]]; then
    mv "$install_root" "$previous_root"
    previous_available=1
fi
if ! mv "$new_root" "$install_root"; then
    if [[ "$previous_available" -eq 1 && -d "$previous_root" ]]; then
        mv "$previous_root" "$install_root"
    fi
    echo "install_program_replace_failed" >&2
    exit 5
fi

install -d -o "$service_user" -g "$service_user" -m 0700 \
    "$state_root" \
    "${state_root}/data" \
    "${state_root}/config" \
    "${state_root}/backups"
install -o root -g root -m 0644 \
    "${script_root}/cipher-vault.service" \
    "$unit_path"

systemctl daemon-reload
if ! systemctl enable --now "$service_name"; then
    echo "install_service_start_failed" >&2
    exit 5
fi
service_stable=0
for _ in $(seq 1 40); do
    if systemctl is-active --quiet "$service_name"; then
        service_stable=$((service_stable + 1))
        if [[ "$service_stable" -ge 4 ]]; then
            break
        fi
    else
        service_stable=0
    fi
    sleep 0.25
done
if [[ "$service_stable" -lt 4 ]]; then
    journalctl -u "$service_name" -n 20 --no-pager >&2 || true
    systemctl stop "$service_name" >/dev/null 2>&1 || true
    rm -rf -- "$install_root"
    if [[ "$previous_available" -eq 1 && -d "$previous_root" ]]; then
        mv "$previous_root" "$install_root"
        systemctl restart "$service_name" >/dev/null 2>&1 || true
    else
        systemctl disable "$service_name" >/dev/null 2>&1 || true
        rm -f -- "$unit_path"
    fi
    systemctl daemon-reload
    echo "install_service_start_failed" >&2
    exit 5
fi
systemctl --no-pager --full status "$service_name" | sed -n '1,12p'
echo "install_completed"
