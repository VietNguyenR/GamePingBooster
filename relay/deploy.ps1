<#
.SYNOPSIS
    Build relayd and ship it to the VPS. Run this from the Windows dev machine - the
    server never needs Go installed.

.DESCRIPTION
    PowerShell equivalent of `make deploy`, so GNU make is not required on Windows.
    Go cross-compiles a static Linux binary, so the server only receives one executable.

.PARAMETER RemoteHost
    Which relay. Normally a name declared in gpb.conf, for example sg, in which case the host,
    port, user and key or password all come from that file. A name gpb.conf does not declare is
    handed to ssh unchanged, so an alias from your ~/.ssh/config or a plain root@203.0.113.10
    works too. Omit it entirely to use RELAY_DEFAULT.

.PARAMETER Arch
    amd64 (default) or arm64 for ARM VPSes (Oracle Ampere, AWS Graviton).

.PARAMETER PackageOnly
    Only produce gpb-relay.tar.gz for manual upload; do not use ssh/scp.

.EXAMPLE
    .\deploy.ps1 -RemoteHost sg

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

$repoRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $repoRoot 'tools\GpbConf.ps1')

# --------------------------------------------------------------- resolve the relay first
#
# Before the build, not after: a name that resolves to nothing should cost a second, not a
# cross-compile.

$relay = $null
if (-not $PackageOnly) {
    $relay = Get-GpbRelay -Name $RemoteHost -RepoRoot $repoRoot
    if (-not $relay) {
        $known = Get-GpbRelayNames -RepoRoot $repoRoot
        $msg = "Which relay? Pass -RemoteHost <name>, or set RELAY_DEFAULT in gpb.conf."
        if ($known) {
            $msg += "`nDeclared in gpb.conf: $($known -join ', ')"
        } else {
            $msg += "`ngpb.conf declares none yet - copy gpb.conf.example to gpb.conf and fill in one block."
        }
        $msg += "`nOr use -PackageOnly to just build a tarball."
        throw $msg
    }
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

foreach ($tool in 'ssh', 'tar') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "$tool not found. OpenSSH and tar both ship with Windows 10 and later - see Settings > System > Optional features. Or use -PackageOnly."
    }
}

# Everything goes over ONE ssh connection, as a tar stream on stdin.
#
# The earlier version opened four: ssh to mkdir, scp the binary, scp the scripts, ssh to install.
# With a key that costs nothing and nobody notices. With password authentication it is four
# password prompts for a single deploy, which is why deploying by hand felt easier than using
# this script. One connection means exactly one prompt, whichever authentication the server uses.
#
# The tar is written to a temp file first rather than piped: PowerShell 5.1 passes pipeline data
# between native commands as decoded text, which corrupts a gzip stream, and it has no input
# redirection operator to feed the file to ssh directly. cmd handles both without complaint.
# Enable-GpbAskpass, in tools\GpbConf.ps1, explains how the password reaches ssh.
$askpass = $null
$sshArgs = ConvertTo-GpbCmdArgs $relay.SshArgs

$payload = [System.IO.Path]::GetTempFileName()
try {
    $askpass = Enable-GpbAskpass -Password $relay.Password
    if ($askpass) { Write-Host "==> Using the password from gpb.conf" -ForegroundColor DarkGray }

    Write-Host "==> Packing" -ForegroundColor Cyan
    & tar -czf $payload -C . relayd deploy/setup-nat.sh deploy/install.sh deploy/relayd.service
    if ($LASTEXITCODE -ne 0) { throw "tar failed" }

    # The sed strips CR: these files are edited on Windows, and a shell script with CRLF endings
    # fails on Linux with an error that names the wrong thing entirely. .gitattributes should keep
    # them LF now, so this is a second line of defence rather than the thing making it work.
    $remote = "set -e; mkdir -p /opt/gpb; tar -xzf - -C /opt/gpb; cd /opt/gpb/deploy; sed -i 's/\r`$//' *.sh; chmod +x *.sh; ./install.sh"

    Write-Host "==> Deploying to $($relay.Name) at $($relay.Target) (one connection)" -ForegroundColor Cyan
    cmd /c "ssh $sshArgs `"$remote`" < `"$payload`""
    if ($LASTEXITCODE -ne 0) { throw "deploy failed - see the output above" }
} finally {
    Remove-Item $payload -Force -ErrorAction SilentlyContinue
    Disable-GpbAskpass -Helper $askpass
}

# The two values install.sh just printed go to two different places, and putting the endpoint in
# gpb.conf - the file this deploy was configured from - is the mistake worth heading off.
Write-Host ""
Write-Host "Done." -ForegroundColor Green
if ($relay.Endpoint) {
    Write-Host "  The endpoint goes into a profile, not into gpb.conf:"
    Write-Host "    `"relays`": [ { `"id`": `"$($relay.Name)`", `"name`": `"$($relay.Name)`", `"endpoint`": `"$($relay.Endpoint)`" } ]"
    Write-Host "  The PSK goes into client\config.json, with `"defaultRelayId`": `"$($relay.Name)`"."
} else {
    Write-Host "  Copy the endpoint into a profile's `"relays`" list, and the PSK into client\config.json."
}
Write-Host "Follow the relay log:  .\gpb.ps1 relay logs $($relay.Name)"
