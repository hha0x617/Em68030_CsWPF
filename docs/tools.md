# Disk Image and Utility Tools

The `tools/` directory contains scripts for creating and managing disk images
and transferring files to guest operating systems.

## Overview

| Script | Platform | Description |
|--------|----------|-------------|
| `create-netbsd-disk.ps1` / `.sh` | Windows, Linux/WSL | Create NetBSD disk image with BSD disklabel |
| `create-debian-disk.sh` | Linux/WSL | Create Debian/m68k disk image via debootstrap |
| `create-gentoo-disk.sh` | Linux/WSL | Create Gentoo/m68k disk image from stage3 tarball |
| `expand-netbsd-disk.ps1` / `.sh` | Windows, Linux/WSL | Expand existing NetBSD disk image |
| `expand-linux-disk.sh` | Linux/WSL | Expand existing Linux (Debian/Gentoo) disk image |
| `create-iso.ps1` / `.sh` | Windows, Linux/WSL | Create ISO image for file transfer to guest |
| `mkdisklabel.c` | (helper) | NetBSD VID disklabel writer (compiled automatically) |

---

## Create NetBSD Disk Image

Creates a raw SCSI disk image with a NetBSD VID disklabel.
Optionally places a miniroot image on sd0b for installation.

**Windows (PowerShell, requires Docker):**
```powershell
.\tools\create-netbsd-disk.ps1 -Size 2G -Miniroot miniroot.fs -Output netbsd.img
```

**Linux / WSL:**
```bash
./tools/create-netbsd-disk.sh -s 2G -m miniroot.fs -o netbsd.img
```

> **Note:** The shell script compiles `mkdisklabel.c` automatically and requires `gcc` (or a C compiler set via `$CC`).
> On Ubuntu/WSL: `sudo apt install build-essential`

| Option | Default | Description |
|--------|---------|-------------|
| `-s` / `-Size` | `2G` | Disk image size (500M to 4T) |
| `-m` / `-Miniroot` | (none) | NetBSD miniroot.fs to place on sd0b |
| `-o` / `-Output` | `disk.img` | Output file |
| `-w` | `32` | Swap partition size in MB |

Miniroot download: `https://cdn.netbsd.org/pub/NetBSD/NetBSD-10.1/mvme68k/installation/miniroot/`

---

## Create Debian Disk Image

Creates a Debian/m68k disk image using debootstrap. Requires root privileges.

```bash
sudo ./tools/create-debian-disk.sh -s 1G -p root -o debian.img
```

| Option | Default | Description |
|--------|---------|-------------|
| `-s` | `1G` | Disk image size (500M to 4T) |
| `-p` | `root` | Root password |
| `-o` | `debian.img` | Output file |
| `-w` | `64` | Swap partition size in MB |
| `-n` | (disabled) | Enable NAT network (10.0.2.15/24) |

Requirements: `debootstrap`, `qemu-user-static` (with binfmt_misc F flag),
`sfdisk`, `mkfs.ext4`, `openssl`.

---

## Create Gentoo Disk Image

Creates a Gentoo/m68k disk image from a stage3 tarball. Requires root privileges.

```bash
sudo ./tools/create-gentoo-disk.sh -t stage3-m68k-openrc-<DATE>.tar.xz -s 2G -o gentoo.img
# or
sudo ./tools/create-gentoo-disk.sh -t stage3-m68k-systemd-<DATE>.tar.xz -s 2G -o gentoo.img
```

| Option | Default | Description |
|--------|---------|-------------|
| `-t` | (required) | Gentoo stage3 tarball |
| `-s` | `2G` | Disk image size (500M to 4T) |
| `-p` | `root` | Root password |
| `-o` | `gentoo.img` | Output file |
| `-w` | `64` | Swap partition size in MB |
| `-i` | auto-detect | Init system (`openrc` or `systemd`). Auto-detected from tarball filename if omitted |
| `-n` | (disabled) | Enable NAT network (10.0.2.15/24) |

**Stage3 download** (download before running the script):

```bash
# Check the latest tarball filename (openrc or systemd)
curl -s https://distfiles.gentoo.org/releases/m68k/autobuilds/latest-stage3-m68k-openrc.txt
curl -s https://distfiles.gentoo.org/releases/m68k/autobuilds/latest-stage3-m68k-systemd.txt

# Download one of the variants (replace <DATE> with the actual timestamp from above)
wget https://distfiles.gentoo.org/releases/m68k/autobuilds/current-stage3-m68k-openrc/stage3-m68k-openrc-<DATE>.tar.xz
wget https://distfiles.gentoo.org/releases/m68k/autobuilds/current-stage3-m68k-systemd/stage3-m68k-systemd-<DATE>.tar.xz
```

---

## Expand NetBSD Disk Image

Expands an existing NetBSD disk image and updates the VID disklabel.
Preserves the existing filesystem and partition offsets (read-modify-write).

**Windows (PowerShell, requires Docker):**
```powershell
.\tools\expand-netbsd-disk.ps1 -Size 2G netbsd.img
.\tools\expand-netbsd-disk.ps1 netbsd.img            # relabel only
```

**Linux / WSL:**
```bash
./tools/expand-netbsd-disk.sh -s 2G netbsd.img
./tools/expand-netbsd-disk.sh netbsd.img            # relabel only (no size change)
```

> **Note:** The shell script compiles `mkdisklabel.c` automatically and requires `gcc` (or a C compiler set via `$CC`).
> On Ubuntu/WSL: `sudo apt install build-essential`

| Option | Default | Description |
|--------|---------|-------------|
| `-s` / `-Size` | (none) | New size. If omitted, only rewrites the disklabel |
| `-w` / `-SwapMB` | `32` | Swap partition (sd0b) size in MB |

After expanding, boot NetBSD and resize the filesystem:
```
# resize_ffs /dev/sd0a
```

---

## Expand Linux Disk Image

Expands an existing Linux (Debian/Gentoo) disk image. Rewrites the MBR partition
table and resizes the filesystem. No guest-side action needed. Requires root privileges.

```bash
sudo ./tools/expand-linux-disk.sh -s 2G debian.img
sudo ./tools/expand-linux-disk.sh -s 4G gentoo.img
```

| Option | Default | Description |
|--------|---------|-------------|
| `-s` | `2G` | New size (must be larger than current) |
| `-w` | `64` | Swap partition size in MB |
| `-f` | auto-detect | Filesystem type (`ext2`, `ext3`, `ext4`) |

---

## Create ISO Image (File Transfer)

Creates an ISO image from a directory for transferring files to the guest OS
via the emulator's SCSI CD-ROM.

**Windows (PowerShell, requires Docker):**
```powershell
.\tools\create-iso.ps1 C:\path\to\files
.\tools\create-iso.ps1 -Output transfer.iso C:\path\to\files
```

**Linux / WSL:**
```bash
./tools/create-iso.sh /path/to/files
./tools/create-iso.sh -o transfer.iso /path/to/files
```

| Option | Default | Description |
|--------|---------|-------------|
| `-o` / `-Output` | `<dirname>.iso` | Output ISO file |

**Usage in guest:**
1. Set the ISO file in emulator Settings > SCSI CD-ROM
2. Mount in guest:
   ```sh
   # NetBSD
   mount -t cd9660 /dev/cd0a /mnt

   # Linux
   mount -t iso9660 /dev/sr0 /mnt
   ```
3. Copy files from `/mnt/`
4. Unmount: `umount /mnt`

---

## Requirements Summary

| Script | Root | Docker | Other |
|--------|------|--------|-------|
| `create-netbsd-disk.ps1` | — | Yes | — |
| `create-netbsd-disk.sh` | No | No | `gcc` |
| `create-debian-disk.sh` | Yes | No | `debootstrap`, `qemu-user-static`, `sfdisk` |
| `create-gentoo-disk.sh` | Yes | No | `sfdisk`, `mkfs.ext2`, `tar` |
| `expand-netbsd-disk.ps1` | — | Yes | — |
| `expand-netbsd-disk.sh` | No | No | `gcc` |
| `expand-linux-disk.sh` | Yes | No | `sfdisk`, `resize2fs`, `e2fsck` |
| `create-iso.ps1` | — | Yes | — |
| `create-iso.sh` | No | No | `genisoimage` or `mkisofs` |
