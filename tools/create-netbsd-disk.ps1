# Create a NetBSD disk image for the Em68030 emulator.
#
# Creates a raw SCSI disk image with a NetBSD disklabel.
# If a miniroot image is provided, it is placed on sd0b for installation.
#
# Usage:
#   .\create-netbsd-disk.ps1 [-Size 2G] [-Miniroot miniroot.fs] [-Output disk.img]
#
# Examples:
#   .\create-netbsd-disk.ps1 -Size 2G -Miniroot miniroot.fs -Output netbsd-disk.img
#   .\create-netbsd-disk.ps1 -Size 4G -Output blank-disk.img

param(
    [string]$Size = "2G",
    [string]$Miniroot,
    [string]$Output = "disk.img"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# SCSI geometry (must match emulator)
$Heads = 64
$Sectors = 32
$SecSize = 512
$SecPerCyl = $Heads * $Sectors  # 2048
$BytesPerCyl = $SecPerCyl * $SecSize  # 1048576 = 1 MB
$DefaultMinirootMB = 32

function Parse-Size([string]$sizeStr) {
    if ($sizeStr -match '^(\d+(\.\d+)?)\s*(M|MB|G|GB|T|TB)$') {
        $num = [double]$Matches[1]
        $unit = $Matches[3].ToUpper()
        switch -Wildcard ($unit) {
            "M*" { return [uint64]($num * 1024 * 1024) }
            "G*" { return [uint64]($num * 1024 * 1024 * 1024) }
            "T*" { return [uint64]($num * 1024 * 1024 * 1024 * 1024) }
        }
    }
    Write-Error "Invalid size format: $sizeStr (use e.g., 500M, 2G, 4T)"
    exit 1
}

# Parse and validate size
$SizeBytes = Parse-Size $Size
$MinBytes = [uint64](500 * 1024 * 1024)
$MaxBytes = [uint64](4) * 1024 * 1024 * 1024 * 1024

if ($SizeBytes -lt $MinBytes) {
    Write-Error "Disk size must be at least 500M (got $Size)"
    exit 1
}
if ($SizeBytes -gt $MaxBytes) {
    Write-Error "Disk size must be at most 4T (got $Size)"
    exit 1
}

# Align to cylinder boundary
$TotalSectors = [uint64]($SizeBytes / $SecSize)
$TotalCyl = [uint64]($TotalSectors / $SecPerCyl)
$TotalSectors = $TotalCyl * $SecPerCyl
$SizeBytes = $TotalSectors * $SecSize
$SizeMB = $SizeBytes / 1024 / 1024

# Determine miniroot partition size
if ($Miniroot) {
    if (-not (Test-Path $Miniroot)) {
        Write-Error "Miniroot file not found: $Miniroot"
        exit 1
    }
    $MinirootBytes = (Get-Item $Miniroot).Length
    $MinirootCyl = [Math]::Ceiling($MinirootBytes / $BytesPerCyl)
} else {
    $MinirootBytes = 0
    $MinirootCyl = $DefaultMinirootMB
}

$MinirootSectors = $MinirootCyl * $SecPerCyl

if (($MinirootCyl + 2) -gt $TotalCyl) {
    Write-Error "Disk too small for miniroot ($MinirootCyl MB miniroot, $TotalCyl MB disk)"
    exit 1
}

# Partition layout
$SwapSectors = $MinirootSectors
$ASectors = $TotalSectors - $SwapSectors
$BOffset = $ASectors

Write-Host "=== Em68030 NetBSD Disk Image Creator ==="
Write-Host "Image size: $SizeMB MB ($TotalCyl cylinders, $TotalSectors sectors)"
Write-Host "Geometry:   $Heads heads, $Sectors sectors/track, $SecSize bytes/sector"
Write-Host "sd0a:       sector 0 - $($ASectors - 1) ($($ASectors / $SecPerCyl) MB, root)"
Write-Host "sd0b:       sector $BOffset - $($TotalSectors - 1) ($MinirootCyl MB, swap/miniroot)"
if ($Miniroot) {
    Write-Host "Miniroot:   $Miniroot ($([Math]::Round($MinirootBytes / 1MB)) MB)"
}
Write-Host "Output:     $Output"
Write-Host ""

# Build and run via Docker
$DockerImage = "em68030-disktools"

# Create a minimal Dockerfile if needed
$DockerDir = Join-Path $ScriptDir "docker-disktools"
if (-not (Test-Path $DockerDir)) {
    New-Item -ItemType Directory -Path $DockerDir | Out-Null
}
$Dockerfile = Join-Path $DockerDir "Dockerfile"
@"
FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y gcc libc6-dev && rm -rf /var/lib/apt/lists/*
WORKDIR /work
"@ | Set-Content -Path $Dockerfile -Encoding UTF8

Write-Host "Building Docker image..."
docker build -t $DockerImage $DockerDir 2>&1 | Out-Null

# Prepare output directory
$OutputDir = Split-Path -Parent (Resolve-Path -Path $Output -ErrorAction SilentlyContinue ?? $Output)
if (-not $OutputDir) { $OutputDir = "." }
$OutputDir = (Resolve-Path $OutputDir).Path
$OutputName = Split-Path -Leaf $Output

# Build volume mounts
$VolArgs = @(
    "-v", "${ScriptDir}:/work/tools:ro",
    "-v", "${OutputDir}:/work/output"
)
if ($Miniroot) {
    $MinirootFull = (Resolve-Path $Miniroot).Path
    $VolArgs += @("-v", "${MinirootFull}:/work/miniroot.fs:ro")
}

$MountMiniroot = ""
if ($Miniroot) { $MountMiniroot = "/work/miniroot.fs" }

Write-Host "Creating disk image..."
docker run --rm @VolArgs $DockerImage bash -c @"
set -euo pipefail
cc -o /tmp/mkdisklabel /work/tools/mkdisklabel.c
dd if=/dev/zero of=/work/output/$OutputName bs=1 count=0 seek=$SizeBytes 2>/dev/null
/tmp/mkdisklabel create /work/output/$OutputName $TotalSectors $SwapSectors
if [ -n '$MountMiniroot' ]; then
    dd if=$MountMiniroot of=/work/output/$OutputName bs=512 seek=$BOffset conv=notrunc 2>/dev/null
    echo 'Miniroot installed.'
fi
"@

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create disk image"
    exit 1
}

Write-Host ""
Write-Host "=== Done ==="
Write-Host "Disk image: $Output"
Write-Host "Load in emulator via: Settings > SCSI Disk 0"
