# Create an ISO image from a directory for the Em68030 emulator.
#
# Usage:
#   .\create-iso.ps1 [-Output output.iso] DIRECTORY
#
# Examples:
#   .\create-iso.ps1 C:\temp\files
#   .\create-iso.ps1 -Output transfer.iso C:\data
#
# The ISO can be loaded via Settings > SCSI CD-ROM in the emulator,
# then mounted in the guest with: mount -t cd9660 /dev/cd0a /mnt
#
# Requires Docker.

param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$Directory,
    [string]$Output
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Directory -PathType Container)) {
    Write-Error "Directory not found: $Directory"
    exit 1
}

$DirFull = (Resolve-Path $Directory).Path

if (-not $Output) {
    $Output = (Split-Path -Leaf $DirFull) + ".iso"
}

$OutputDir = Split-Path (Join-Path (Get-Location) $Output)
if (-not $OutputDir) { $OutputDir = (Get-Location).Path }
$OutputDir = (Resolve-Path $OutputDir).Path
$OutputName = Split-Path -Leaf $Output

docker run --rm `
    -v "${DirFull}:/build/input:ro" `
    -v "${OutputDir}:/build/output" `
    debian:bookworm-slim `
    bash -c "apt-get update -qq > /dev/null 2>&1 && apt-get install -y -qq genisoimage > /dev/null 2>&1 && genisoimage -o /build/output/$OutputName -R -J /build/input/ 2>&1 | tail -1"

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create ISO"
    exit 1
}

$isoPath = Join-Path $OutputDir $OutputName
$size = [Math]::Round((Get-Item $isoPath).Length / 1MB, 1)
Write-Host "ISO created: $isoPath (${size} MB)"
Write-Host "Load in emulator: Settings > SCSI CD-ROM"
Write-Host "Mount in guest:   mount -t cd9660 /dev/cd0a /mnt"
