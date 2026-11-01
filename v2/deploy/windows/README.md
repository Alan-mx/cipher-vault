# CipherVault Windows

Supported target: Windows 11 x64.

## Start

```powershell
.\Start-CipherVault.ps1
```

Open `http://127.0.0.1:43127` in a local browser.

Optional:

```powershell
.\Start-CipherVault.ps1 -OpenBrowser
```

The server remains attached to the console. Press `Ctrl+C` to stop it.

## Start after sign-in

```powershell
.\Install-CipherVaultStartup.ps1
```

This registers a current-user Task Scheduler task with limited privileges. It does not
create a Windows Service and does not require administrator rights.

Remove the startup task:

```powershell
.\Install-CipherVaultStartup.ps1 -Uninstall
```

If this folder is moved, run the install script again so the task points to the new path.

## Data and upgrade

Vault data defaults to:

```text
%LOCALAPPDATA%\CipherVault\
├── data\
├── config\
└── backups\
```

Stop the server before replacing program files. Never delete or overwrite the
`%LOCALAPPDATA%\CipherVault` directory during an upgrade.

## Security

- Access defaults to `127.0.0.1` only.
- This RC is not Authenticode-signed. Windows may show a security warning; no trusted
  publisher identity is claimed.
- Vault Password and MCP Token are different credentials.
- Forgetting the Vault Password makes the Vault and its encrypted backups unrecoverable.
