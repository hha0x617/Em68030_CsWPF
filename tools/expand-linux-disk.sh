#!/bin/bash
# Expand an existing Linux disk image for the Em68030 emulator.
#
# Extends the image file, rewrites the MBR partition table with expanded
# root partition and relocated swap, and resizes the filesystem.
#
# Usage:
#   sudo ./expand-linux-disk.sh [options] IMAGE
#
# Options:
#   -s SIZE       New disk image size (default: 2G, min: 500M, max: 4T)
#   -w SWAP_MB    Swap partition size in MB (default: 64)
#   -f FSTYPE     Filesystem type: ext2, ext3, ext4 (default: auto-detect)
#   -h            Show this help message
#
# The script:
#   1. Extends the image file (existing data preserved)
#   2. Rewrites the MBR partition table (root + swap)
#   3. Resizes the root filesystem using resize2fs
#
# Requirements (on Linux/WSL2):
#   - Root privileges (sudo)
#   - sfdisk, e2fsck, resize2fs, mkswap
#
# Examples:
#   sudo ./expand-linux-disk.sh -s 2G debian.img
#   sudo ./expand-linux-disk.sh -s 4G -w 128 gentoo.img

set -euo pipefail

# Defaults
SIZE="2G"
SWAP_MB=64
FSTYPE=""

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
    [ -n "${LOOPDEV:-}" ] && losetup -d "$LOOPDEV" 2>/dev/null
}
trap cleanup EXIT

usage() {
    sed -n '2,/^$/s/^# \?//p' "$0"
    exit "${1:-0}"
}

while getopts "s:w:f:h" opt; do
    case $opt in
        s) SIZE="$OPTARG" ;;
        w) SWAP_MB="$OPTARG" ;;
        f) FSTYPE="$OPTARG" ;;
        h) usage 0 ;;
        *) usage 1 ;;
    esac
done
shift $((OPTIND - 1))

[ $# -ge 1 ] || die "Image file required. Usage: $0 [options] IMAGE"
IMAGE="$1"
[ -f "$IMAGE" ] || die "Image file not found: $IMAGE"

# Validate environment
[ "$(id -u)" -eq 0 ] || die "Must run as root (use sudo)"
command -v sfdisk >/dev/null || die "sfdisk not found. Install: apt install fdisk"
command -v resize2fs >/dev/null || die "resize2fs not found. Install: apt install e2fsprogs"
command -v e2fsck >/dev/null || die "e2fsck not found. Install: apt install e2fsprogs"

# Current image size
CUR_BYTES=$(stat -c%s "$IMAGE" 2>/dev/null || stat -f%z "$IMAGE")
CUR_MB=$((CUR_BYTES / 1024 / 1024))

# Parse and validate new size
NEW_BYTES=$(parse_size "$SIZE")
MIN_BYTES=$((500 * 1024 * 1024))
MAX_BYTES=$((4 * 1024 * 1024 * 1024 * 1024))
[ "$NEW_BYTES" -lt "$MIN_BYTES" ] && die "Size must be at least 500M (got $SIZE)"
[ "$NEW_BYTES" -gt "$MAX_BYTES" ] && die "Size must be at most 4T (got $SIZE)"

NEW_MB=$((NEW_BYTES / 1024 / 1024))
[ "$NEW_BYTES" -le "$CUR_BYTES" ] && die "New size (${NEW_MB} MB) must be larger than current size (${CUR_MB} MB)"

ROOT_MB=$((NEW_MB - SWAP_MB))
[ "$ROOT_MB" -lt 300 ] && die "Root partition too small (${ROOT_MB} MB). Increase disk size or reduce swap."

echo "=== Em68030 Linux Disk Image Expander ==="
echo "Image:      $IMAGE"
echo "Current:    ${CUR_MB} MB"
echo "New size:   ${NEW_MB} MB (root: ${ROOT_MB} MB, swap: ${SWAP_MB} MB)"
echo ""

# Step 1: Extend the image file
echo "[1/4] Extending image to ${NEW_MB} MB..."
if command -v truncate >/dev/null 2>&1; then
    truncate -s "${NEW_MB}M" "$IMAGE"
else
    dd if=/dev/zero of="$IMAGE" bs=1 count=0 seek="$NEW_BYTES" 2>/dev/null
fi
echo "  Image extended (existing data preserved)."

# Step 2: Rewrite partition table
echo "[2/4] Rewriting partition table..."
sfdisk "$IMAGE" << EOF
label: dos
,${ROOT_MB}M,L
,,S
EOF

# Step 3: Resize filesystem
echo "[3/4] Resizing filesystem..."
LOOPDEV=$(losetup --show -fP "$IMAGE")
echo "  Loop device: $LOOPDEV"

# Auto-detect filesystem type if not specified
if [ -z "$FSTYPE" ]; then
    FSTYPE=$(blkid -o value -s TYPE "${LOOPDEV}p1" 2>/dev/null || echo "ext4")
    echo "  Detected filesystem: $FSTYPE"
fi

echo "  Running e2fsck..."
e2fsck -f -y "${LOOPDEV}p1" || true

echo "  Running resize2fs..."
resize2fs "${LOOPDEV}p1"

# Step 4: Recreate swap
echo "[4/4] Recreating swap partition..."
mkswap "${LOOPDEV}p2"

losetup -d "$LOOPDEV"
LOOPDEV=""

echo ""
echo "=== Done ==="
echo "Disk image: $IMAGE (${NEW_MB} MB)"
echo ""
echo "The root filesystem has been resized. No guest-side action needed."
