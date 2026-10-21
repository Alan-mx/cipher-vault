# CipherVault

Windows 11 local encrypted Markdown/TXT vault with one shared Host process and
Streamable HTTP MCP access.

## Architecture

```text
CipherVault.Ui.Wpf / CipherVault.Cli / CipherVault.Mcp
                         |
                  Admin API or MCP HTTP
                         |
                  CipherVault.Host.exe
                         |
              CipherVault.Core + SQLCipher
```

- `CipherVault.Host.exe` owns the only active Vault session, SQLCipher connection,
  keys, lock timeout and audit stream.
- Only one Host process may run for each Windows user, regardless of Vault or port.
- WPF and CLI use the protected `/admin/v1` API.
- AI clients use Streamable HTTP at `/mcp` with a per-client Bearer Token.
- `CipherVault.Mcp.exe` remains a thin STDIO adapter for clients without HTTP
  MCP support. It forwards to Host and never opens the Vault itself.
- The Host binds only to `127.0.0.1`. It is not installed as a Windows service
  and does not create a login startup entry.

## Security model

- SQLCipher encrypts document content, metadata, revisions, recycle-bin items,
  FTS and audit records.
- The password and offline recovery key independently unwrap a random master key.
- Recovery-key confirmation is required before document access.
- Recovery-key rotation remains pending until the new key is confirmed.
- Unlocking once in Host is shared by WPF, CLI, HTTP MCP and STDIO bridge clients.
- Agent Tokens are stored only as SHA-256 hashes and are shown once when created
  or rotated.
- MCP clients default to `readOnly`; write access must be assigned explicitly.
- Admin and Agent Tokens use separate routes and cannot substitute for each other.
- WPF and CLI use separate DPAPI-protected Admin Tokens, so audit identity is
  derived from the authenticated token rather than a client-supplied header.
- Static Bearer Tokens are a trusted-local-client model, not full MCP OAuth.
- Requests with an `Origin` header are rejected unless the exact origin is listed
  in `host.allowedOrigins`.
- Host and Origin headers, request-size limits and authentication-failure rate
  limits are enforced before vault content access.
- Sessions lock after 15 minutes without Windows keyboard/mouse activity, after
  4 hours, on screen lock, or on explicit lock.
- Vault files, configuration and local logs use protected Windows ACLs.
- The Host retains database keys only while the global Vault session is unlocked.

Windows administrators, kernel malware and active process-memory attacks remain
outside scope. Content already returned to an AI client cannot be recalled by a
later lock operation.

## First use and recovery

1. Start `CipherVault.Ui.Wpf.exe` or run an online CLI command.
2. Choose a new Vault directory and initialize it with a password of at least
   12 characters.
3. Store the offline recovery key outside the Vault, machine and backup files.
4. Re-enter the recovery key to confirm it was saved.
5. Unlock with either the password or the recovery key.

Losing both the password and recovery key makes the Vault unrecoverable.

WPF normal close leaves the Host session unlocked. Use **退出并锁定** to lock the
global session before closing, or **停止 Host** to lock and terminate the Host.
In Host mode, credential input is accepted only by the Host authorization window.

## Configuration

The Host, WPF, CLI and STDIO bridge use `configVersion: 2`:

```text
%LOCALAPPDATA%\CipherVault\config.json
```

```json
{
  "configVersion": 2,
  "vaultPath": "%USERPROFILE%\\CipherVault",
  "session": {
    "idleMinutes": 15,
    "hardHours": 4
  },
  "host": {
    "bindAddress": "127.0.0.1",
    "port": 43127,
    "mcpPath": "/mcp",
    "adminPath": "/admin/v1",
    "maxRequestBodyBytes": 4194304,
    "allowedOrigins": []
  },
  "clients": []
}
```

Do not put passwords, recovery keys, Agent Tokens or Admin Tokens in this file.
WPF and CLI use separate DPAPI-protected Admin Tokens:

```text
%LOCALAPPDATA%\CipherVault\admin-ui-token.bin
%LOCALAPPDATA%\CipherVault\admin-cli-token.bin
```

`--config` or `CIPHER_VAULT_CONFIG` can select another configuration file.
Relative `vaultPath` values are resolved against that file's directory.
Configurations with `configVersion: 1` are rejected; only version 2 is supported.

## Starting Host

Start Host explicitly with:

```powershell
.\CipherVault.Host.exe --config .\config.json
```

WPF and online CLI commands can also ensure Host is running. HTTP-only agents do
not start Host. The STDIO bridge also does not start Host; if Host is unavailable,
tool calls return `host_unavailable`.

Stop a running Host explicitly:

```powershell
.\CipherVault.Cli.exe host-stop --config .\config.json
```

## Agent Tokens

Create a read-only client:

```powershell
.\CipherVault.Cli.exe client-add --config .\config.json `
  --id codex --access readOnly
```

Create a write-enabled client:

```powershell
.\CipherVault.Cli.exe client-add --config .\config.json `
  --id codex-writer --access readWrite
```

The command prints the plaintext token once. Store it in the agent's environment,
not in the CipherVault configuration. Other management commands:

```powershell
.\CipherVault.Cli.exe client-list --config .\config.json
.\CipherVault.Cli.exe client-rotate --config .\config.json --id codex
.\CipherVault.Cli.exe client-delete --config .\config.json --id codex
```

## HTTP MCP

Use an Agent Token environment variable such as `CIPHER_VAULT_CODEX_TOKEN`:

```toml
[mcp_servers.cipher_vault]
url = "http://127.0.0.1:43127/mcp"
bearer_token_env_var = "CIPHER_VAULT_CODEX_TOKEN"
tool_timeout_sec = 120
default_tools_approval_mode = "prompt"
```

The HTTP transport is SDK-managed Streamable HTTP and uses stateless MCP protocol
sessions. Vault unlock state is maintained independently by Host.

## STDIO Bridge

Use the bridge only when the client cannot configure Streamable HTTP:

```toml
[mcp_servers.cipher_vault]
command = 'D:\path\to\CipherVault.Mcp.exe'
args = ['--config', 'D:\path\to\config.json', '--read-only']
tool_timeout_sec = 120
default_tools_approval_mode = "prompt"
```

Set `CIPHER_VAULT_MCP_TOKEN` for the bridge process. A different variable can be
selected with `--token-env`. `--read-only` is only a local upper bound; Host Token
permissions remain authoritative.

The bridge always derives its HTTP endpoint from the validated CipherVault
configuration. It does not support an external URL override, so the Agent Token
cannot be redirected away from the configured loopback Host.

## CLI modes

CLI commands use Host by default:

```powershell
.\CipherVault.Cli.exe status --config .\config.json
.\CipherVault.Cli.exe search --config .\config.json --query "invoice"
.\CipherVault.Cli.exe backup --config .\config.json --to .\vault.cvbackup
```

Explicit direct mode is available for offline maintenance:

```powershell
.\CipherVault.Cli.exe status --standalone --config .\config.json
```

`--standalone` acquires the `VaultOwnershipLock` before opening the database and
fails with `vault_in_use` if Host or another standalone process owns the Vault.

## Backups, restore and export

- Backup creates a consistent encrypted `.cvbackup`.
- Restore-new creates a separate Vault directory and does not replace the active
  Vault.
- Online overwrite of the active Vault is rejected. Stop Host and use the explicit
  offline restore path.
- Plaintext export is an explicit user operation outside the Vault encryption
  boundary. Protect or delete exported files appropriately.

## Projects

- `CipherVault.Core`: encryption, SQLCipher storage, document operations and audit
- `CipherVault.Host`: loopback HTTP service, global session and MCP HTTP endpoint
- `CipherVault.Host.Client`: shared Admin API client used by WPF and CLI
- `CipherVault.Ui.Wpf`: desktop UI
- `CipherVault.Cli`: online management commands and explicit offline mode
- `CipherVault.Mcp`: STDIO-to-Host compatibility bridge

## Build and test

```powershell
dotnet build .\CipherVault.sln -c Release
dotnet test .\CipherVault.sln -c Release --no-build
```

## Publish

```powershell
.\scripts\publish.ps1
```

The script creates a Windows x64 ZIP containing the desktop, Host, CLI, MCP bridge,
README and LICENSE.

## License

CipherVault is released under the MIT License. See `LICENSE`.
