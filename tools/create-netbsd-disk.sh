#!/bin/bash
# Create a NetBSD disk image for the Em68030 emulator.
#
# Creates a raw SCSI disk image with a NetBSD disklabel.
# If a miniroot image is provided, it is placed on sd0b for installation.
#
# Usage:
#   ./create-netbsd-disk.sh [options]
#
# Options:
#   -s SIZE    Disk image size (default: 2G)
#              Accepts: 500M, 1G, 2G, 100G, 1T, 4T, etc.
#              Minimum: 500M, Maximum: 4T
#   -m FILE    NetBSD miniroot image (e.g., miniroot.fs)
#              If omitted, creates a blank disk with empty sd0b.
#   -o FILE    Output disk image file (default: disk.img)
#   -h         Show this help message
#
# Examples:
#   ./create-netbsd-disk.sh -s 2G -m miniroot.fs -o netbsd-disk.img
#   ./create-netbsd-disk.sh -s 4G -o blank-disk.img
#
# Disk geometry (matches emulator SCSI controller):
#   64 heads, 32 sectors/track, 512 bytes/sector
#   1 cylinder = 1 MB
#
# Partition layout:
#   sd0a: root filesystem (empty, for NetBSD installation)
#   sd0b: swap / miniroot (at end of disk)
#   sd0c: entire disk (raw)
#
# The miniroot contains the NetBSD installer (sysinst).
# Download from: https://cdn.netbsd.org/pub/NetBSD/NetBSD-10.1/mvme68k/installation/miniroot/

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Defaults
SIZE="2G"
MINIROOT=""
OUTPUT="disk.img"

# SCSI geometry (must match emulator)
HEADS=64
SECTORS=32
SECSIZE=512
SECPERCYL=$((HEADS * SECTORS))  # 2048
BYTESPERCYL=$((SECPERCYL * SECSIZE))  # 1048576 = 1 MB

# Miniroot partition size (default 32 MB if no miniroot provided)
DEFAULT_MINIROOT_MB=32

usage() {
    sed -n '2,/^$/s/^# \?//p' "$0"
    exit "${1:-0}"
}

# Parse size string (e.g., "500M", "2G", "4T") to bytes
parse_size() {
    local input="$1"
    local num unit

    num=$(echo "$input" | sed 's/[^0-9]//g')
    unit=$(echo "$input" | sed 's/[0-9]//g' | tr '[:lower:]' '[:upper:]')

    case "$unit" in
        M|MB) echo $((num * 1024 * 1024)) ;;
        G|GB) echo $((num * 1024 * 1024 * 1024)) ;;
        T|TB) echo $((num * 1024 * 1024 * 1024 * 1024)) ;;
        *)    echo "Error: Unknown size unit '$unit'. Use M, G, or T." >&2; exit 1 ;;
    esac
}

# Parse arguments
while getopts "s:m:o:h" opt; do
    case $opt in
        s) SIZE="$OPTARG" ;;
        m) MINIROOT="$OPTARG" ;;
        o) OUTPUT="$OPTARG" ;;
        h) usage 0 ;;
        *) usage 1 ;;
    esac
done

# Auto-detect miniroot if not specified
if [ -z "$MINIROOT" ]; then
    OUTPUT_DIR="$(cd "$(dirname "$OUTPUT")" 2>/dev/null && pwd)" || OUTPUT_DIR="$(pwd)"
    for candidate in \
        "$OUTPUT_DIR/miniroot.fs" \
        "$OUTPUT_DIR/miniroot.fs.gz" \
        "$SCRIPT_DIR/miniroot.fs" \
        "$SCRIPT_DIR/miniroot.fs.gz"; do
        if [ -f "$candidate" ]; then
            MINIROOT="$candidate"
            echo "Auto-detected miniroot: $MINIROOT"
            break
        fi
    done
fi

# Decompress miniroot.fs.gz if needed
if [ -n "$MINIROOT" ] && echo "$MINIROOT" | grep -q '\.gz$'; then
    MINIROOT_DECOMPRESSED="${MINIROOT%.gz}"
    echo "Decompressing $MINIROOT ..."
    gzip -dkf "$MINIROOT"
    MINIROOT="$MINIROOT_DECOMPRESSED"
fi

# Parse and validate size
SIZE_BYTES=$(parse_size "$SIZE")
MIN_BYTES=$((500 * 1024 * 1024))            # 500 MB
MAX_BYTES=$((4 * 1024 * 1024 * 1024 * 1024)) # 4 TB

if [ "$SIZE_BYTES" -lt "$MIN_BYTES" ]; then
    echo "Error: Disk size must be at least 500M (got $SIZE)" >&2
    exit 1
fi

if [ "$SIZE_BYTES" -gt "$MAX_BYTES" ]; then
    echo "Error: Disk size must be at most 4T (got $SIZE)" >&2
    exit 1
fi

# Align to cylinder boundary
TOTAL_SECTORS=$((SIZE_BYTES / SECSIZE))
TOTAL_CYL=$((TOTAL_SECTORS / SECPERCYL))
TOTAL_SECTORS=$((TOTAL_CYL * SECPERCYL))
SIZE_BYTES=$((TOTAL_SECTORS * SECSIZE))
SIZE_MB=$((SIZE_BYTES / 1024 / 1024))

# Determine miniroot partition size
if [ -n "$MINIROOT" ]; then
    if [ ! -f "$MINIROOT" ]; then
        echo "Error: Miniroot file not found: $MINIROOT" >&2
        exit 1
    fi
    MINIROOT_BYTES=$(stat -c%s "$MINIROOT" 2>/dev/null || stat -f%z "$MINIROOT")
    # Round up to cylinder boundary
    MINIROOT_CYL=$(( (MINIROOT_BYTES + BYTESPERCYL - 1) / BYTESPERCYL ))
else
    MINIROOT_BYTES=0
    MINIROOT_CYL=$DEFAULT_MINIROOT_MB
fi

MINIROOT_SECTORS=$((MINIROOT_CYL * SECPERCYL))

# Validate: miniroot + 1 cylinder (reserved) + at least 1 cylinder for root
if [ $((MINIROOT_CYL + 2)) -gt "$TOTAL_CYL" ]; then
    echo "Error: Disk too small for miniroot ($MINIROOT_CYL MB miniroot, $TOTAL_CYL MB disk)" >&2
    exit 1
fi

# Partition layout (VID format: sd0a starts at sector 0)
SWAP_SECTORS=$MINIROOT_SECTORS
A_SECTORS=$((TOTAL_SECTORS - SWAP_SECTORS))
B_OFFSET=$A_SECTORS

echo "=== Em68030 NetBSD Disk Image Creator ==="
echo "Image size: ${SIZE_MB} MB ($TOTAL_CYL cylinders, $TOTAL_SECTORS sectors)"
echo "Geometry:   $HEADS heads, $SECTORS sectors/track, $SECSIZE bytes/sector"
echo "sd0a:       sector 0 - $((A_SECTORS - 1)) ($((A_SECTORS / 2048)) MB, root)"
echo "sd0b:       sector $B_OFFSET - $((TOTAL_SECTORS - 1)) ($MINIROOT_CYL MB, swap/miniroot)"
if [ -n "$MINIROOT" ]; then
    echo "Miniroot:   $MINIROOT ($(( MINIROOT_BYTES / 1024 / 1024 )) MB)"
fi
echo "Output:     $OUTPUT"
echo ""

# Compile mkdisklabel
MKDISKLABEL="$SCRIPT_DIR/mkdisklabel"
if [ ! -x "$MKDISKLABEL" ] || [ "$SCRIPT_DIR/mkdisklabel.c" -nt "$MKDISKLABEL" ]; then
    echo "Compiling mkdisklabel..."
    ${CC:-gcc} -o "$MKDISKLABEL" "$SCRIPT_DIR/mkdisklabel.c"
fi

# Create disk image (sparse file)
echo "Creating disk image ($SIZE_MB MB)..."
dd if=/dev/zero of="$OUTPUT" bs=1 count=0 seek="$SIZE_BYTES" 2>/dev/null

# Write disklabel
echo "Writing disklabel..."
"$MKDISKLABEL" create "$OUTPUT" "$TOTAL_SECTORS" "$SWAP_SECTORS"

# Copy miniroot to sd0b
if [ -n "$MINIROOT" ]; then
    echo "Copying miniroot to sd0b (sector $B_OFFSET)..."
    dd if="$MINIROOT" of="$OUTPUT" bs=512 seek="$B_OFFSET" conv=notrunc 2>/dev/null
    echo "Miniroot installed."
fi

echo ""
echo "=== Done ==="
echo "Disk image: $OUTPUT"
echo "Load in emulator via: Settings > SCSI Disk 0"
