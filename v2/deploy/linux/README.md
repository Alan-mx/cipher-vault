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

## Tailscale access

The default service remains loopback-only. To expose CipherVault to the local
tailnet through Tailscale Serve:

```bash
sudo ./enable-tailscale-serve.sh
```

The command reads the node's MagicDNS name and Tailscale IPv4/IPv6 addresses,
writes an exact Host allowlist to a systemd drop-in, and enables Tailscale Serve
TCP forwarding on port 43127. All of these URLs are available inside the tailnet:

```text
http://<magic-dns-name>:43127
http://<tailscale-ipv4>:43127
http://[<tailscale-ipv6>]:43127
```

It does not enable Tailscale Funnel, bind CipherVault to `0.0.0.0`, or replace
Vault Password and MCP Token authentication.

Check the Serve configuration:

```bash
tailscale serve status
```

Disable tailnet access and return to loopback-only mode:

```bash
sudo ./enable-tailscale-serve.sh --disable
```

## SSH tunnel access

CipherVault remains bound to `127.0.0.1:43127`. An SSH tunnel lets a computer
reach that loopback service without exposing CipherVault to the LAN or USB
network directly.

The local forwarded port must be `43127`. Using another local port changes the
HTTP Host header and will be rejected by the Host/Origin allowlist.

### Same LAN

Find the phone's LAN address on the phone:

```bash
ip -4 addr show wlan0
```

From a computer on the same reachable LAN:

```powershell
ssh -N -L 43127:127.0.0.1:43127 orbital@<phone-lan-ip>
```

Then open:

```text
http://127.0.0.1:43127
```

An SSH config entry can keep the forwarding rule:

```text
Host oneplus6-lan
    HostName <phone-lan-ip>
    User orbital
    LocalForward 43127 127.0.0.1:43127
```

```powershell
ssh -N oneplus6-lan
```

The phone and computer must be in the same reachable subnet, or routing and
firewall rules must explicitly allow computer access to the phone's SSH port.

### USB

Connecting a USB cable by itself does not expose the CipherVault web server.
Use one of these methods:

#### ADB port forwarding

The phone must have USB debugging enabled and the computer must have ADB
installed and authorized:

```powershell
adb devices -l
adb forward tcp:43127 tcp:43127
```

Then open:

```text
http://127.0.0.1:43127
```

Remove the forwarding rule when finished:

```powershell
adb forward --remove tcp:43127
```

#### USB network plus SSH

If USB networking is configured and the computer can reach SSH on the phone's
USB-network address:

```powershell
ssh -N -L 43127:127.0.0.1:43127 orbital@<phone-usb-ip>
```

Then open:

```text
http://127.0.0.1:43127
```

MTP/storage mode does not provide access to the web server. Both USB methods
still use the existing Vault Password and MCP Token authentication.

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
