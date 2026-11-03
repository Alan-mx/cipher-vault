#!/usr/bin/env bash
set -euo pipefail

readonly service_name="cipher-vault.service"
readonly unit_path="/etc/systemd/system/${service_name}"
readonly dropin_dir="/etc/systemd/system/${service_name}.d"
readonly dropin_path="${dropin_dir}/50-tailscale.conf"
readonly local_url="http://127.0.0.1:43127"
readonly serve_port="43127"

disable=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --disable)
            disable=1
            shift
            ;;
        *)
            echo "tailscale_arguments_invalid" >&2
            exit 2
            ;;
    esac
done

if [[ "${EUID}" -ne 0 ]]; then
    echo "tailscale_requires_root" >&2
    exit 3
fi
for tool in tailscale systemctl curl python3 install mktemp uname find rmdir; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        echo "tailscale_tool_missing:$tool" >&2
        exit 4
    fi
done
if ! systemctl is-active --quiet tailscaled.service; then
    echo "tailscale_daemon_inactive" >&2
    exit 4
fi
if [[ ! -f "$unit_path" ]]; then
    echo "tailscale_cipher_vault_unit_missing" >&2
    exit 6
fi

temporary_root="$(mktemp -d)"
trap 'rm -rf -- "$temporary_root"' EXIT

if [[ "$disable" -eq 0 ]]; then
    if ! tailscale status --json >"${temporary_root}/status.json"; then
        echo "tailscale_status_unavailable" >&2
        exit 5
    fi
    identity="$(
        python3 - "${temporary_root}/status.json" <<'PY'
import ipaddress
import json
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    status = json.load(stream)
self_node = status.get("Self") or {}
dns_name = str(self_node.get("DNSName") or "").rstrip(".")
if not dns_name.endswith(".ts.net") or dns_name.count(".") < 3:
    raise SystemExit(5)
ipv4 = ""
ipv6 = ""
for value in self_node.get("TailscaleIPs") or []:
    address = ipaddress.ip_address(value)
    if address.version == 4 and address in ipaddress.ip_network(
            "100.64.0.0/10"):
        ipv4 = str(address)
    elif address.version == 6 and address in ipaddress.ip_network(
            "fd7a:115c:a1e0::/48"):
        ipv6 = str(address)
if not ipv4:
    raise SystemExit(5)
print(f"{dns_name}\t{ipv4}\t{ipv6}")
PY
    )"
    IFS=$'\t' read -r magic_dns tailscale_ipv4 tailscale_ipv6 <<<"$identity"
    if [[ -z "$magic_dns" || -z "$tailscale_ipv4" ]]; then
        echo "tailscale_identity_unavailable" >&2
        exit 5
    fi

    allowed_hosts="127.0.0.1,localhost,${magic_dns},${tailscale_ipv4}"
    if [[ -n "$tailscale_ipv6" ]]; then
        allowed_hosts+=",${tailscale_ipv6}"
    fi

    install -d -o root -g root -m 0755 "$dropin_dir"
    temporary_config="${temporary_root}/50-tailscale.conf"
    {
        echo "[Service]"
        echo "Environment=\"CIPHERVAULT_ALLOWED_HOSTS=${allowed_hosts}\""
    } >"$temporary_config"
    install \
        -o root \
        -g root \
        -m 0644 \
        "$temporary_config" \
        "$dropin_path"

    if ! systemctl daemon-reload ||
        ! systemctl restart "$service_name"; then
        echo "tailscale_service_restart_failed" >&2
        exit 6
    fi
    for _ in $(seq 1 120); do
        if curl -fsS "${local_url}/api/vault/status" >/dev/null 2>&1; then
            break
        fi
        sleep 0.25
    done
    if ! curl -fsS \
        -H "Host: ${magic_dns}:${serve_port}" \
        "${local_url}/api/version" \
        >"${temporary_root}/version.json"; then
        echo "tailscale_host_validation_failed" >&2
        exit 8
    fi
    if ! python3 - "${temporary_root}/version.json" "$(uname -m)" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    version = json.load(stream)
expected = {
    "x86_64": "linux-x64",
    "aarch64": "linux-arm64",
}.get(sys.argv[2])
if expected is None or version.get("rid") != expected:
    raise SystemExit("tailscale_version_rid_invalid")
PY
    then
        echo "tailscale_version_validation_failed" >&2
        exit 8
    fi

    if ! tailscale serve \
        --bg \
        --tcp="${serve_port}" \
        "tcp://127.0.0.1:${serve_port}"; then
        echo "tailscale_serve_enable_failed" >&2
        exit 7
    fi
    if ! tailscale serve status --json >"${temporary_root}/serve.json"; then
        echo "tailscale_serve_status_failed" >&2
        exit 7
    fi
    if ! python3 - \
        "${temporary_root}/serve.json" \
        "$serve_port" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    status = json.load(stream)
tcp = status.get("TCP") or {}
entry = tcp.get(sys.argv[2]) or {}
if entry.get("TCPForward") != "127.0.0.1:43127":
    raise SystemExit("tailscale_serve_status_invalid")
PY
    then
        echo "tailscale_serve_validation_failed" >&2
        exit 7
    fi

    echo "tailscale_serve_enabled:${magic_dns}:${serve_port}"
    exit 0
fi

tailscale serve --tcp="${serve_port}" off >/dev/null 2>&1 || true
if ! tailscale serve status --json >"${temporary_root}/serve.json"; then
    echo "tailscale_serve_status_failed" >&2
    exit 7
fi
if ! python3 - "${temporary_root}/serve.json" "$serve_port" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    status = json.load(stream)
if sys.argv[2] in (status.get("TCP") or {}):
    raise SystemExit("tailscale_serve_still_enabled")
PY
then
    echo "tailscale_serve_disable_failed" >&2
    exit 7
fi
if [[ -f "$dropin_path" ]]; then
    rm -f -- "$dropin_path"
fi
if [[ -d "$dropin_dir" ]] && [[ -z "$(find "$dropin_dir" -type f -print -quit)" ]]; then
    rmdir -- "$dropin_dir"
fi
if ! systemctl daemon-reload ||
    ! systemctl restart "$service_name"; then
    echo "tailscale_service_restart_failed" >&2
    exit 6
fi
for _ in $(seq 1 120); do
    if curl -fsS "${local_url}/api/vault/status" >/dev/null 2>&1; then
        echo "tailscale_serve_disabled"
        exit 0
    fi
    sleep 0.25
done
echo "tailscale_loopback_unavailable" >&2
exit 8
