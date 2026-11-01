#!/usr/bin/env bash
set -euo pipefail

readonly v2_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "${v2_root}/deploy/versions.env"

platform=""
publish_root=""
output_root=""
version="${RELEASE_VERSION}"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --platform) platform="${2:-}"; shift 2 ;;
        --publish-root) publish_root="${2:-}"; shift 2 ;;
        --output-root) output_root="${2:-}"; shift 2 ;;
        --version) version="${2:-}"; shift 2 ;;
        *) echo "package_arguments_invalid" >&2; exit 2 ;;
    esac
done

if [[ "$platform" != "linux" || -z "$publish_root" || -z "$output_root" ]]; then
    echo "package_arguments_invalid" >&2
    exit 2
fi
test -d "$publish_root"
mkdir -p "$output_root"
output_root="$(realpath "$output_root")"
publish_root="$(realpath "$publish_root")"
readonly wrapper_name="CipherVault-${version}-linux-x64"
readonly archive="${output_root}/CipherVault-${version}-linux-x64.tar.gz"
staging="$(mktemp -d)"
trap 'rm -rf -- "$staging"' EXIT

wrapper="${staging}/${wrapper_name}"
mkdir -p "$wrapper"
mkdir -p "${wrapper}/publish"
cp -a "${publish_root}/." "${wrapper}/publish/"
cp -a "${v2_root}/deploy/linux/." "$wrapper/"
find "${wrapper}/publish" -type f -exec chmod 0644 {} +
chmod 0755 "${wrapper}/publish/CipherVault.Server"
chmod 0755 "${wrapper}/install-systemd.sh" "${wrapper}/uninstall-systemd.sh"
chmod 0644 "${wrapper}/cipher-vault.service" "${wrapper}/README.md"

if find "$wrapper" -type f \
    \( -name '*.pdb' -o -name '*.cs' -o -name '*.csproj' -o -name '*.slnx' \
       -o -name 'appsettings.Development.json' -o -name '*.tmp' -o -name '*.partial' \) |
    grep -q .; then
    echo "package_forbidden_file" >&2
    exit 4
fi

tar \
    --sort=name \
    --owner=0 \
    --group=0 \
    --numeric-owner \
    -czf "$archive" \
    -C "$staging" "$wrapper_name"

verify_root="${staging}/verify"
mkdir -p "$verify_root"
tar -xzf "$archive" -C "$verify_root"
test -x "${verify_root}/${wrapper_name}/publish/CipherVault.Server"
test -f "${verify_root}/${wrapper_name}/publish/wwwroot/index.html"
test -x "${verify_root}/${wrapper_name}/install-systemd.sh"
test -f "${verify_root}/${wrapper_name}/cipher-vault.service"

echo "$archive"
