#!/usr/bin/env bash
set -euo pipefail

readonly v2_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "${v2_root}/deploy/versions.env"

platform=""
publish_root=""
output_root=""
version="${RELEASE_VERSION}"
rid="linux-x64"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --platform) platform="${2:-}"; shift 2 ;;
        --rid) rid="${2:-}"; shift 2 ;;
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
case "$rid" in
    linux-x64) expected_elf_machine="x86-64" ;;
    linux-arm64) expected_elf_machine="AArch64" ;;
    *)
        echo "package_rid_invalid" >&2
        exit 2
        ;;
esac
if ! command -v readelf >/dev/null 2>&1; then
    echo "package_tool_missing:readelf" >&2
    exit 2
fi

assert_elf_architecture() {
    local path="$1"
    local expected="$2"
    local header class data machine normalized

    if [[ ! -f "$path" ]] ||
        ! header="$(LC_ALL=C readelf -h "$path" 2>/dev/null)"; then
        echo "package_elf_invalid:$path" >&2
        return 1
    fi
    class="$(awk '$1 == "Class:" { print $2; exit }' <<<"$header")"
    data="$(awk '$1 == "Data:" { print; exit }' <<<"$header")"
    machine="$(
        awk '$1 == "Machine:" {
            sub(/^[[:space:]]*Machine:[[:space:]]*/, "")
            print
            exit
        }' <<<"$header"
    )"
    normalized="$machine"
    if [[ "$machine" == "Advanced Micro Devices X86-64" ]]; then
        normalized="x86-64"
    fi
    if [[ "$class" != "ELF64" ||
        "$data" != *"little endian"* ||
        "$normalized" != "$expected" ]]; then
        echo "package_elf_invalid:$path" >&2
        return 1
    fi
}

if [[ ! -d "$publish_root" ]]; then
    echo "package_publish_root_missing" >&2
    exit 2
fi
mkdir -p "$output_root"
output_root="$(realpath "$output_root")"
publish_root="$(realpath "$publish_root")"
readonly architecture="${rid#linux-}"
readonly wrapper_name="CipherVault-${version}-linux-${architecture}"
readonly archive="${output_root}/${wrapper_name}.tar.gz"

if [[ ! -x "${publish_root}/CipherVault.Server" ]]; then
    echo "package_server_missing" >&2
    exit 4
fi
if [[ -z "$(
    find "$publish_root" -type f -name 'libsqlite3mc.so' -print -quit
)" ]]; then
    echo "package_native_missing" >&2
    exit 4
fi
if ! assert_elf_architecture \
    "${publish_root}/CipherVault.Server" \
    "$expected_elf_machine"; then
    exit 4
fi
while IFS= read -r native_file; do
    if ! assert_elf_architecture \
        "$native_file" \
        "$expected_elf_machine"; then
        exit 4
    fi
done < <(find "$publish_root" -type f -name 'libsqlite3mc.so')

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
if [[ ! -x "${verify_root}/${wrapper_name}/publish/CipherVault.Server" ||
    ! -f "${verify_root}/${wrapper_name}/publish/wwwroot/index.html" ||
    ! -x "${verify_root}/${wrapper_name}/install-systemd.sh" ||
    ! -f "${verify_root}/${wrapper_name}/cipher-vault.service" ]]; then
    echo "package_layout_invalid" >&2
    exit 4
fi
if ! assert_elf_architecture \
    "${verify_root}/${wrapper_name}/publish/CipherVault.Server" \
    "$expected_elf_machine"; then
    exit 4
fi
while IFS= read -r native_file; do
    if ! assert_elf_architecture \
        "$native_file" \
        "$expected_elf_machine"; then
        exit 4
    fi
done < <(find "${verify_root}/${wrapper_name}/publish" \
    -type f -name 'libsqlite3mc.so')

echo "$archive"
