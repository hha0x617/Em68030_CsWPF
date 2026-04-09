#!/bin/bash
# Create a Gentoo/m68k disk image for the Em68030 emulator.
#
# Extracts a Gentoo stage3 tarball onto a raw SCSI disk image
# with MBR partitions (root + swap).
#
# Usage:
#   sudo ./create-gentoo-disk.sh -t TARBALL [options]
#
# Options:
#   -t TARBALL    Gentoo stage3 tarball (required)
#                 Download from: https://distfiles.gentoo.org/releases/m68k/autobuilds/
#   -s SIZE       Disk image size (default: 2G, min: 500M, max: 4T)
#   -p PASSWORD   Root password (default: root)
#   -o FILE       Output disk image file (default: gentoo.img)
#   -w SWAP_MB    Swap partition size in MB (default: 64)
#   -i INIT       Init system: openrc or systemd
#                 (auto-detected from tarball filename if omitted)
#   -n            Enable NAT network configuration (10.0.2.15/24)
#   -h            Show this help message
#
# Requirements (on Linux/WSL2):
#   - Root privileges (sudo)
#   - sfdisk, mkfs.ext2, mkswap, openssl, tar (with xz support)
#
# Download stage3 tarball:
#   Available variants: m68k-openrc, m68k-systemd, m68k_musl-openrc, m68k_musl-systemd
#   wget https://distfiles.gentoo.org/releases/m68k/autobuilds/current-stage3-m68k-openrc/stage3-m68k-openrc-<DATE>.tar.xz
#
#   To find the latest filename:
#   curl -s https://distfiles.gentoo.org/releases/m68k/autobuilds/latest-stage3-m68k-openrc.txt
#   curl -s https://distfiles.gentoo.org/releases/m68k/autobuilds/latest-stage3-m68k-systemd.txt
#
# Examples:
#   sudo ./create-gentoo-disk.sh -t stage3-m68k-openrc-20260403T164601Z.tar.xz
#   sudo ./create-gentoo-disk.sh -t stage3-m68k-systemd-20260403T164601Z.tar.xz -i systemd -s 4G -n

set -euo pipefail

# Defaults
TARBALL=""
SIZE="2G"
PASSWORD="root"
OUTPUT="gentoo.img"
SWAP_MB=64
INIT=""
INIT_EXPLICIT=0
NAT=0

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

while getopts "t:s:p:o:w:i:nh" opt; do
    case $opt in
        t) TARBALL="$OPTARG" ;;
        s) SIZE="$OPTARG" ;;
        p) PASSWORD="$OPTARG" ;;
        o) OUTPUT="$OPTARG" ;;
        w) SWAP_MB="$OPTARG" ;;
        i) INIT="$OPTARG"; INIT_EXPLICIT=1 ;;
        n) NAT=1 ;;
        h) usage 0 ;;
        *) usage 1 ;;
    esac
done

# Validate
[ "$(id -u)" -eq 0 ] || die "Must run as root (use sudo)"
[ -n "$TARBALL" ] || die "Stage3 tarball required (-t). Download from:\n  https://distfiles.gentoo.org/releases/m68k/autobuilds/"
[ -f "$TARBALL" ] || die "Tarball not found: $TARBALL"

# Install missing packages
MISSING_PKGS=()
command -v sfdisk >/dev/null || MISSING_PKGS+=(fdisk)
command -v mkfs.ext2 >/dev/null || MISSING_PKGS+=(e2fsprogs)
command -v openssl >/dev/null || MISSING_PKGS+=(openssl)

if [ ${#MISSING_PKGS[@]} -gt 0 ]; then
    echo "Installing missing packages: ${MISSING_PKGS[*]}"
    apt-get update -qq
    apt-get install -y -qq "${MISSING_PKGS[@]}" \
        || die "Failed to install packages: ${MISSING_PKGS[*]}"
fi

# Auto-detect init system from tarball filename if -i not specified
if [ $INIT_EXPLICIT -eq 0 ]; then
    TARBALL_BASE=$(basename "$TARBALL")
    case "$TARBALL_BASE" in
        *systemd*)
            INIT="systemd"
            echo "Auto-detected init system: systemd (from filename '$TARBALL_BASE')"
            ;;
        *)
            INIT="openrc"
            echo "Auto-detected init system: openrc (from filename '$TARBALL_BASE')"
            ;;
    esac
fi

case "$INIT" in
    openrc|systemd) ;;
    *) die "Invalid init system '$INIT'. Use 'openrc' or 'systemd'." ;;
esac

# Parse and validate size
SIZE_BYTES=$(parse_size "$SIZE")
MIN_BYTES=$((500 * 1024 * 1024))
MAX_BYTES=$((4 * 1024 * 1024 * 1024 * 1024))
[ "$SIZE_BYTES" -lt "$MIN_BYTES" ] && die "Disk size must be at least 500M (got $SIZE)"
[ "$SIZE_BYTES" -gt "$MAX_BYTES" ] && die "Disk size must be at most 4T (got $SIZE)"

SIZE_MB=$((SIZE_BYTES / 1024 / 1024))
ROOT_MB=$((SIZE_MB - SWAP_MB))
[ "$ROOT_MB" -lt 300 ] && die "Root partition too small (${ROOT_MB} MB). Increase disk size or reduce swap."

echo "=== Em68030 Gentoo Disk Image Creator ==="
echo "Image size:   ${SIZE_MB} MB (root: ${ROOT_MB} MB, swap: ${SWAP_MB} MB)"
echo "Stage3:       $TARBALL"
echo "Init system:  $INIT"
echo "Root password: $PASSWORD"
echo "NAT network:  $([ $NAT -eq 1 ] && echo "enabled (10.0.2.15/24)" || echo "disabled")"
echo "Output:       $OUTPUT"
echo ""

# Step 1: Create disk image
echo "[1/6] Creating disk image..."
dd if=/dev/zero of="$OUTPUT" bs=1M count="$SIZE_MB" status=progress

# Step 2: Partition (MBR: root + swap)
echo "[2/6] Partitioning..."
sfdisk "$OUTPUT" << EOF
label: dos
,${ROOT_MB}M,L
,,S
EOF

# Step 3: Set up loop device, format, mount
echo "[3/6] Formatting (ext2)..."
LOOPDEV=$(losetup --show -fP "$OUTPUT")
echo "  Loop device: $LOOPDEV"

mkfs.ext2 -q "${LOOPDEV}p1"
mkswap "${LOOPDEV}p2"

MOUNTPOINT=$(mktemp -d /tmp/em68030-gentoo.XXXXXX)
mount "${LOOPDEV}p1" "$MOUNTPOINT"

# Step 4: Extract stage3
echo "[4/6] Extracting stage3 tarball (this may take a while)..."
tar xpf "$TARBALL" -C "$MOUNTPOINT" --xattrs-include='*.*' --numeric-owner

# Step 5: Configure root filesystem
echo "[5/6] Configuring system..."

# fstab
cat > "$MOUNTPOINT/etc/fstab" << 'EOF'
/dev/sda1    /        ext2    defaults,noatime    0 1
/dev/sda2    none     swap    sw                  0 0
proc         /proc    proc    defaults            0 0
sysfs        /sys     sysfs   defaults            0 0
devtmpfs     /dev     devtmpfs defaults           0 0
EOF

# Hostname
echo "mvme147" > "$MOUNTPOINT/etc/hostname"

# Root password
HASHED=$(openssl passwd -6 "$PASSWORD")
sed -i "s|root:[^:]*|root:${HASHED}|" "$MOUNTPOINT/etc/shadow"

# Serial console
if [ "$INIT" = "openrc" ]; then
    # OpenRC: add to inittab
    if ! grep -q 'ttyS0' "$MOUNTPOINT/etc/inittab" 2>/dev/null; then
        echo "s0:12345:respawn:/sbin/agetty 9600 ttyS0 vt100" >> "$MOUNTPOINT/etc/inittab"
    fi
else
    # systemd: enable serial-getty
    mkdir -p "$MOUNTPOINT/etc/systemd/system/getty.target.wants"
    ln -sf /usr/lib/systemd/system/serial-getty@.service \
        "$MOUNTPOINT/etc/systemd/system/getty.target.wants/serial-getty@ttyS0.service"
fi

# NAT network
if [ $NAT -eq 1 ]; then
    if [ "$INIT" = "openrc" ]; then
        cat > "$MOUNTPOINT/etc/conf.d/net" << 'EOF'
config_eth0="10.0.2.15/24"
routes_eth0="default via 10.0.2.2"
EOF
        cd "$MOUNTPOINT/etc/init.d" && ln -sf net.lo net.eth0
        cd - > /dev/null
    else
        mkdir -p "$MOUNTPOINT/etc/systemd/network"
        cat > "$MOUNTPOINT/etc/systemd/network/10-eth0.network" << 'EOF'
[Match]
Name=eth0

[Network]
Address=10.0.2.15/24
Gateway=10.0.2.2
EOF
    fi
    cat > "$MOUNTPOINT/etc/resolv.conf" << 'EOF'
nameserver 8.8.8.8
EOF
fi

# Step 6: Unmount
echo "[6/6] Unmounting..."
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
