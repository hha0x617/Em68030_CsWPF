# Getting Started: Installing Debian Linux on Em68030

This guide describes the steps to install Debian/m68k on a virtual SCSI hard disk and boot it using the Em68030 emulator (MVME147 board).

Debian has an active m68k port maintained as part of the [Debian Ports](https://www.ports.debian.org/) project, making it one of the best-supported Linux distributions for m68k hardware.

> **Note**: Ubuntu does not support the m68k architecture. This guide covers Debian only.

## Overview

The installation follows these phases:

1. **Prepare the root filesystem** -- Create a disk image, partition it, and bootstrap a Debian root filesystem using `debootstrap` (on WSL or a Linux host)
2. **Build a kernel** -- Cross-compile a Linux kernel for MVME147 with required source patches
3. **Boot the system** -- Load the kernel in Em68030 and boot Debian

## Prerequisites

- Em68030 emulator
- A Linux environment for filesystem preparation and kernel build (WSL2 on Windows, or a Debian/Ubuntu host)
- Cross-compiler toolchain for m68k (`m68k-linux-gnu` from Debian packages)
- Internet access to download Debian packages and kernel source
- About 1 GB of free disk space

## Emulator-Side Changes for Linux

Linux/m68k on MVME147 requires the following emulator features, all of which are already implemented:

### Linux Bootinfo Format

Linux/m68k uses a different boot protocol than NetBSD. The emulator's boot stub constructs a chain of `bi_record` structures in memory when the **Target OS** is set to `Linux` in settings:

| Tag | Value | Description |
|---|---|---|
| `BI_MACHTYPE` | 0x0001 | Machine type: `MACH_MVME147` (5) + `MMU_68030` + `FPU_68882` |
| `BI_MEMCHUNK` | 0x0004 | Memory region: start address + size |
| `BI_COMMAND_LINE` | 0x0007 | Kernel command line (e.g., `root=/dev/sda1 console=ttyS0,9600`) |
| `BI_VME_TYPE` | 0x8000 | VME board type: `VME_TYPE_MVME147` (0x0147) |
| `BI_VME_BRDINFO` | 0x8001 | Board information (clock speed, etc.) |
| `BI_LAST` | 0x0000 | End of record chain |

### Virtual 16550A UART

The emulator includes a virtual 16550A UART device memory-mapped at **0xFFFE2000** (8 bytes). This device is not present on the real MVME147 hardware but is required for Linux userspace console I/O.

**Why it is needed**: The real MVME147 uses a Z8530 SCC for serial communication. Linux's early boot console (`earlyprintk`) can output via the SCC, but userspace programs (init, shell, systemd) require a tty device backed by a proper serial driver. The upstream Linux kernel does not include a Z8530-based tty driver for MVME147. Adding a 16550-compatible UART allows Linux to use the well-supported `8250/16550` serial driver for the userspace console (`/dev/ttyS0`).

The virtual UART supports:
- Full 16550A register set (RBR/THR, IER, IIR/FCR, LCR, MCR, LSR, MSR, SCR, DLAB)
- MCR loopback mode (bit 4) for 8250 driver autodetection
- 64-byte receive FIFO
- Interrupt output (active high)

### RTC Year Convention

The M48T02 RTC year encoding differs between NetBSD and Linux:
- **NetBSD**: Year base 1968 (stores `year - 1968`)
- **Linux**: Raw 2-digit year (`year % 100`)

The emulator automatically selects the correct encoding based on the **Target OS** setting.

---

## Phase 1: Prepare the Root Filesystem

All filesystem preparation is done on a Linux host or WSL2, since Windows cannot natively handle ext2/ext4 filesystems.

### 1.1 Install Required Tools

On a Debian/Ubuntu host:

```bash
sudo apt update
sudo apt install debootstrap qemu-user-static binfmt-support
```

`qemu-user-static` enables running m68k binaries on an x86 host via user-mode emulation, which is needed for `debootstrap`'s second stage.

Verify that binfmt_misc is set up for m68k:

```bash
cat /proc/sys/fs/binfmt_misc/qemu-m68k
```

If the output includes `flags: F`, binfmt is correctly configured. The `F` (fix-binary) flag is essential -- it tells the kernel to resolve the QEMU interpreter path at registration time, so it works inside chroots.

If the file does not exist or lacks the `F` flag, restart the service:

```bash
sudo systemctl restart systemd-binfmt
```

If it still does not appear, register it manually:

```bash
echo ':qemu-m68k:M::\x7fELF\x01\x02\x01\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x02\x00\x04:\xff\xff\xff\xff\xff\xff\xff\x00\x00\x00\x00\x00\x00\x00\x00\x00\xff\xfe\xff\xff:/usr/bin/qemu-m68k-static:F' | sudo tee /proc/sys/fs/binfmt_misc/register
```

### 1.2 Create and Partition the Disk Image

Create a 512 MB disk image and partition it with `fdisk`:

```bash
dd if=/dev/zero of=debian.img bs=1M count=512

fdisk debian.img
```

Create two partitions:
- **Partition 1** (Linux, ~480 MB): root filesystem
- **Partition 2** (Linux swap, ~32 MB): swap space

Example `fdisk` commands:
```
n p 1 <enter> +480M
n p 2 <enter> <enter>
t 2 82
w
```

### 1.3 Format and Mount

Set up loop devices, format, and mount:

```bash
sudo losetup -fP debian.img
# Note the loop device assigned (e.g., /dev/loop0)
LOOPDEV=/dev/loop0

sudo mkfs.ext4 ${LOOPDEV}p1
sudo mkswap ${LOOPDEV}p2

sudo mkdir -p /mnt/debian
sudo mount ${LOOPDEV}p1 /mnt/debian
```

### 1.4 Bootstrap Debian with debootstrap

Run `debootstrap` to install a minimal Debian system. Debian/m68k packages are hosted on `ftp.ports.debian.org`:

```bash
sudo debootstrap --arch=m68k --foreign --no-check-gpg \
    sid /mnt/debian http://ftp.ports.debian.org/debian-ports/
```

> **Note**: `--foreign` is required because the host architecture (x86_64) differs from the target (m68k). The `--no-check-gpg` flag skips GPG verification for the ports archive. `sid` (unstable) is used because m68k is not available in Debian stable releases.

Complete the second stage using QEMU user-mode emulation:

```bash
sudo chroot /mnt/debian /debootstrap/debootstrap --second-stage
```

> **Note**: If binfmt_misc is registered with the `F` flag (see step 1.1), the kernel automatically uses the host's `/usr/bin/qemu-m68k-static` to run m68k binaries inside the chroot -- no need to copy `qemu-m68k-static` into the chroot. If you get `Exec format error`, verify the `F` flag is set (see step 1.1).

The second stage unpacks and configures all packages inside the chroot. This may take several minutes.

### 1.5 Configure the Root Filesystem

> **Note**: QEMU user-mode emulation of m68k does not support interactive shells. All configuration is performed using `sudo chroot /mnt/debian /bin/sh -c "command"` to run commands non-interactively.

Mount virtual filesystems required by some chroot commands:

```bash
sudo mount --bind /dev /mnt/debian/dev
sudo mount --bind /dev/pts /mnt/debian/dev/pts
sudo mount -t proc proc /mnt/debian/proc
sudo mount -t sysfs sysfs /mnt/debian/sys
```

#### Set root password

```bash
HASHED=$(openssl passwd -6 "your_password_here")
sudo chroot /mnt/debian /bin/sh -c "usermod -p '${HASHED}' root"
```

#### fstab

```bash
sudo tee /mnt/debian/etc/fstab << 'EOF'
/dev/sda1    /        ext4    defaults,noatime    0 1
/dev/sda2    none     swap    sw                  0 0
proc         /proc    proc    defaults            0 0
sysfs        /sys     sysfs   defaults            0 0
devtmpfs     /dev     devtmpfs defaults           0 0
EOF
```

#### Hostname

```bash
echo "mvme147" | sudo tee /mnt/debian/etc/hostname
echo "127.0.1.1 mvme147" | sudo tee -a /mnt/debian/etc/hosts
```

#### Serial console

```bash
# Enable serial console login (systemd)
sudo chroot /mnt/debian /bin/sh -c "systemctl enable serial-getty@ttyS0.service 2>/dev/null"
```

#### Network (optional)

If the emulator's network mode is set to **Virtual (Echo Server)** (default), no guest-side
network configuration is required. The following is only needed for **NAT (Host Network)** mode,
which provides the guest with access to the host network.

The default NAT gateway address is `10.0.2.2` and the guest IP is `10.0.2.15`.
These match the emulator's default settings (Settings → Network).

The emulator's NAT implementation forwards UDP/TCP packets to the destination IP
as-is via the host OS network stack. There is no built-in DNS forwarder, so
`/etc/resolv.conf` must point to a DNS server reachable from the host
(e.g., `8.8.8.8`, or your LAN's DNS server).

```bash
cat << 'EOF' | sudo tee /mnt/debian/etc/systemd/network/10-eth0.network
[Match]
Name=eth0

[Network]
Address=10.0.2.15/24
Gateway=10.0.2.2
EOF
```

```bash
cat << 'EOF' | sudo tee /mnt/debian/etc/resolv.conf
nameserver 8.8.8.8
EOF
```

Enable systemd-networkd:

```bash
sudo chroot /mnt/debian /bin/sh -c "systemctl enable systemd-networkd 2>/dev/null"
```

#### APT sources (for future package installation)

```bash
sudo tee /mnt/debian/etc/apt/sources.list << 'EOF'
deb http://ftp.ports.debian.org/debian-ports/ sid main
deb http://ftp.ports.debian.org/debian-ports/ unreleased main
EOF
```

#### Debian Ports archive keyring

The Debian Ports archive uses a separate signing key that is not included in the
default `debian-keyring`. Without it, `apt update` will fail with
`NO_PUBKEY C6894E6BB25B9C99`. Install the keyring package during the chroot setup:

```bash
sudo chroot /mnt/debian /bin/sh -c "apt -o Acquire::AllowInsecureRepositories=true update && apt install -y debian-ports-archive-keyring"
```

### 1.6 Unmount

```bash
sudo umount /mnt/debian/sys
sudo umount /mnt/debian/proc
sudo umount /mnt/debian/dev/pts
sudo umount /mnt/debian/dev
sudo umount /mnt/debian
sudo losetup -d ${LOOPDEV}
```

The `debian.img` file is now ready. Copy it to the Em68030 directory on Windows.

---

## Phase 2: Build a Linux Kernel

### 2.1 Install Cross-Compiler

On Debian/Ubuntu, the m68k cross-compiler is available as a package:

```bash
sudo apt install build-essential gcc-m68k-linux-gnu flex bison libncurses-dev libssl-dev
```

This installs the cross-compiler (`m68k-linux-gnu-gcc`), build tools (`make`, `gcc`), and dependencies required for kernel configuration and compilation.

### 2.2 Download Kernel Source

```bash
wget https://cdn.kernel.org/pub/linux/kernel/v6.x/linux-6.12.17.tar.xz
tar xf linux-6.12.17.tar.xz
cd linux-6.12.17
```

### 2.3 Patch Kernel Source

Two source files must be modified to support the emulator's virtual hardware.

#### Patch 1: Prevent boot console from being unregistered

The file `arch/m68k/kernel/early_printk.c` contains a `late_initcall` that explicitly unregisters the early boot console. On the real MVME16x, this is correct because the platform has a proper SCC-based tty driver. On MVME147, the early console is the only kernel-level SCC output path, and unregistering it would stop all kernel messages. However, since we add a 16550 UART (see Patch 2), the 8250 driver registers `ttyS0` as a real console, making the early boot console (`debug0`) redundant. The `keep_bootcon` kernel parameter would normally preserve it, but `unregister_early_console()` bypasses that parameter. This patch prevents the explicit unregistration on MVME147, allowing `debug0` to coexist with `ttyS0` if `keep_bootcon` is specified (useful for debugging).

> **Note**: This patch is optional if you do not use `earlyprintk` or `keep_bootcon` on the kernel command line. When `console=ttyS0` is the only console, the 8250 driver handles all output and this patch has no effect.

Edit `arch/m68k/kernel/early_printk.c` and add `MACH_IS_MVME147` to the skip condition:

```c
static int __init unregister_early_console(void)
{
    /* Skip unregistration for platforms that rely on the early console */
    if (!early_console || MACH_IS_MVME16x || MACH_IS_MVME147)
        return 0;
    return unregister_console(early_console);
}
late_initcall(unregister_early_console);
```

#### Patch 2: Register the virtual 16550 UART as a platform device

The emulator provides a virtual 16550A UART at address 0xFFFE2000. The kernel must be told about this device so the 8250/16550 serial driver can claim it as `/dev/ttyS0`.

Edit `arch/m68k/mvme147/config.c` and add the following at the top (with existing includes):

```c
#include <linux/platform_device.h>
#include <linux/serial_8250.h>
```

Then add the platform device registration code at the end of the file (before the final closing comment, if any):

```c
/*
 * Virtual 16550A UART provided by the Em68030 emulator.
 * Not present on real MVME147 hardware.
 */
static struct plat_serial8250_port mvme147_uart_data[] = {
    {
        .mapbase  = 0xFFFE2000,
        .irq      = 0,
        .uartclk  = 1843200,
        .iotype   = UPIO_MEM,
        .flags    = UPF_BOOT_AUTOCONF | UPF_SKIP_TEST | UPF_IOREMAP,
        .regshift = 0,
    },
    { },  /* Terminator */
};

static struct platform_device mvme147_uart_device = {
    .name = "serial8250",
    .id   = PLAT8250_DEV_PLATFORM,
    .dev  = {
        .platform_data = mvme147_uart_data,
    },
};

static int __init mvme147_uart_init(void)
{
    if (!MACH_IS_MVME147)
        return -ENODEV;
    return platform_device_register(&mvme147_uart_device);
}
device_initcall(mvme147_uart_init);
```

### 2.4 Configure Kernel

```bash
# Start with mvme16x default config
make ARCH=m68k CROSS_COMPILE=m68k-linux-gnu- mvme16x_defconfig
make ARCH=m68k CROSS_COMPILE=m68k-linux-gnu- menuconfig
```

Verify these options are enabled. The menuconfig path for each option is shown in parentheses:

```
CONFIG_M68030=y               # (Processor type and features > 68030 support)
CONFIG_MMU=y                  # (automatically selected)
CONFIG_MVME147=y              # (Platform dependent setup > VME board support > Motorola MVME147 support)
CONFIG_SCSI=y                 # (Device Drivers > SCSI device support)
CONFIG_MVME147_SCSI=y         # (Device Drivers > SCSI device support > SCSI low-level drivers > WD33C93 SCSI driver for MVME147)
CONFIG_BLK_DEV_SD=y           # (Device Drivers > SCSI device support > SCSI disk support)
CONFIG_EXT4_FS=y              # (File systems > The Extended 4 (ext4) filesystem)
CONFIG_PROC_FS=y              # (File systems > Pseudo filesystems > /proc file system support)
CONFIG_SERIAL_8250=y          # (Device Drivers > Character devices > Serial drivers > 8250/16550 and compatible serial support)
CONFIG_SERIAL_8250_CONSOLE=y  # (Device Drivers > Character devices > Serial drivers > Console on 8250/16550 and compatible serial port)
CONFIG_NET=y                  # (Networking support)
CONFIG_INET=y                 # (Networking support > Networking options > TCP/IP networking)
CONFIG_NETDEVICES=y           # (Device Drivers > Network device support)
CONFIG_ETHERNET=y             # (Device Drivers > Network device support > Ethernet driver support)
CONFIG_NET_VENDOR_AMD=y       # (Device Drivers > Network device support > Ethernet driver support > AMD devices)
CONFIG_MVME147_NET=y          # (Device Drivers > Network device support > Ethernet driver support > AMD devices > MVME147 (LANCE) Ethernet support)
CONFIG_CGROUPS=y              # (General setup > Control Group support) -- required by systemd
CONFIG_MEMCG=y                # (General setup > Control Group support > Memory controller)
CONFIG_CGROUP_PIDS=y          # (General setup > Control Group support > PIDs controller)
CONFIG_CGROUP_FREEZER=y       # (General setup > Control Group support > Freezer controller)
CONFIG_CGROUP_DEVICE=y        # (General setup > Control Group support > Device controller)
CONFIG_CGROUP_BPF=y           # (General setup > Control Group support > Support for eBPF programs attached to cgroups)
```

> **Note**: `CONFIG_SERIAL_8250` and `CONFIG_SERIAL_8250_CONSOLE` are essential. Without them, the kernel cannot use the virtual 16550 UART and userspace will have no console (`Warning: unable to open an initial console`).

> **Note**: `CONFIG_CGROUPS` and its sub-options are required for systemd to start. Without them, systemd will fail at cgroup2 filesystem mount and the system will not boot to a login prompt.

> **Note**: `CONFIG_M68040` and `CONFIG_M68060` are enabled by default in `mvme16x_defconfig`. They can be left enabled -- the kernel detects the CPU type at runtime. Disabling them slightly reduces the kernel size.

> **Note**: `CONFIG_TRIM_UNUSED_KSYMS` is enabled by default in `mvme16x_defconfig`. If you plan to build out-of-tree kernel modules (e.g., `em68030fb`), disable this option. It strips unexported symbols from the kernel, causing `insmod` to fail with "Unknown symbol in module". In menuconfig: **General setup > Enable unused/obsolete exported symbols** → `n` (which sets `CONFIG_TRIM_UNUSED_KSYMS=y`; confusingly, `y` for the menu entry *disables* trimming).

> **Tip**: In menuconfig, press `/` to search for a config symbol by name (e.g., `SERIAL_8250`) to see its location and dependencies.

After saving the configuration in menuconfig, verify with `grep`:

```bash
grep -E "CONFIG_(M68030|MVME147|MVME147_SCSI|MVME147_NET|SCSI|BLK_DEV_SD|EXT4_FS|SERIAL_8250|SERIAL_8250_CONSOLE|NET_VENDOR_AMD|CGROUPS|MEMCG|CGROUP_PIDS|CGROUP_FREEZER|CGROUP_DEVICE|CGROUP_BPF)=" .config
```

All options should show `=y`. If an option is missing or shows `# CONFIG_XXX is not set`, re-run `menuconfig` and enable it.

### 2.5 Build

```bash
make ARCH=m68k CROSS_COMPILE=m68k-linux-gnu- vmlinux -j$(nproc)
```

The output is `vmlinux` -- an ELF binary that Em68030 can load directly via **File > Open ELF...**.

---

## Phase 3: Boot the System

### 3.1 Configure Em68030

1. Launch Em68030
2. Open **Settings**
3. Set **Board Type** to `MVME147`
4. Set **Target OS** to `Linux`
5. Add `debian.img` as a SCSI disk at **SCSI ID 0**
6. Set **Memory Size** to 128 MB
7. Set **Kernel command line** to: `root=/dev/sda1 console=ttyS0,9600`
8. Click **OK**

> **Important**: The **Target OS** setting must be `Linux`. This controls both the boot stub format (Linux bootinfo vs. NetBSD bootinfo) and the RTC year encoding.

### 3.2 Load and Boot

1. Select **File > Open ELF...** and load `vmlinux`
2. Press **F5** to start execution
3. Open **View > Console Window**

The kernel will display boot messages. After the boot sequence completes, systemd will start services and present a login prompt:

```
Debian GNU/Linux forky/sid mvme147 ttyS0

mvme147 login:
```

### 3.3 First Login

Login as `root` with the password you set in Phase 1.5.

### 3.4 Halt and Reboot

To shut down the system:

```
# halt
```

To reboot:

```
# reboot
```

The `reboot` command triggers the Linux kernel's `mvme147_reset()`, which uses the PCC watchdog timer to perform a hardware reset. The emulator detects this and performs a warm reboot (reloads the kernel ELF and restarts).

You can also press **Shift+F5** at any time to stop the emulator.

---

## Kernel Command Line Options

| Option | Description |
|---|---|
| `root=/dev/sda1` | Root filesystem device (required) |
| `console=ttyS0,9600` | Use the virtual 16550 UART as the system console (required) |
| `earlyprintk` | Enable early boot messages via the Z8530 SCC (optional, for debugging) |
| `keep_bootcon` | Keep the early boot console active after ttyS0 registers (optional; requires Patch 1) |

For normal operation, `root=/dev/sda1 console=ttyS0,9600` is sufficient.

---

## Debian vs Gentoo for m68k

| | Debian | Gentoo |
|---|---|---|
| **Package management** | `apt` -- pre-built binary packages | `emerge` -- builds from source |
| **Installation method** | `debootstrap` -- quick, no cross-compilation needed | stage3 tarball extraction |
| **Package availability** | ~12,000 packages in m68k port | Fewer tested packages |
| **Maintenance** | Active Debian Ports community | Limited m68k maintainers |
| **Resource usage** | Moderate (systemd) | Minimal (OpenRC) |
| **Suitability for emulator** | Better -- binary packages avoid slow on-target compilation | Source compilation is impractical at emulated speeds |

Debian is generally recommended for m68k emulation because binary packages eliminate the need for slow on-target compilation.

---

## Known Limitations

1. **Debian m68k is unofficial** -- The m68k port is maintained under Debian Ports, not the main Debian archive. Package availability and testing may be limited.
2. **No network support in emulator** -- `apt install` requires network access. Pre-install packages during the debootstrap phase, or implement network emulation.
3. **Virtual 16550 UART is non-standard** -- The 16550 UART at 0xFFFE2000 does not exist on real MVME147 hardware. The kernel patches in Phase 2.3 are specific to the Em68030 emulator.
4. **Kernel patches required** -- The stock Linux kernel does not include support for the emulator's virtual UART. The two patches described in Phase 2.3 are mandatory.

## Troubleshooting

### debootstrap second stage fails with "Exec format error"

- Ensure `qemu-user-static` and `binfmt-support` are installed and active
- Verify binfmt_misc is registered with the `F` flag: `cat /proc/sys/fs/binfmt_misc/qemu-m68k` -- the output must include `flags: F`
- If the `F` flag is missing, re-register (see step 1.1) -- without it, the kernel cannot find the QEMU interpreter inside a chroot
- Try `sudo systemctl restart systemd-binfmt` to re-register binfmt entries

### Kernel panics at boot

- Ensure the kernel is built with `CONFIG_MVME147=y` and `CONFIG_M68030=y`
- Verify that the kernel source patches (Phase 2.3) are applied correctly
- Try **Run > Full Reset** before loading the kernel

### "VFS: Cannot open root device"

- Check that `CONFIG_MVME147_SCSI=y` and `CONFIG_BLK_DEV_SD=y` are enabled in the kernel
- Verify the root filesystem partition contains a valid ext4 filesystem
- Ensure the kernel command line specifies the correct root device (`root=/dev/sda1`)

### "Warning: unable to open an initial console"

- This means the 8250/16550 serial driver is not compiled into the kernel
- Verify `CONFIG_SERIAL_8250=y` and `CONFIG_SERIAL_8250_CONSOLE=y` are set
- Verify the kernel source patch for the UART platform device (Patch 2) is applied
- Rebuild the kernel

### No console output after "printk: legacy bootconsole [debug0] disabled"

- The early boot console (Z8530 SCC) has been replaced by the 16550 UART console (`ttyS0`)
- If `ttyS0` is not registered, output stops here. Verify the UART platform device patch and `CONFIG_SERIAL_8250=y`
- If using `earlyprintk`, add `keep_bootcon` to keep both consoles active (requires Patch 1)

### systemd fails with cgroup2 mount error

- Enable `CONFIG_CGROUPS=y` in the kernel configuration and rebuild

### Date/time is wrong (e.g., year 2058 instead of 2026)

- Ensure **Target OS** is set to `Linux` in Em68030 settings
- The RTC year encoding differs between NetBSD and Linux; the wrong setting causes year miscalculation

---

## References

- [Debian Ports -- m68k](https://www.ports.debian.org/)
- [Debian m68k Wiki](https://wiki.debian.org/M68k)
- [Linux/m68k FAQ](https://www.linux-m68k.org/faq/faq.html)
- [Kernel Source: arch/m68k/kernel/head.S](https://git.kernel.org/pub/scm/linux/kernel/git/torvalds/linux.git/tree/arch/m68k/kernel/head.S) -- boot entry point and bootinfo parsing
- [debootstrap manual](https://wiki.debian.org/Debootstrap)
