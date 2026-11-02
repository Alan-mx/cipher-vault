# CipherVault Docker

The Docker release uses a self-contained linux/amd64 image. Vault data lives in the
explicit named Volume `cipher-vault-data`.

```bash
sha256sum -c SHA256SUMS
```

SHA-256 detects corrupted or modified files. It does not authenticate the publisher.

## Run

```bash
docker load --input cipher-vault-2.0.0-rc.1-docker-linux-amd64.tar
CIPHERVAULT_IMAGE=cipher-vault:2.0.0-rc.1 \
docker compose up -d
```

Open:

```text
http://127.0.0.1:8080
```

Only the host loopback address is published. The container itself listens on port 8080.
If the host mapping uses another port, set `CIPHERVAULT_PORT` to the same public port:

```bash
CIPHERVAULT_PORT=18080 docker compose up -d
```

`CIPHERVAULT_PUBLIC_HTTP_PORTS` follows that mapping and controls the accepted HTTP
`Host` and `Origin` port. The container's internal port does not automatically grant
access to the same public port.

Host and Origin validation is not a network boundary. The Compose mapping must remain
bound to `127.0.0.1`; do not attach the container to an untrusted shared network. Other
containers on the same Docker network can still reach the container port. Authentication,
authorization, TLS and network isolation remain separate deployment requirements.

## Persistence

```bash
docker compose down
```

This removes the container but keeps `cipher-vault-data`.

```bash
docker compose down -v
```

This deletes the named Volume and permanently deletes the Vault, configuration, MCP Token
file and encrypted backups. Do not run it unless permanent deletion is intended.

## Upgrade

Load the new image, update `CIPHERVAULT_IMAGE` if needed, and recreate the container:

```bash
docker compose up -d --force-recreate
```

The named Volume is preserved.

## Health and logs

```bash
docker inspect --format '{{.State.Health.Status}}' cipher-vault
docker logs cipher-vault
```

A healthy container does not mean the Vault is unlocked.

## Offline restore

Stop the Compose service and mount the backup directory read-only plus a writable target
parent. Run the same image in an interactive terminal:

```bash
docker run --rm -it \
  -v /host/backups:/backup:ro \
  -v /host/restore-parent:/restore \
  cipher-vault:2.0.0-rc.1 \
  --restore-backup /backup/backup.cvbackup \
  --target /restore/new-vault
```

The restore target must be a new directory.
