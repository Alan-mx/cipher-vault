# syntax=docker/dockerfile:1

ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.402-noble@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2
ARG RUNTIME_DEPS_IMAGE=mcr.microsoft.com/dotnet/runtime-deps:10.0.13-noble@sha256:1c20bc9ebda297198be069d82b6c5d1716faeeec8b8dca53ef963f9c92d3e2e3
ARG RUNTIME_DEPS_DIGEST=sha256:1c20bc9ebda297198be069d82b6c5d1716faeeec8b8dca53ef963f9c92d3e2e3

FROM ${SDK_IMAGE} AS build
ARG RELEASE_VERSION=0.0.0-local
ARG BUILD_COMMIT=local
ARG BUILD_TIMESTAMP_UTC=1970-01-01T00:00:00.0000000Z

WORKDIR /src

COPY Directory.Build.props global.json ./
COPY v2/Directory.Packages.props v2/
COPY v2/src/CipherVault.Core/CipherVault.Core.csproj v2/src/CipherVault.Core/
COPY v2/src/CipherVault.Core/packages.lock.json v2/src/CipherVault.Core/
COPY v2/src/CipherVault.Application/CipherVault.Application.csproj v2/src/CipherVault.Application/
COPY v2/src/CipherVault.Application/packages.lock.json v2/src/CipherVault.Application/
COPY v2/src/CipherVault.Server/CipherVault.Server.csproj v2/src/CipherVault.Server/
COPY v2/src/CipherVault.Server/packages.lock.json v2/src/CipherVault.Server/

RUN dotnet restore v2/src/CipherVault.Server/CipherVault.Server.csproj \
    --locked-mode \
    -p:NuGetAudit=false

COPY v2/src/CipherVault.Core v2/src/CipherVault.Core
COPY v2/src/CipherVault.Application v2/src/CipherVault.Application
COPY v2/src/CipherVault.Server v2/src/CipherVault.Server

RUN dotnet publish v2/src/CipherVault.Server/CipherVault.Server.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    --no-restore \
    -warnaserror \
    -p:PublishSingleFile=false \
    -p:PublishTrimmed=false \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -p:CipherVaultReleaseVersion="${RELEASE_VERSION}" \
    -p:CipherVaultBuildCommit="${BUILD_COMMIT}" \
    -p:CipherVaultBuildTimestampUtc="${BUILD_TIMESTAMP_UTC}" \
    -o /out

FROM ${RUNTIME_DEPS_IMAGE}
ARG RELEASE_VERSION=0.0.0-local
ARG BUILD_COMMIT=local
ARG RUNTIME_DEPS_IMAGE
ARG RUNTIME_DEPS_DIGEST

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*

RUN groupadd --gid 10001 cipher-vault \
    && useradd \
        --uid 10001 \
        --gid 10001 \
        --create-home \
        --shell /usr/sbin/nologin \
        cipher-vault \
    && install -d -o 10001 -g 10001 -m 0700 \
        /data \
        /data/vault \
        /data/config \
        /data/backups \
    && install -d -o 10001 -g 10001 -m 0755 /app

WORKDIR /app
COPY --from=build --chown=10001:10001 /out/ ./

LABEL org.opencontainers.image.title="CipherVault" \
      org.opencontainers.image.version="${RELEASE_VERSION}" \
      org.opencontainers.image.revision="${BUILD_COMMIT}" \
      org.opencontainers.image.base.name="mcr.microsoft.com/dotnet/runtime-deps:10.0.13-noble" \
      org.opencontainers.image.base.digest="${RUNTIME_DEPS_DIGEST}"

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    CIPHERVAULT_CONTAINER_MODE=true \
    CIPHERVAULT_DATA_DIR=/data/vault \
    CIPHERVAULT_CONFIG_DIR=/data/config \
    CIPHERVAULT_BACKUP_DIR=/data/backups

USER 10001:10001
EXPOSE 8080
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/api/vault/status || exit 1

ENTRYPOINT ["./CipherVault.Server"]
