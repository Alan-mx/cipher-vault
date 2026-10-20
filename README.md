# Personal Vault

Windows 11 local encrypted Markdown/TXT vault with a STDIO MCP interface.

## Security model

- SQLCipher encrypts document content, metadata, revisions, recycle bin, FTS and audit.
- The password and offline recovery key independently unwrap a random master key.
- Recovery-key confirmation is enforced by the Vault core before content access.
- Recovery-key rotation is staged in `vault.keys.pending` until the new key is confirmed.
- MCP content tools require an interactive password prompt in the MCP process.
- Tool calls do not extend the session.
- Sessions lock after 15 minutes without Windows keyboard/mouse input, after 4 hours, on screen lock, or on manual lock.
- Vault files and directories use a protected Windows ACL.
- Plaintext export is an explicit user operation outside the Vault protection boundary.
- Windows administrators, kernel malware and active process-memory attacks are outside scope.

## Projects

- `PersonalVault.Core`: encryption, SQLCipher storage, search and document operations
- `PersonalVault.Mcp`: STDIO MCP server and WPF authorization prompt
- `PersonalVault.Cli`: management commands
- `PersonalVault.Ui.Wpf`: Windows desktop UI

## Build and test

```powershell
dotnet build .\PersonalVault.sln -c Release
dotnet test .\PersonalVault.sln -c Release --no-build
```

## MCP configuration

```toml
[mcp_servers.personal_vault]
command = 'D:\path\to\personal-vault-mcp.exe'
args = ['--vault', 'D:\PersonalVault']
tool_timeout_sec = 120
default_tools_approval_mode = "prompt"
```

## Publish

```powershell
.\scripts\publish.ps1
```

Outputs are written under `artifacts\publish`.
