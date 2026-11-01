# CipherVault V2

Version: `2.0.0-rc.1`

CipherVault is a single-user, local-first encrypted knowledge base. It runs as one
ASP.NET Core process containing the Vue Web UI, REST API, MCP endpoint, Vault session,
crypto layer and SQLite3MC database.

## Supported Release Targets

- Windows 11 x64 self-contained ZIP
- glibc Linux x64 self-contained tar.gz
- Ubuntu 22.04 and Debian 12 acceptance baseline
- linux/amd64 Docker image

ARM64, musl/Alpine, MSI, deb, rpm and AppImage are not part of this RC.

## Start

Windows:

```powershell
.\Start-CipherVault.ps1 -OpenBrowser
```

Linux foreground:

```bash
cd publish
./CipherVault.Server
```

Linux systemd:

```bash
sudo ./install-systemd.sh
```

Docker:

```bash
docker load --input cipher-vault-2.0.0-rc.1-docker-linux-amd64.tar
docker compose up -d
```

Default Web URL:

```text
http://127.0.0.1:43127
```

Docker host URL:

```text
http://127.0.0.1:8080
```

## MCP

HTTP endpoint:

```text
http://127.0.0.1:43127/mcp
```

Use a global Bearer Token generated from Settings. A valid Token does not unlock a
Locked Vault. The Vault must first be unlocked through the Web UI.

Generic client fields:

```json
{
  "transport": "http",
  "url": "http://127.0.0.1:43127/mcp",
  "headers": {
    "Authorization": "Bearer <token>"
  }
}
```

The Token has all enabled MCP read/write/delete capabilities. It is shown once and
cannot be retrieved later.

## Backup and Restore

Create and download encrypted `.cvbackup` files from Settings.

Offline restore requires the Server stopped and an interactive terminal:

```text
CipherVault.Server --restore-backup <file> --target <new-directory>
```

The target must not exist. The restored Vault starts Locked.

## Data and Security

- Vault data and backups are encrypted at rest.
- The Vault Password is not recoverable. A forgotten password makes the Vault and its
  encrypted backups unrecoverable.
- MCP Token and Vault Password are separate credentials.
- The Web UI defaults to loopback-only access.
- Logs do not intentionally contain content, passwords, Tokens or keys.
- SHA-256 release checks detect corruption but are not a publisher signature.

Verify downloaded files from the release directory:

```bash
sha256sum -c SHA256SUMS
```

Manual distribution is intentional for this RC. The release workflow uploads workflow
artifacts only; it does not create a GitHub Release, sign binaries, publish packages or
push an image registry.
