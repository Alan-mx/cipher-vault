#!/usr/bin/env bash
set -euo pipefail

readonly image="cipher-vault-phase0:2.6.1"
readonly image_digest="sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2"
readonly repo_root="/mnt/d/workspace/cipher-vault"
readonly artifact_dir="/home/lyman/phase0-docker-artifacts-$(date +%Y%m%d-%H%M%S)"
readonly probe="/src/CipherVault.PlainSqliteProbe/bin/Release/net10.0/CipherVault.PlainSqliteProbe.dll"

if [[ "$(id -u)" -ne 0 ]]; then
    echo "Run this script as root so it can prepare and copy the artifact directory." >&2
    exit 3
fi

if ! docker image inspect "$image" >/dev/null 2>&1; then
    echo "Docker image not found: $image" >&2
    exit 3
fi

mkdir -p "$artifact_dir"
chown 10001:10001 "$artifact_dir"

docker run --rm \
    --user 10001:10001 \
    -e "CV_PHASE0_IMAGE_DIGEST=$image_digest" \
    -v "$artifact_dir:/artifacts" \
    "$image" \
    run \
    --artifacts-root /artifacts \
    --platform docker \
    --plain-sqlite-probe "$probe"

chown -R lyman:lyman "$artifact_dir"
mkdir -p "$repo_root/poc/artifacts/phase0"
cp -a "$artifact_dir"/. "$repo_root/poc/artifacts/phase0/"

echo "Docker Phase 0 artifacts copied to:"
echo "  $repo_root/poc/artifacts/phase0"
