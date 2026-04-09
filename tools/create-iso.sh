#!/bin/bash
# Create an ISO image from a directory for the Em68030 emulator.
#
# Usage:
#   ./create-iso.sh [-o output.iso] DIRECTORY
#
# Examples:
#   ./create-iso.sh /tmp/files
#   ./create-iso.sh -o transfer.iso ~/data
#
# The ISO can be loaded via Settings > SCSI CD-ROM in the emulator,
# then mounted in the guest with: mount -t cd9660 /dev/cd0a /mnt

set -euo pipefail

OUTPUT=""

usage() {
    echo "Usage: $0 [-o output.iso] DIRECTORY"
    echo "  -o FILE    Output ISO file (default: <dirname>.iso)"
    exit "${1:-0}"
}

while getopts "o:h" opt; do
    case $opt in
        o) OUTPUT="$OPTARG" ;;
        h) usage 0 ;;
        *) usage 1 ;;
    esac
done
shift $((OPTIND - 1))

[ $# -ge 1 ] || usage 1
SRCDIR="$1"
[ -d "$SRCDIR" ] || { echo "Error: Directory not found: $SRCDIR" >&2; exit 1; }

if [ -z "$OUTPUT" ]; then
    OUTPUT="$(basename "$SRCDIR").iso"
fi

if ! command -v genisoimage >/dev/null 2>&1 && ! command -v mkisofs >/dev/null 2>&1; then
    echo "The following packages are required but not installed: genisoimage"
    printf "Install them now? [y/N] "
    read -r REPLY
    case "$REPLY" in
        [yY]|[yY][eE][sS]) ;;
        *) echo "Error: Required packages not installed: genisoimage" >&2; exit 1 ;;
    esac
    apt-get update -qq
    apt-get install -y -qq genisoimage \
        || { echo "Error: Failed to install genisoimage" >&2; exit 1; }
fi

if command -v genisoimage >/dev/null 2>&1; then
    genisoimage -o "$OUTPUT" -R -J "$SRCDIR"
else
    mkisofs -o "$OUTPUT" -R -J "$SRCDIR"
fi

echo "ISO created: $OUTPUT ($(du -h "$OUTPUT" | cut -f1))"
echo "Load in emulator: Settings > SCSI CD-ROM"
echo "Mount in guest:   mount -t cd9660 /dev/cd0a /mnt"
