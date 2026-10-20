# CipherVault

Windows 11 local encrypted Markdown/TXT vault with a STDIO MCP interface.

## Security model

- SQLCipher encrypts document content, metadata, revisions, recycle bin, FTS and audit.
- The password and offline recovery key independently unwrap a random master key.
- Recovery-key confirmation is enforced by the Vault core before content access.
- Recovery-key rotation is staged in `vault.keys.pending` until the new key is confirmed.
- MCP content tools require an interactive password prompt in the MCP process.
- MCP supports `--read-only` (or `CIPHER_VAULT_READ_ONLY=1`) to block all create,
  update, append and delete tools.
- Tool calls do not extend the session.
- Sessions lock after 15 minutes without Windows keyboard/mouse input, after 4 hours, on screen lock, or on manual lock.
- Idle and hard session limits can be configured with `--idle-minutes` and
  `--hard-hours`, or with `CIPHER_VAULT_IDLE_MINUTES` and
  `CIPHER_VAULT_HARD_HOURS`. The idle limit must be between 1 minute and 24 hours;
  the hard limit must cover it and must not exceed 7 days.
- Vault files and directories use a protected Windows ACL.
- Plaintext export is an explicit user operation outside the Vault protection boundary.
- Export staging directories use a protected ACL and stale staging directories are
  removed when a matching export starts.
- Password KDF parameters and backup archive expansion are bounded before expensive
  or memory-allocating work starts.
- Backup creation, backup restoration and plaintext export write encrypted audit
  events containing operation type, result and aggregate counts without recording
  destination filesystem paths.
- Windows administrators, kernel malware and active process-memory attacks are outside scope.

## MCP trust boundary

CipherVault protects data at rest and controls access to decrypted content through
the MCP server. Once a tool returns document text, that text enters the MCP client,
model context, logs and any external services used by the client. Local and cloud
models therefore have different trust boundaries. Cloud model providers may receive
document bodies, search snippets, titles and tags.

Use `--read-only` unless the AI client needs to modify the Vault, and keep the
client's per-tool approval policy enabled for write and delete operations.

Long-running AI workflows can use a separate MCP process with a longer idle
timeout while keeping the hard limit bounded:

```toml
args = [
  '--vault', 'D:\CipherVault',
  '--read-only',
  '--idle-minutes', '60',
  '--hard-hours', '8',
]
```

## Projects

- `CipherVault.Core`: encryption, SQLCipher storage, search and document operations
- `CipherVault.Mcp`: STDIO MCP server and WPF authorization prompt
- `CipherVault.Cli`: management commands
- `CipherVault.Ui.Wpf`: Windows desktop UI

The current backup extension is `.cvbackup`; `CIPHER_VAULT_PATH` is the
supported environment variable for the MCP server.

## Build and test

```powershell
dotnet build .\CipherVault.sln -c Release
dotnet test .\CipherVault.sln -c Release --no-build
```

## MCP configuration

```toml
[mcp_servers.cipher_vault]
command = 'D:\path\to\CipherVault.Mcp.exe'
args = ['--vault', 'D:\CipherVault', '--read-only']
tool_timeout_sec = 120
default_tools_approval_mode = "prompt"
```

## Publish

```powershell
.\scripts\publish.ps1
```

Outputs are written under `artifacts\publish`.

## Desktop behavior

The browsing view supports cursor-based "load more" pagination for the catalog,
search results and recycle bin. Session countdowns refresh once per second, and
decrypted lists, document text and active import state are cleared immediately
when the session locks or the workstation locks.
