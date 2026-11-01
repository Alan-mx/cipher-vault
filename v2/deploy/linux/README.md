# CipherVault Linux

Supported baseline: glibc x64, validated on Ubuntu 22.04 and Debian 12.
ARM64 and musl/Alpine are not supported by this RC.

The self-contained package does not require a .NET runtime, but the host must provide
the standard native dependencies used by .NET and OpenSSL, including glibc, libicu,
libssl and zlib. A minimal image or distribution may need those packages installed.

## Install

```bash
tar -xzf CipherVault-2.0.0-rc.1-linux-x64.tar.gz
cd CipherVault-2.0.0-rc.1-linux-x64
sudo ./install-systemd.sh
```

The service runs as the `cipher-vault` system user and listens on:

```text
http://127.0.0.1:43127
```

Check:

```bash
systemctl status cipher-vault.service
journalctl -u cipher-vault.service
```

The release archive's highest observed symbol requirement is `GLIBC_2.34`, which is
satisfied by the Ubuntu 22.04 and Debian 12 baselines.

## Foreground

```bash
cd publish
./CipherVault.Server
```

## Paths

```text
/opt/cipher-vault                 program files
/var/lib/cipher-vault/data        Vault database and key envelope
/var/lib/cipher-vault/config      persisted configuration
/var/lib/cipher-vault/backups     encrypted backups
```

## Upgrade

Stop the service, replace program files with the new release, and start it again.
Never delete `/var/lib/cipher-vault` during an upgrade.

## Uninstall

```bash
sudo ./uninstall-systemd.sh
```

This removes the service and program files. It preserves `/var/lib/cipher-vault` and the
`cipher-vault` system user.

## Verify the download

```bash
sha256sum -c SHA256SUMS
```

SHA-256 detects corrupted or modified files. It does not authenticate the publisher;
this RC does not provide Linux package signatures.

## Offline restore

Stop the service first. Run the same binary in an interactive terminal:

```bash
sudo systemctl stop cipher-vault.service
sudo -u cipher-vault \
  CIPHERVAULT_CONFIG_DIR=/var/lib/cipher-vault/config \
  CIPHERVAULT_BACKUP_DIR=/var/lib/cipher-vault/backups \
  /opt/cipher-vault/CipherVault.Server \
  --restore-backup /path/to/backup.cvbackup \
  --target /path/to/new-vault-directory
```

Point `CIPHERVAULT_DATA_DIR` at the new directory only after restore succeeds.
