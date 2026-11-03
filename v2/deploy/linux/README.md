# CipherVault Linux

Supported packages:

- glibc x64: validated on Ubuntu 22.04 and Debian 12.
- glibc ARM64: automated baseline on Ubuntu 24.04; OnePlus 6T with
  Ubuntu 26.04.1 is the pending physical-device acceptance target.

Other ARM64 distributions are not yet claimed as supported. musl/Alpine is not
supported.

The self-contained package does not require a .NET runtime, but the host must provide
the native dependencies used by .NET and OpenSSL, including glibc, libicu, libssl,
and zlib. A minimal image or distribution may need those packages installed.

## Install

Choose the archive that matches `uname -m`. For x64:

```bash
tar -xzf CipherVault-2.0.0-rc.1-linux-x64.tar.gz
cd CipherVault-2.0.0-rc.1-linux-x64
sudo ./install-systemd.sh
```

For ARM64 (`aarch64`):

```bash
tar -xzf CipherVault-2.0.0-rc.1-linux-arm64.tar.gz
cd CipherVault-2.0.0-rc.1-linux-arm64
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

The current Linux x64 and ARM64 archives both record a highest observed symbol
requirement of `GLIBC_2.34`. Native ARM64 CI must reconfirm this for every release;
do not infer the requirement from the target device's glibc 2.43.

## System dependencies

For the OnePlus 6T Ubuntu 26.04.1 ARM64 target, check these packages before
installation:

```text
libc6
libgcc-s1
libgssapi-krb5-2
libicu78
libssl3t64
libstdc++6
tzdata
zlib1g
libbrotli1
ca-certificates
```

The validated OnePlus 6T Ubuntu 26.04.1 device has `libbrotli1` installed, and
the ARM64 archive's Server and SQLite native library report no missing `ldd`
dependencies. Keep the full list above as the release dependency checklist.

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
