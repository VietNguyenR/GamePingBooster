<#
.SYNOPSIS
    Build relayd and ship it to the VPS. Run this from the Windows dev machine - the
    server never needs Go installed.

.DESCRIPTION
    PowerShell equivalent of `make deploy`, so GNU make is not required on Windows.
    Go cross-compiles a static Linux binary, so the server only receives one executable.

.PARAMETER RemoteHost
    Target in user@ip form, for example root@180.210.220.9.

.PARAMETER Arch
    amd64 (default) or arm64 for ARM VPSes (Oracle Ampere, AWS Graviton).

.PARAMETER PackageOnly
    Only produce gpb-relay.tar.gz for manual upload; do not use ssh/scp.

.EXAMPLE
    .\deploy.ps1 -RemoteHost root@180.210.220.9

.EXAMPLE
    .\deploy.ps1 -PackageOnly
#>

[CmdletBinding()]
param(
    [string]$RemoteHost,
    [ValidateSet('amd64', 'arm64')][string]$Arch = 'amd64',
    [switch]$PackageOnly
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $PackageOnly -and -not $RemoteHost) {
    throw "Need -RemoteHost in user@ip form, or use -PackageOnly to just build a tarball."
}

# ------------------------------------------------------------------- build

Write-Host "==> Building relayd for linux/$Arch" -ForegroundColor Cyan
$env:CGO_ENABLED = '0'
$env:GOOS = 'linux'
$env:GOARCH = $Arch
go build -trimpath -ldflags='-s -w' -o relayd ./cmd/relayd
if ($LASTEXITCODE -ne 0) { throw "go build failed" }

$size = [math]::Round((Get-Item relayd).Length / 1MB, 1)
Write-Host "    relayd: $size MB (static binary, no runtime dependencies on the VPS)"

# ----------------------------------------------------------------- package

if ($PackageOnly) {
    Write-Host "==> Packaging gpb-relay.tar.gz" -ForegroundColor Cyan
    if (Test-Path dist) { Remove-Item dist -Recurse -Force }
    New-Item -ItemType Directory -Path dist\deploy -Force | Out-Null
    Copy-Item relayd dist\
    Copy-Item deploy\setup-nat.sh, deploy\install.sh, deploy\relayd.service dist\deploy\
    tar -czf gpb-relay.tar.gz -C dist .
    Remove-Item dist -Recurse -Force

    Write-Host ""
    Write-Host "Created gpb-relay.tar.gz. On the VPS run:" -ForegroundColor Green
    Write-Host "    mkdir -p /opt/gpb && tar -xzf gpb-relay.tar.gz -C /opt/gpb"
    Write-Host "    cd /opt/gpb/deploy && chmod +x *.sh && ./install.sh"
    return
}

# ------------------------------------------------------------------ deploy

foreach ($tool in 'ssh', 'scp') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "$tool not found. Install the OpenSSH Client: Settings > System > Optional features. Or use -PackageOnly."
    }
}

Write-Host "==> Uploading to $RemoteHost" -ForegroundColor Cyan
ssh $RemoteHost "mkdir -p /opt/gpb/deploy"
if ($LASTEXITCODE -ne 0) { throw "Could not ssh to $RemoteHost" }

scp relayd "${RemoteHost}:/opt/gpb/relayd"
scp deploy\setup-nat.sh deploy\install.sh deploy\relayd.service "${RemoteHost}:/opt/gpb/deploy/"
if ($LASTEXITCODE -ne 0) { throw "scp failed" }

Write-Host "==> Running install.sh on the VPS" -ForegroundColor Cyan
# Inline dos2unix: Git on Windows may have rewritten line endings to CRLF, which bash cannot run.
ssh $RemoteHost "cd /opt/gpb/deploy && sed -i 's/\r$//' *.sh && chmod +x *.sh && ./install.sh"
if ($LASTEXITCODE -ne 0) { throw "install.sh failed - see the log above" }

Write-Host ""
Write-Host "Done. Copy the endpoint and PSK printed above into client/config.json." -ForegroundColor Green
Write-Host "Follow the relay log:  ssh $RemoteHost 'journalctl -u relayd -f'"
