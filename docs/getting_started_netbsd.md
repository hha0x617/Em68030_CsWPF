# Getting Started: Installing and Booting NetBSD on Em68030

This guide walks you through installing NetBSD/mvme68k on a virtual SCSI hard disk using the Em68030 emulator, then booting NetBSD from the installed disk.

## Overview

The installation follows the same three phases as on real MVME147 hardware:

1. **Boot RAMDISK kernel** — Partition the disk and write the miniroot filesystem
2. **Boot miniroot** — Run the sysinst installer to install NetBSD from CD-ROM or network
3. **Boot the installed system** — Start NetBSD from the installed disk

## Prerequisites

- Em68030 built and ready to run (see [README](../README.md) for build instructions)
- Internet access to download NetBSD/mvme68k files
- About 600 MB of free disk space (for disk image + downloaded files)

## Download NetBSD/mvme68k Files

Download the following files from the NetBSD CDN. For NetBSD 10.1:

| File | URL | Description |
|---|---|---|
| `netbsd-RAMDISK.gz` | `.../installation/tapeimage/netbsd-RAMDISK.gz` | RAMDISK kernel for disk setup |
| `netbsd-GENERIC.gz` | `.../binary/kernel/netbsd-GENERIC.gz` | Standard kernel for normal operation |
| CD-ROM ISO | `.../images/NetBSD-10.1-mvme68k.iso` | Installation sets and miniroot |

Base URL: `https://cdn.netbsd.org/pub/NetBSD/NetBSD-10.1/mvme68k/`

Decompress the `.gz` files after downloading:

```bash
gzip -d netbsd-RAMDISK.gz
gzip -d netbsd-GENERIC.gz
```

> **Note**: On Windows, you can use 7-Zip or similar tools to decompress `.gz` files.

## Create a Virtual SCSI Disk Image

### Option A: Using the Emulator GUI

1. Launch Em68030
2. Open **Settings** from the menu bar
3. Set **Board Type** to `MVME147`
4. In the **SCSI Disks** section, enter a size in the **New Image Size (MB)** field (e.g., `500` for a 500 MB disk)
5. Click **Create** and choose a location to save the disk image file (e.g., `netbsd.img`)
6. Add the created disk image at **SCSI ID 0** in the SCSI Disks list
7. Set **Memory Size** to 64 MB (67108864 bytes) — recommended
8. Click **OK** to save

The emulator creates an empty disk image with a valid NetBSD `cpu_disklabel` pre-written, including partition `a` (root filesystem) and partition `b` (swap, also used for the miniroot during installation).

### Option B: Using the Command-Line Script

The `tools/create-netbsd-disk.sh` script creates a disk image with a BSD disklabel
from the command line. If a miniroot image is provided, it is placed on sd0b:

```bash
./tools/create-netbsd-disk.sh -s 2G -m miniroot.fs -o netbsd.img
```

See [Disk Image and Utility Tools](tools.md#create-netbsd-disk-image) for full options and Windows (PowerShell) usage.

---

## Phase 1: Boot RAMDISK — Write Miniroot to Partition b

The RAMDISK kernel contains a minimal in-memory root filesystem with basic utilities for disk setup. Its root is on `md0` (memory disk), leaving the SCSI disk available for writing.

### 1.1 Configure CD-ROM

The CD-ROM ISO must be configured before booting the RAMDISK kernel:

1. Open **Settings**
2. Set **SCSI CD-ROM** path to `NetBSD-10.1-mvme68k.iso`
3. Click **OK**

### 1.2 Load the RAMDISK Kernel

1. Select **File > Open ELF...** from the menu bar
2. Select the `netbsd-RAMDISK` file
3. Press **F5** to start execution
4. Open the **Console Window** from **View > Console Window**

The RAMDISK kernel boots and drops you into a shell prompt.

### 1.3 Verify the Disk Label

The emulator's "Create" function pre-writes a disklabel with partitions `a` and `b`. Verify it:

```
# /sbin/disklabel sd0
```

You should see partition `a` (4.2BSD) and partition `b` (swap).

### 1.4 Write Miniroot to Partition b

Mount the CD-ROM and write the miniroot to partition `b`:

```
# /sbin/mount -t cd9660 /dev/cd0a /mnt2
# /usr/bin/gunzip < /mnt2/mvme68k/installation/miniroot/miniroot.fs.gz | /bin/dd of=/dev/rsd0b obs=8k
# /sbin/umount /mnt2
```

> **Note**: We write to `/dev/rsd0b` (partition b) rather than the whole disk. This preserves the disklabel at sector 0 and keeps partition `a` available for the installer.

### 1.5 Halt

```
# /sbin/halt
```

Or press **Shift+F5** to stop the emulator.

---

## Phase 2: Boot Miniroot — Install NetBSD with sysinst

The miniroot contains the `sysinst` installer, which handles disk partitioning, filesystem creation, and installation set extraction.

### 2.1 Set Boot Partition to b

The kernel must boot from partition `b` (where the miniroot was written). This also ensures that `sysinst` can detect `sd0` as an installation target (it excludes the root partition from candidates).

1. Open **Settings**
2. Set **Boot Partition** to `b`
3. Click **OK**

### 2.2 Boot the Miniroot

1. Select **Run > Full Reset** to clear CPU state
2. Select **File > Open ELF...** and load `netbsd-GENERIC`
3. Press **F5** to start execution

The kernel boots and mounts the miniroot on partition `b` as the root filesystem. The `sysinst` installer starts automatically.

### 2.3 Run sysinst

Follow the installer prompts:

1. Select **Install NetBSD to hard disk**
2. The installer detects the SCSI disk at `sd0`
3. When asked about the partition layout, select **Manually define partitions**
4. Edit partition `a` with the following settings:
   - **type**: `FFS` (not FFSv2)
   - **install**: `Yes`
   - **newfs**: `Yes`
   - **mount**: `Yes`
   - **mount point**: `/`
5. Leave partition `b` (swap) unchanged
6. Select **Partition sizes ok**, then confirm the layout
7. When asked for the installation source:
   - If you configured the CD-ROM ISO: select **CD-ROM** (`cd0`)
   - Otherwise: select **FTP** and enter a NetBSD mirror URL (e.g., `cdn.netbsd.org`, directory `/pub/NetBSD/NetBSD-10.1`)
8. Select the installation sets you want (at minimum: `base`, `etc`)
9. Wait for the extraction to complete
10. Configure timezone, root password, and other options as prompted

> **Important**: The emulator's disk image has partition `a` marked as `unused` in the disklabel. You must manually set it to `FFS` with the install/newfs/mount flags so that sysinst creates the filesystem and mounts it as `/`.

> **Tip**: You can paste text into the console window for long inputs like mirror URLs.

### 2.4 Complete Installation

When sysinst finishes:

1. Select **Reboot** from the installer menu, or press **Shift+F5** to stop the emulator

---

## Phase 3: Boot the Installed System

### 3.1 Set Boot Partition Back to a

1. Open **Settings**
2. Set **Boot Partition** to `a`
3. Click **OK**

### 3.2 Load the Kernel

1. Stop the emulator if it is still running (**Shift+F5**)
2. Select **Run > Full Reset**
3. Select **File > Open ELF...** and load `netbsd-GENERIC`

### 3.3 Verify Settings

In **Settings**, confirm:

- **Board Type**: `MVME147`
- **Boot Partition**: `a`
- **SCSI Disks**: The installed disk image at SCSI ID 0
- The CD-ROM ISO path can be cleared if no longer needed

### 3.4 Boot

1. Press **F5** to start execution
2. NetBSD boots from the installed disk and displays startup messages in the console

On the first boot, the system enters **single-user mode** because `/etc/rc.conf` is not yet configured:

```
/etc/rc.conf is not configured.  Multiuser boot aborted.
Enter pathname of shell or RETURN for /bin/sh:
```

Press **Enter** to get a shell. Run the following two commands to make the root filesystem writable and set the terminal type:

```
# mount -u -o rw /
# export TERM=vt100
```

### 3.5 Recommended Initial Configuration

Add `TERM=vt100` to `/etc/profile` so it is set automatically on every login:

```
# cat /etc/profile
#       $NetBSD: profile,v 1.1 1997/06/21 06:07:39 mikel Exp $
#
# System-wide .profile file for sh(1).
export TERM=vt100
#
```

Once you have finished configuring the system, enable multi-user mode by setting `rc_configured=YES` in `/etc/rc.conf`.

Confirm that `rc_configured` is set to `YES` in `/etc/rc.conf`:

```
# grep 'rc_configured' /etc/rc.conf
rc_configured=YES
#
```

Press **Ctrl+D** to exit single-user mode and enter multi-user mode. On subsequent boots, the system will start in multi-user mode automatically and display a login prompt.

Login with `root` (using the password you set during installation).

---

## Quick Reference

### Keyboard Shortcuts

| Key | Action |
|---|---|
| F5 | Run |
| Shift+F5 | Stop |
| F10 | Step (single instruction) |
| F4 | Run to cursor |

### Menu Structure

| Menu | Items |
|---|---|
| File | Open Binary, Open S-Record, Open ELF, Exit |
| Run | Run, Stop, Step, Run to Cursor, Set PC to Cursor, Reset, Full Reset |
| View | Console Window, Breakpoints Window, Toggle LST View |
| Settings | Emulator Settings |

### Configuration File

Settings are saved to `appsettings.json` in the application directory. See [README](../README.md) for the full configuration reference.

---

## Expanding the Disk Image

If the disk image becomes too small (e.g., for installing X Window System packages),
use the expand script to resize it:

```bash
./tools/expand-netbsd-disk.sh -s 2G netbsd.img
```

After expanding, boot NetBSD and resize the filesystem: `resize_ffs /dev/sd0a`

See [Disk Image and Utility Tools](tools.md#expand-netbsd-disk-image) for full options, Windows usage, and troubleshooting.

---

## X Window System (Optional)

The Em68030 emulator supports X Window System on NetBSD via the `wsfb` framebuffer driver.
This requires the MVME147_FB kernel (with genfb, wskbd, wsmouse drivers) and a custom-built
Xorg server, since the official NetBSD/mvme68k release does not include Xorg.

### Prerequisites

- **Kernel**: MVME147_FB (from this project's releases or built from source)
- **Disk space**: At least 2 GB (use `expand-netbsd-disk` if needed)
- **X11 base sets**: xbase, xcomp, xetc, xfont, xserver from NetBSD release
- **Xorg server**: `xserver-wsfb-mvme68k.tgz` from [Em68030-Guest-NetBSD releases](https://github.com/hha0x617/Em68030-Guest-NetBSD/releases)

### Step 1: Install X11 base sets

Download the sets via CD-ROM (see [create-iso](tools.md#create-iso-image-file-transfer)):

```sh
mount -t cd9660 /dev/cd0a /mnt
cd /
for set in xbase xcomp xetc xfont xserver; do
    tar xpzf /mnt/${set}.tgz
    echo "${set} done"
done
umount /mnt
```

### Step 2: Install Xorg server with wsfb driver

Transfer `xserver-wsfb-mvme68k.tgz` via CD-ROM and extract:

```sh
mount -t cd9660 /dev/cd0a /mnt
cd /
tar xpzf /mnt/xserver-wsfb-mvme68k.tgz
umount /mnt
ln -s /usr/X11R7/bin/Xorg /usr/X11R7/bin/X
```

### Step 3: Create xorg.conf

```sh
cat > /etc/X11/xorg.conf << 'EOF'
Section "ServerFlags"
    Option "AutoAddDevices" "false"
EndSection

Section "ServerLayout"
    Identifier   "Layout0"
    Screen       "Screen0"
    InputDevice  "Keyboard0" "CoreKeyboard"
    InputDevice  "Mouse0"    "CorePointer"
EndSection

Section "InputDevice"
    Identifier  "Keyboard0"
    Driver      "kbd"
    Option      "Protocol" "wskbd"
    Option      "Device"   "/dev/wskbd0"
EndSection

Section "InputDevice"
    Identifier  "Mouse0"
    Driver      "mouse"
    Option      "Protocol" "wsmouse"
    Option      "Device"   "/dev/wsmouse0"
EndSection

Section "Device"
    Identifier  "Card0"
    Driver      "wsfb"
    Option      "device"   "/dev/ttyE0"
    Option      "HWCursor" "false"
EndSection

Section "Screen"
    Identifier  "Screen0"
    Device      "Card0"
    DefaultDepth 16
    SubSection "Display"
        Depth   16
        Modes   "1024x768"
    EndSubSection
EndSection
EOF
```

Key settings:
- **`AutoAddDevices false`** — Prevents hotplug from disabling kbd/mouse drivers
- **`HWCursor false`** — Required for Em68030's virtual framebuffer
- **`/dev/ttyE0`** — Explicit wsdisplay device path

### Step 4: Start X

```sh
startx
```

X should start on the framebuffer window with a basic X cursor.

---

## Troubleshooting

### "boot device: \<unknown\>"
The boot stub could not identify the SCSI controller. Ensure **Board Type** is set to `MVME147` and at least one SCSI disk is configured.

### Kernel panics immediately after boot
- Try **Run > Full Reset** before loading a new kernel to clear stale memory state
- Ensure the disk image has a valid miniroot or installation
- Ensure you are using the correct kernel for the phase: `netbsd-RAMDISK` for Phase 1, `netbsd-GENERIC` for Phases 2 and 3

### RAMDISK kernel cannot find the SCSI disk
- Verify the disk image is configured at SCSI ID 0 in Settings
- Ensure the disk image file exists and is not empty

### Console window shows no output
- Open the console from **View > Console Window**
- Ensure the emulator is running (press **F5**)

### sysinst cannot find installation sets on CD-ROM
- Verify the ISO path is correctly set in **Settings > SCSI CD-ROM**
- The ISO should be a standard NetBSD/mvme68k release ISO

### Windows Defender SmartScreen blocks the emulator
Since the executable is not code-signed, SmartScreen may block it on first run. Click "More info" and then "Run anyway", or right-click the exe, open Properties, and check "Unblock" on the General tab.
