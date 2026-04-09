#!/bin/bash
# Expand an existing NetBSD disk image for the Em68030 emulator.
#
# Extends the image file, rewrites the disklabel with new partition layout,
# and prints instructions for resizing the filesystem inside NetBSD.
#
# Usage:
#   ./expand-netbsd-disk.sh [options] IMAGE
#
# Options:
#   -s SIZE       New disk image size (min: 500M, max: 4T)
#                 If omitted or equal to current size, only the disklabel is rewritten.
#   -w SWAP_MB    Swap partition (sd0b) size in MB (default: 32)
#   -h            Show this help message
#
# The script:
#   1. Validates that the new size is larger than the current image
#   2. Extends the image file (existing data preserved)
#   3. Rewrites the disklabel with expanded sd0a and relocated sd0b
#
# After running this script, boot NetBSD and resize the filesystem:
#   # resize_ffs /dev/sd0a
#
# Examples:
#   ./expand-netbsd-disk.sh -s 2G disk.img
#   ./expand-netbsd-disk.sh -s 4G -w 64 disk.img

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Defaults
SIZE=""
SWAP_MB=32

# SCSI geometry (must match emulator)
HEADS=64
SECTORS=32
SECSIZE=512
SECPERCYL=$((HEADS * SECTORS))  # 2048

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

usage() {
    sed -n '2,/^$/s/^# \?//p' "$0"
    exit "${1:-0}"
}

while getopts "s:w:h" opt; do
    case $opt in
        s) SIZE="$OPTARG" ;;
        w) SWAP_MB="$OPTARG" ;;
        h) usage 0 ;;
        *) usage 1 ;;
    esac
done
shift $((OPTIND - 1))

[ $# -ge 1 ] || die "Image file required. Usage: $0 [options] IMAGE"
IMAGE="$1"
[ -f "$IMAGE" ] || die "Image file not found: $IMAGE"

# Parse size (if specified)
LABEL_ONLY=0
if [ -n "$SIZE" ]; then
    NEW_BYTES=$(parse_size "$SIZE")
    MIN_BYTES=$((500 * 1024 * 1024))
    MAX_BYTES=$((4 * 1024 * 1024 * 1024 * 1024))
    [ "$NEW_BYTES" -lt "$MIN_BYTES" ] && die "Size must be at least 500M (got $SIZE)"
    [ "$NEW_BYTES" -gt "$MAX_BYTES" ] && die "Size must be at most 4T (got $SIZE)"
fi

# Current image size
if stat --version >/dev/null 2>&1; then
    # GNU stat
    CUR_BYTES=$(stat -c%s "$IMAGE")
else
    # BSD stat
    CUR_BYTES=$(stat -f%z "$IMAGE")
fi
CUR_MB=$((CUR_BYTES / 1024 / 1024))

# If -s not specified or same size, rewrite label only
if [ -z "$SIZE" ]; then
    NEW_BYTES=$CUR_BYTES
    LABEL_ONLY=1
elif [ "$NEW_BYTES" -eq "$CUR_BYTES" ]; then
    LABEL_ONLY=1
elif [ "$NEW_BYTES" -lt "$CUR_BYTES" ]; then
    die "New size must be >= current size (${CUR_MB} MB)"
fi

# Align to cylinder boundary
TOTAL_SECTORS=$((NEW_BYTES / SECSIZE))
TOTAL_CYL=$((TOTAL_SECTORS / SECPERCYL))
TOTAL_SECTORS=$((TOTAL_CYL * SECPERCYL))
NEW_BYTES=$((TOTAL_SECTORS * SECSIZE))
NEW_MB=$((NEW_BYTES / 1024 / 1024))

# Partition layout (VID format: sd0a starts at sector 0)
SWAP_SECTORS=$((SWAP_MB * 2048))
A_SECTORS=$((TOTAL_SECTORS - SWAP_SECTORS))

echo "=== Em68030 NetBSD Disk Image Expander ==="
echo "Image:      $IMAGE"
echo "Current:    ${CUR_MB} MB"
if [ $LABEL_ONLY -eq 1 ]; then
    echo "Mode:       Rewrite disklabel only (no size change)"
else
    echo "New size:   ${NEW_MB} MB ($TOTAL_CYL cylinders, $TOTAL_SECTORS sectors)"
fi
echo "sd0a:       sector 0 - $((A_SECTORS - 1)) ($((A_SECTORS / 2048)) MB, root)"
echo "sd0b:       sector $A_SECTORS - $((TOTAL_SECTORS - 1)) ($SWAP_MB MB, swap)"
echo ""

# Compile mkdisklabel if needed
MKDISKLABEL="$SCRIPT_DIR/mkdisklabel"
if [ ! -x "$MKDISKLABEL" ] || [ "$SCRIPT_DIR/mkdisklabel.c" -nt "$MKDISKLABEL" ]; then
    if ! command -v "${CC:-gcc}" >/dev/null 2>&1; then
        echo "The following packages are required but not installed: gcc"
        printf "Install them now? [y/N] "
        read -r REPLY
        case "$REPLY" in
            [yY]|[yY][eE][sS]) ;;
            *) die "Required packages not installed: gcc" ;;
        esac
        apt-get update -qq
        apt-get install -y -qq gcc libc6-dev \
            || die "Failed to install packages: gcc libc6-dev"
    fi
    echo "Compiling mkdisklabel..."
    ${CC:-gcc} -o "$MKDISKLABEL" "$SCRIPT_DIR/mkdisklabel.c"
fi

if [ $LABEL_ONLY -eq 0 ]; then
    # Step 1: Extend the image file
    echo "[1/2] Extending image to ${NEW_MB} MB..."
    if command -v truncate >/dev/null 2>&1; then
        truncate -s "$NEW_BYTES" "$IMAGE"
    else
        dd if=/dev/zero of="$IMAGE" bs=1 count=0 seek="$NEW_BYTES" 2>/dev/null
    fi
    echo "  Image extended (existing data preserved)."
fi

# Rewrite disklabel
if [ $LABEL_ONLY -eq 1 ]; then
    echo "Rewriting disklabel..."
else
    echo "[2/2] Rewriting disklabel..."
fi
"$MKDISKLABEL" update "$IMAGE" "$TOTAL_SECTORS" "$SWAP_SECTORS"

echo ""
echo "=== Done ==="
echo ""
echo "The image file has been expanded and the disklabel updated."
echo "sd0b (swap) has been relocated to the end of the disk."
echo ""
echo "Next step: boot NetBSD and resize the root filesystem:"
echo ""
echo "  # resize_ffs /dev/sd0a"
echo ""
echo "If resize_ffs is not available, boot in single-user mode and run:"
echo ""
echo "  mount -o rw /dev/sd0a /"
echo "  resize_ffs /dev/sd0a"
echo "  reboot"
