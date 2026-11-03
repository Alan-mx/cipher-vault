#!/usr/bin/env bash
set -euo pipefail

readonly v2_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly repo_root="$(cd "${v2_root}/.." && pwd)"
readonly solution="${repo_root}/CipherVault.slnx"
source "${v2_root}/deploy/versions.env"

rid=""
output_root=""
version="${CIPHERVAULT_RELEASE_VERSION:-$RELEASE_VERSION}"
commit="${CIPHERVAULT_BUILD_COMMIT:-}"
skip_frontend=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rid) rid="${2:-}"; shift 2 ;;
        --output-root) output_root="${2:-}"; shift 2 ;;
        --version) version="${2:-}"; shift 2 ;;
        --commit) commit="${2:-}"; shift 2 ;;
        --skip-frontend-build) skip_frontend=1; shift ;;
        *) echo "release_arguments_invalid" >&2; exit 2 ;;
    esac
done

if [[ "$version" != 2.0.0-rc.* ]]; then
    echo "release_version_invalid" >&2
    exit 2
fi
case "$rid" in
    linux-x64) expected_elf_machine="x86-64" ;;
    linux-arm64) expected_elf_machine="AArch64" ;;
    *)
        echo "publish_rid_invalid" >&2
        exit 2
        ;;
esac
if [[ -z "$output_root" ]]; then
    echo "output_root_required" >&2
    exit 2
fi
if ! command -v readelf >/dev/null 2>&1; then
    echo "release_tool_missing:readelf" >&2
    exit 2
fi

assert_elf_architecture() {
    local path="$1"
    local expected="$2"
    local header class data machine normalized

    if [[ ! -f "$path" ]]; then
        echo "release_publish_architecture_invalid:$path" >&2
        return 1
    fi
    if ! header="$(LC_ALL=C readelf -h "$path" 2>/dev/null)"; then
        echo "release_publish_architecture_invalid:$path" >&2
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
        echo "release_publish_architecture_invalid:$path" >&2
        return 1
    fi
}

cd "$repo_root"
if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then
    echo "release_worktree_dirty" >&2
    exit 3
fi
if [[ -z "$commit" ]]; then
    commit="$(git rev-parse HEAD)"
fi
if [[ ! "$commit" =~ ^[0-9a-f]{40}$ ]]; then
    echo "release_commit_invalid" >&2
    exit 2
fi
timestamp="${CIPHERVAULT_BUILD_TIMESTAMP_UTC:-$(date -u +%Y-%m-%dT%H:%M:%S.0000000Z)}"
output_root="$(realpath -m "$output_root")"
release_base="$(realpath -m "${v2_root}/artifacts/release/${version}")"
case "${output_root}/" in
    "${release_base}/"*) ;;
    *)
        echo "release_output_root_invalid" >&2
        exit 2
        ;;
esac
if [[ "$output_root" == "$release_base" ]]; then
    echo "release_output_root_invalid" >&2
    exit 2
fi
rm -rf -- "$output_root"
mkdir -p "$output_root"

if [[ "$skip_frontend" -eq 0 ]]; then
    cd "${v2_root}/frontend"
    npm ci --no-audit --no-fund
    npm run format:check
    npm run typecheck
    npm run lint
    npm test
    npm run build
else
    test -f "${v2_root}/src/CipherVault.Server/wwwroot/index.html"
fi

cd "$v2_root"
dotnet restore "$solution" --locked-mode -p:NuGetAudit=false
dotnet publish src/CipherVault.Server/CipherVault.Server.csproj \
    -c Release \
    -r "$rid" \
    --self-contained true \
    --no-restore \
    -warnaserror \
    -p:PublishSingleFile=false \
    -p:PublishTrimmed=false \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -p:CipherVaultReleaseVersion="$version" \
    -p:CipherVaultBuildCommit="$commit" \
    -p:CipherVaultBuildTimestampUtc="$timestamp" \
    -o "${output_root}/publish"

if [[ ! -x "${output_root}/publish/CipherVault.Server" ]]; then
    echo "release_publish_missing" >&2
    exit 6
fi
if [[ ! -f "${output_root}/publish/CipherVault.Server.runtimeconfig.json" ||
    ! -f "${output_root}/publish/wwwroot/index.html" ]]; then
    echo "release_publish_layout_missing" >&2
    exit 6
fi
if [[ -z "$(
    find "${output_root}/publish" -type f -name 'libsqlite3mc.so' -print -quit
)" ]]; then
    echo "release_native_missing" >&2
    exit 6
fi
if ! assert_elf_architecture \
    "${output_root}/publish/CipherVault.Server" \
    "$expected_elf_machine"; then
    exit 6
fi
while IFS= read -r native_file; do
    if ! assert_elf_architecture \
        "$native_file" \
        "$expected_elf_machine"; then
        exit 6
    fi
done < <(find "${output_root}/publish" -type f -name 'libsqlite3mc.so')
if find "${output_root}/publish" -type f \
    \( -name '*.pdb' -o -name '*.cs' -o -name '*.tmp' -o -name '*.partial' \
       -o -name 'appsettings.Development.json' \) | grep -q .; then
    echo "release_publish_forbidden_file" >&2
    exit 6
fi

echo "$output_root/publish"
