#!/bin/bash
# Create a Debian/m68k disk image for the Em68030 emulator.
#
# Runs debootstrap to install a minimal Debian sid (unstable) system
# on a raw SCSI disk image with MBR partitions (root + swap).
#
# Usage:
#   sudo ./create-debian-disk.sh [options]
#
# Options:
#   -s SIZE       Disk image size (default: 1G, min: 500M, max: 4T)
#   -p PASSWORD   Root password (default: root)
#   -o FILE       Output disk image file (default: debian.img)
#   -w SWAP_MB    Swap partition size in MB (default: 64)
#   -n            Enable NAT network configuration (10.0.2.15/24)
#   -h            Show this help message
#
# Requirements (on Linux/WSL2):
#   - Root privileges (sudo)
#   - debootstrap, qemu-user-static, sfdisk, mkfs.ext4, mkswap, openssl
#   - binfmt_misc with F flag for qemu-m68k-static
#
# Examples:
#   sudo ./create-debian-disk.sh -s 1G -o debian.img
#   sudo ./create-debian-disk.sh -s 2G -p mypassword -n -o debian.img

set -euo pipefail

# Defaults
SIZE="1G"
PASSWORD="root"
OUTPUT="debian.img"
SWAP_MB=64
NAT=0

# Constants
SUITE="sid"
MIRROR="http://ftp.ports.debian.org/debian-ports/"
SECSIZE=512

die() { echo "Error: $*" >&2; exit 1; }

parse_size() {
    local input="$1"
    local num unit
    num=$(echo "$input" | sed 's/[^0-9]//g')
    unit=$(echo "$input" | sed 's/[0-9]//g' | tr '[:lower:]' '[:upper:]')
    case "$unit" in
        M|MB) echo $((num * 1024 * 1024)) ;;
        G|GB) echo $((num * 1024 * 1024 * 1024)) ;;
        T|TB) echo $((num * 1024 * 1024 * 1024 * 1024)) ;;
        *)    die "Unknown size unit '$unit'. Use M, G, or T." ;;
    esac
}

cleanup() {
    set +e
    echo "Cleaning up..."
    [ -n "${MOUNTPOINT:-}" ] && {
        umount "$MOUNTPOINT/dev/pts" 2>/dev/null
        umount "$MOUNTPOINT/dev" 2>/dev/null
        umount "$MOUNTPOINT/proc" 2>/dev/null
        umount "$MOUNTPOINT/sys" 2>/dev/null
        umount "$MOUNTPOINT" 2>/dev/null
        rmdir "$MOUNTPOINT" 2>/dev/null
    }
    [ -n "${LOOPDEV:-}" ] && losetup -d "$LOOPDEV" 2>/dev/null
}
trap cleanup EXIT

usage() {
    sed -n '2,/^$/s/^# \?//p' "$0"
    exit "${1:-0}"
}

while getopts "s:p:o:w:nh" opt; do
    case $opt in
        s) SIZE="$OPTARG" ;;
        p) PASSWORD="$OPTARG" ;;
        o) OUTPUT="$OPTARG" ;;
        w) SWAP_MB="$OPTARG" ;;
        n) NAT=1 ;;
        h) usage 0 ;;
        *) usage 1 ;;
    esac
done

# Validate environment
[ "$(id -u)" -eq 0 ] || die "Must run as root (use sudo)"

# Install missing packages
MISSING_PKGS=()
command -v debootstrap >/dev/null || MISSING_PKGS+=(debootstrap)
command -v sfdisk >/dev/null || MISSING_PKGS+=(fdisk)
command -v mkfs.ext4 >/dev/null || MISSING_PKGS+=(e2fsprogs)
command -v qemu-m68k-static >/dev/null || MISSING_PKGS+=(qemu-user-static)
command -v openssl >/dev/null || MISSING_PKGS+=(openssl)

if [ ${#MISSING_PKGS[@]} -gt 0 ]; then
    echo "The following packages are required but not installed: ${MISSING_PKGS[*]}"
    printf "Install them now? [y/N] "
    read -r REPLY
    case "$REPLY" in
        [yY]|[yY][eE][sS]) ;;
        *) die "Required packages not installed: ${MISSING_PKGS[*]}" ;;
    esac
    apt-get update -qq
    apt-get install -y -qq "${MISSING_PKGS[@]}" \
        || die "Failed to install packages: ${MISSING_PKGS[*]}"
fi

# Check qemu-m68k-static version (>= 6.0 required for Debian sid)
QEMU_VER=$(qemu-m68k-static --version 2>/dev/null | head -1 | grep -oP 'version \K[0-9]+\.[0-9]+' || echo "0.0")
QEMU_MAJOR=$(echo "$QEMU_VER" | cut -d. -f1)
if [ "$QEMU_MAJOR" -lt 6 ] 2>/dev/null; then
    die "qemu-m68k-static $QEMU_VER is too old (>= 6.0 required).\n  Upgrade your distro or install a newer qemu-user-static.\n  Current Debian sid packages require recent QEMU for m68k instruction support."
fi

# Ensure binfmt_misc is mounted
if [ ! -d /proc/sys/fs/binfmt_misc ]; then
    mount -t binfmt_misc binfmt_misc /proc/sys/fs/binfmt_misc \
        || die "Failed to mount binfmt_misc"
fi

# Register qemu-m68k with F flag if not present
BINFMT_ENTRY=':qemu-m68k:M::\x7fELF\x01\x02\x01\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x02\x00\x04:\xff\xff\xff\xff\xff\xff\xff\x00\x00\x00\x00\x00\x00\x00\x00\x00\xff\xfe\xff\xff:/usr/bin/qemu-m68k-static:F'
if [ ! -f /proc/sys/fs/binfmt_misc/qemu-m68k ]; then
    echo "Registering qemu-m68k binfmt_misc handler..."
    printf '%s\n' "$BINFMT_ENTRY" > /proc/sys/fs/binfmt_misc/register \
        || die "Failed to register qemu-m68k binfmt_misc handler"
elif ! grep -q 'flags:.*F' /proc/sys/fs/binfmt_misc/qemu-m68k; then
    echo "Re-registering qemu-m68k with F flag..."
    echo -1 > /proc/sys/fs/binfmt_misc/qemu-m68k
    printf '%s\n' "$BINFMT_ENTRY" > /proc/sys/fs/binfmt_misc/register \
        || die "Failed to re-register qemu-m68k binfmt_misc handler"
fi

# Parse and validate size
SIZE_BYTES=$(parse_size "$SIZE")
MIN_BYTES=$((500 * 1024 * 1024))
MAX_BYTES=$((4 * 1024 * 1024 * 1024 * 1024))
[ "$SIZE_BYTES" -lt "$MIN_BYTES" ] && die "Disk size must be at least 500M (got $SIZE)"
[ "$SIZE_BYTES" -gt "$MAX_BYTES" ] && die "Disk size must be at most 4T (got $SIZE)"

SIZE_MB=$((SIZE_BYTES / 1024 / 1024))
ROOT_MB=$((SIZE_MB - SWAP_MB))
[ "$ROOT_MB" -lt 300 ] && die "Root partition too small (${ROOT_MB} MB). Increase disk size or reduce swap."

echo "=== Em68030 Debian Disk Image Creator ==="
echo "Image size:   ${SIZE_MB} MB (root: ${ROOT_MB} MB, swap: ${SWAP_MB} MB)"
echo "Suite:        $SUITE"
echo "Root password: $PASSWORD"
echo "NAT network:  $([ $NAT -eq 1 ] && echo "enabled (10.0.2.15/24)" || echo "disabled")"
echo "Output:       $OUTPUT"
echo ""

# Step 1: Create disk image
echo "[1/7] Creating disk image..."
dd if=/dev/zero of="$OUTPUT" bs=1M count="$SIZE_MB" status=progress

# Step 2: Partition (MBR: root + swap)
echo "[2/7] Partitioning..."
sfdisk "$OUTPUT" << EOF
label: dos
,${ROOT_MB}M,L
,,S
EOF

# Step 3: Set up loop device, format, mount
echo "[3/7] Formatting..."
LOOPDEV=$(losetup --show -fP "$OUTPUT")
echo "  Loop device: $LOOPDEV"

mkfs.ext4 -q "${LOOPDEV}p1"
mkswap "${LOOPDEV}p2"

MOUNTPOINT=$(mktemp -d /tmp/em68030-debian.XXXXXX)
mount "${LOOPDEV}p1" "$MOUNTPOINT"

# Step 4: debootstrap
echo "[4/7] Running debootstrap (first stage)..."
debootstrap --arch=m68k --foreign --no-check-gpg \
    "$SUITE" "$MOUNTPOINT" "$MIRROR"

echo "[4/7] Running debootstrap (second stage)..."

# Ensure qemu-m68k-static is available inside chroot (needed without binfmt F flag)
QEMU_BIN=$(command -v qemu-m68k-static)
if [ -n "$QEMU_BIN" ]; then
    mkdir -p "$MOUNTPOINT/$(dirname "$QEMU_BIN")"
    cp "$QEMU_BIN" "$MOUNTPOINT/$QEMU_BIN"
fi

chroot "$MOUNTPOINT" /debootstrap/debootstrap --second-stage

# Clean up qemu binary from target (not needed at runtime in emulator)
rm -f "$MOUNTPOINT/$QEMU_BIN"

# Step 5: Configure root filesystem
echo "[5/7] Configuring system..."

# Mount virtual filesystems
mount --bind /dev "$MOUNTPOINT/dev"
mount --bind /dev/pts "$MOUNTPOINT/dev/pts"
mount -t proc proc "$MOUNTPOINT/proc"
mount -t sysfs sysfs "$MOUNTPOINT/sys"

# fstab
cat > "$MOUNTPOINT/etc/fstab" << 'EOF'
/dev/sda1    /        ext4    defaults,noatime    0 1
/dev/sda2    none     swap    sw                  0 0
proc         /proc    proc    defaults            0 0
sysfs        /sys     sysfs   defaults            0 0
devtmpfs     /dev     devtmpfs defaults           0 0
EOF

# Hostname
echo "mvme147" > "$MOUNTPOINT/etc/hostname"
echo "127.0.1.1 mvme147" >> "$MOUNTPOINT/etc/hosts"

# Root password
HASHED=$(openssl passwd -6 "$PASSWORD")
chroot "$MOUNTPOINT" /bin/sh -c "usermod -p '$HASHED' root"

# Serial console — enable serial-getty@ttyS0 via symlink (systemctl doesn't work in chroot)
mkdir -p "$MOUNTPOINT/etc/systemd/system/getty.target.wants"
ln -sf /lib/systemd/system/serial-getty@.service \
    "$MOUNTPOINT/etc/systemd/system/getty.target.wants/serial-getty@ttyS0.service"

# APT sources
cat > "$MOUNTPOINT/etc/apt/sources.list" << 'EOF'
deb http://ftp.ports.debian.org/debian-ports/ sid main
deb http://ftp.ports.debian.org/debian-ports/ unreleased main
EOF

# Debian Ports keyring
echo "[6/7] Installing Debian Ports keyring..."
chroot "$MOUNTPOINT" /bin/sh -c \
    "apt -o Acquire::AllowInsecureRepositories=true update && apt install -y debian-ports-archive-keyring" \
    2>/dev/null || echo "  Warning: Failed to install keyring (can be done later)"

# NAT network
if [ $NAT -eq 1 ]; then
    mkdir -p "$MOUNTPOINT/etc/systemd/network"
    cat > "$MOUNTPOINT/etc/systemd/network/10-eth0.network" << 'EOF'
[Match]
Name=eth0

[Network]
Address=10.0.2.15/24
Gateway=10.0.2.2
EOF
    cat > "$MOUNTPOINT/etc/resolv.conf" << 'EOF'
nameserver 8.8.8.8
EOF
    chroot "$MOUNTPOINT" /bin/sh -c "systemctl enable systemd-networkd 2>/dev/null" || true
fi

# Step 7: Unmount
echo "[7/7] Unmounting..."
umount "$MOUNTPOINT/dev/pts"
umount "$MOUNTPOINT/dev"
umount "$MOUNTPOINT/proc"
umount "$MOUNTPOINT/sys"
umount "$MOUNTPOINT"
rmdir "$MOUNTPOINT"
MOUNTPOINT=""

losetup -d "$LOOPDEV"
LOOPDEV=""

echo ""
echo "=== Done ==="
echo "Disk image: $OUTPUT"
echo ""
echo "Boot with emulator settings:"
echo "  Board:        MVME147"
echo "  Target OS:    Linux"
echo "  SCSI Disk 0:  $OUTPUT"
echo "  Kernel:       vmlinux (from Em68030-Guest-Linux)"
echo "  Command line: root=/dev/sda1 console=ttyS0,9600"
