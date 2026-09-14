# Imports the official debian:12 image from Docker Hub as a WSL 2 distro.
#
# Usage: pwsh scripts/debian12/import-wsl.ps1 [-Name Debian12] [-InstallDir <dir>]

param(
    [string]$Name = "Debian12",
    [string]$InstallDir = "$env:LOCALAPPDATA\WSL\Debian12"
)

$ErrorActionPreference = "Stop"

function Get-Json($url, $headers) {
    $resp = Invoke-WebRequest -UseBasicParsing -Headers $headers $url
    $text = if ($resp.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($resp.Content) } else { $resp.Content }
    return $text | ConvertFrom-Json
}

if ((wsl.exe -l -q) -replace "`0", "" -contains $Name) {
    Write-Host "WSL distro '$Name' already exists."
    exit 0
}

$repo = "library/debian"
$tag = "12"

$token = (Invoke-RestMethod "https://auth.docker.io/token?service=registry.docker.io&scope=repository:${repo}:pull").token
$headers = @{
    Authorization = "Bearer $token"
    Accept = "application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json"
}

$index = Get-Json "https://registry-1.docker.io/v2/$repo/manifests/$tag" $headers
$amd64 = $index.manifests |
    Where-Object { $_.platform.os -eq "linux" -and $_.platform.architecture -eq "amd64" -and -not $_.platform.variant } |
    Select-Object -First 1
if (-not $amd64) { throw "No linux/amd64 manifest for debian:$tag." }

$headers.Accept = "application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json"
$manifest = Get-Json "https://registry-1.docker.io/v2/$repo/manifests/$($amd64.digest)" $headers
if ($manifest.layers.Count -ne 1) { throw "Expected a single rootfs layer." }
$layer = $manifest.layers[0]

$rootfs = Join-Path ([IO.Path]::GetTempPath()) "debian12-rootfs.tar.gz"
Write-Host "Downloading debian:$tag rootfs ($($layer.size) bytes)..."
Invoke-WebRequest -UseBasicParsing -Headers @{ Authorization = "Bearer $token" } `
    "https://registry-1.docker.io/v2/$repo/blobs/$($layer.digest)" -OutFile $rootfs

$sha = "sha256:" + (Get-FileHash $rootfs -Algorithm SHA256).Hash.ToLower()
if ($sha -ne $layer.digest) { throw "Digest mismatch: $sha != $($layer.digest)" }

New-Item -ItemType Directory -Force $InstallDir | Out-Null
wsl.exe --import $Name $InstallDir $rootfs --version 2
if ($LASTEXITCODE -ne 0) { throw "wsl --import failed." }
Remove-Item -LiteralPath $rootfs

Write-Host "Imported '$Name': Debian $(wsl.exe -d $Name --exec cat /etc/debian_version)"
