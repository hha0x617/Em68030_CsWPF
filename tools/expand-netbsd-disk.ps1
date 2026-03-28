# Expand an existing NetBSD disk image for the Em68030 emulator.
#
# Extends the image file, rewrites the disklabel with new partition layout,
# and prints instructions for resizing the filesystem inside NetBSD.
#
# All file operations are performed inside Docker to avoid Windows/Docker
# file sync issues that can corrupt data.
#
# Usage:
#   .\expand-netbsd-disk.ps1 [-Size 2G] [-SwapMB 32] IMAGE
#
# If -Size is omitted or equal to current size, only the disklabel is rewritten.
#
# Examples:
#   .\expand-netbsd-disk.ps1 disk.img -Size 2G       # expand + relabel
#   .\expand-netbsd-disk.ps1 disk.img                 # relabel only
#   .\expand-netbsd-disk.ps1 -Size 4G -SwapMB 64 disk.img

param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$Image,
    [string]$Size,
    [int]$SwapMB = 32
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# SCSI geometry
$SecPerCyl = 64 * 32  # 2048
$SecSize = 512

function Parse-Size([string]$sizeStr) {
    if ($sizeStr -match '^(\d+)\s*(M|MB|G|GB|T|TB)$') {
        $num = [uint64]$Matches[1]
        $unit = $Matches[2].ToUpper()
        switch -Wildcard ($unit) {
            "M*" { return $num * 1024 * 1024 }
            "G*" { return $num * 1024 * 1024 * 1024 }
            "T*" { return $num * 1024 * 1024 * 1024 * 1024 }
        }
    }
    Write-Error "Invalid size: $sizeStr"
    exit 1
}

if (-not (Test-Path $Image)) {
    Write-Error "Image file not found: $Image"
    exit 1
}

$ImageFull = (Resolve-Path $Image).Path
$CurBytes = (Get-Item $ImageFull).Length
$CurMB = [Math]::Floor($CurBytes / 1MB)

# Determine mode: expand or label-only
$LabelOnly = $false
if ($Size) {
    $NewBytes = Parse-Size $Size
    $MinBytes = [uint64](500 * 1024 * 1024)
    $MaxBytes = [uint64](4) * 1024 * 1024 * 1024 * 1024
    if ($NewBytes -lt $MinBytes) { Write-Error "Size must be at least 500M"; exit 1 }
    if ($NewBytes -gt $MaxBytes) { Write-Error "Size must be at most 4T"; exit 1 }
    if ($NewBytes -le $CurBytes) {
        if ($NewBytes -eq $CurBytes) {
            $LabelOnly = $true
            $NewBytes = $CurBytes
        } else {
            Write-Error "New size must be >= current size ($CurMB MB)"
            exit 1
        }
    }
} else {
    $LabelOnly = $true
    $NewBytes = $CurBytes
}

# Align to cylinder boundary
$TotalSectors = [uint64]($NewBytes / $SecSize)
$TotalCyl = [uint64]($TotalSectors / $SecPerCyl)
$TotalSectors = $TotalCyl * $SecPerCyl
$NewBytes = $TotalSectors * $SecSize
$NewMB = $NewBytes / 1MB

$SwapSectors = $SwapMB * $SecPerCyl
$ASectors = $TotalSectors - $SwapSectors

Write-Host "=== Em68030 NetBSD Disk Image Expander ==="
Write-Host "Image:      $Image"
Write-Host "Current:    $CurMB MB"
if ($LabelOnly) {
    Write-Host "Mode:       Rewrite disklabel only (no size change)"
} else {
    Write-Host "New size:   $NewMB MB ($TotalCyl cylinders)"
}
Write-Host "sd0a:       $($ASectors / $SecPerCyl) MB (root)"
Write-Host "sd0b:       $SwapMB MB (swap)"
Write-Host ""

# Build Docker image
$DockerImage = "em68030-disktools"
$DockerDir = Join-Path $ScriptDir "docker-disktools"
if (-not (Test-Path $DockerDir)) { New-Item -ItemType Directory -Path $DockerDir | Out-Null }
@"
FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y gcc libc6-dev && rm -rf /var/lib/apt/lists/*
WORKDIR /work
"@ | Set-Content -Path (Join-Path $DockerDir "Dockerfile") -Encoding UTF8

docker build -t $DockerImage $DockerDir 2>&1 | Out-Null

$ImageDir = Split-Path $ImageFull
$ImageName = Split-Path -Leaf $ImageFull

# All file operations inside Docker to avoid Windows/Docker sync issues
if ($LabelOnly) {
    Write-Host "Rewriting disklabel..."
    $DockerScript = @"
set -euo pipefail
gcc -o /tmp/mkdisklabel /work/tools/mkdisklabel.c
/tmp/mkdisklabel update /work/data/$ImageName $TotalSectors $SwapSectors
"@
} else {
    Write-Host "Extending image and rewriting disklabel..."
    $DockerScript = @"
set -euo pipefail
echo '[1/2] Extending image to $NewMB MB...'
truncate -s $NewBytes /work/data/$ImageName
echo '  Image extended (existing data preserved).'
echo '[2/2] Rewriting disklabel...'
gcc -o /tmp/mkdisklabel /work/tools/mkdisklabel.c
/tmp/mkdisklabel update /work/data/$ImageName $TotalSectors $SwapSectors
"@
}

docker run --rm `
    -v "${ScriptDir}:/work/tools:ro" `
    -v "${ImageDir}:/work/data" `
    $DockerImage `
    bash -c $DockerScript

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed"
    exit 1
}

# Cleanup
Remove-Item -Recurse -Force $DockerDir -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "=== Done ==="
Write-Host ""
Write-Host "Next step: boot NetBSD and resize the root filesystem:"
Write-Host ""
Write-Host "  # resize_ffs /dev/sd0a"
