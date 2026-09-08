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
    .\deploy.ps1 -RemoteHost sg -PackageOnly
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
# PackageOnly without a name remains a generic PSK package for console installs. Supplying a
# name makes it a faithful dry run of that relay's deploy, including a token relay's public key.
if (-not $PackageOnly -or $RemoteHost) {
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
# The version is stamped in so a relay can report which build it is running; it is only ever
# displayed, in the startup log and on the admin dashboard.
$version = 'dev'
$versionFile = Join-Path $repoRoot 'VERSION'
if (Test-Path $versionFile) { $version = (Get-Content $versionFile -Raw).Trim() }
go build -trimpath -ldflags "-s -w -X main.version=$version" -o relayd ./cmd/relayd
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
    if ($relay -and $relay.Mode -eq 'token') {
        Copy-Item -LiteralPath $relay.LicenceKey -Destination dist\licence.pub
    }
    tar -czf gpb-relay.tar.gz -C dist .
    Remove-Item dist -Recurse -Force

    Write-Host ""
    Write-Host "Created gpb-relay.tar.gz. On the VPS run:" -ForegroundColor Green
    Write-Host "    mkdir -p /opt/gpb && tar -xzf gpb-relay.tar.gz -C /opt/gpb"
    # The mode is named here for the same reason the deploy names it: a package built for a
    # relay declared psk, unpacked on a box that is currently running as token, installs as
    # token unless the command says otherwise. A package built for no particular relay names no
    # mode, because there is no declaration to go on and install.sh's own rule is the right one.
    if ($relay -and $relay.Mode -eq 'token') {
        Write-Host "    cd /opt/gpb/deploy && chmod +x *.sh && ./install.sh --licence-key ../licence.pub"
    } elseif ($relay) {
        Write-Host "    cd /opt/gpb/deploy && chmod +x *.sh && ./install.sh --psk"
    } else {
        Write-Host "    cd /opt/gpb/deploy && chmod +x *.sh && ./install.sh"
    }
    return
}

# ------------------------------------------------------------------ deploy

<#
.SYNOPSIS
    Run install.sh under sudo on a second connection.

.DESCRIPTION
    Reached only when the tarball is already unpacked on the far end and all that is left is to
    run install.sh as root. It has to be a second connection because sudo -S takes its password
    from stdin, and on the first connection stdin was the tarball.

    -p '' suppresses sudo's own prompt, which would otherwise appear in the output as a stray
    'Password:' with nothing typed after it.
#>
function Invoke-RemoteInstall {
    param($Relay)

    $sshArgv = $Relay.SshArgs

    $maxArg = Get-RelayInstallArgs $Relay

    if (-not $Relay.SudoPassword) {
        Write-Host "==> sudo needs a password on $($Relay.Name). Type it when it asks." -ForegroundColor Cyan
        # -t so sudo has a terminal to prompt on. Safe here and not on the first connection: a
        # pty translates newlines, which would have corrupted the gzip stream.
        & ssh -t @sshArgv "cd ~/.gpb-deploy/deploy && sudo ./install.sh $maxArg"
        if ($LASTEXITCODE -ne 0) { throw "the install step failed - see the output above" }
        return
    }

    Write-Host "==> sudo needs a password on $($Relay.Name); using the one from gpb.conf" -ForegroundColor Cyan

    # Not `$password | & ssh ...`: in PowerShell 5.1 that appends CRLF, which sudo keeps as part
    # of the password, and it replaces every non-ASCII byte with '?'. Both were measured.
    # The password never touches disk either way.
    $code = Invoke-GpbSshWithStdin -SshArgs $sshArgv `
        -RemoteCommand "cd ~/.gpb-deploy/deploy && sudo -S -p '' ./install.sh $maxArg" `
        -StdinLine $Relay.SudoPassword

    if ($code -ne 0) {
        throw "the install step failed. If sudo rejected the password, set RELAY_<NAME>_SUDO_PASSWORD in gpb.conf."
    }
}

function Get-RelayInstallArgs {
    param($Relay)

    # These are arguments rather than environment variables because sudo resets the environment.
    # The public key is always called licence.pub in the payload, so neither its local path nor a
    # Windows path can leak into the remote command line. The private licence key is never read.
    $args = "--max-clients $($Relay.MaxClients)"
    if ($Relay.Mode -eq 'token') {
        $args += ' --licence-key ../licence.pub'
    } else {
        # Said out loud, not left implicit, and this is the line that decides whether a relay can
        # be moved back off token mode at all.
        #
        # install.sh keeps a relay that already has /etc/gpb/licence.pub in TOKEN mode when no
        # mode is named - a rule meant for a deploy typed by hand, which does not know licensed
        # mode exists. This deploy is not that: it read the mode out of gpb.conf, so it has to
        # state it. Sending nothing let the state of the far end decide instead, and a relay
        # declared psk that happened to be running as token silently stayed token, unit and all.
        $args += ' --psk'
    }
    if ($Relay.ReportUrl) { $args += " --report-url $($Relay.ReportUrl)" }
    return $args
}

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
$packageDir = $null
try {
    $askpass = Enable-GpbAskpass -Password $relay.Password
    if ($askpass) { Write-Host "==> Using the password from gpb.conf" -ForegroundColor DarkGray }

    Write-Host "==> Packing" -ForegroundColor Cyan
    # A short-lived staging directory gives the remote side one stable public-key path, without
    # leaving a key beside the build artefacts or exposing a Windows path to the remote shell.
    $packageDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gpb-relay-" + [guid]::NewGuid())
    New-Item -ItemType Directory -Path (Join-Path $packageDir 'deploy') -Force | Out-Null
    Copy-Item relayd $packageDir
    Copy-Item deploy\setup-nat.sh, deploy\install.sh, deploy\relayd.service (Join-Path $packageDir 'deploy')
    if ($relay.Mode -eq 'token') {
        Copy-Item -LiteralPath $relay.LicenceKey -Destination (Join-Path $packageDir 'licence.pub')
    }
    & tar -czf $payload -C $packageDir .
    if ($LASTEXITCODE -ne 0) { throw "tar failed" }

    # What runs on the far end. Duplicated from ./gpb - keep the two in step.
    #
    # The staging directory is under ~ rather than /opt, because an unprivileged account cannot
    # create /opt/gpb - that is the "mkdir: Permission denied" a non-root deploy used to die on.
    # Nothing is installed from there: install.sh finds its own directory and copies to absolute
    # paths, so where it is unpacked does not matter.
    #
    # Exit 90 means "not root, and sudo wants a password". It cannot be handled on this
    # connection: stdin is the tarball, and sudo -S reads its password from stdin. Exit 91 means
    # there is no sudo at all.
    #
    # The string is single-quoted and holds no double quotes of its own: it travels through cmd
    # inside a double-quoted argument, and a quote here would end that argument early. The sed
    # strips CR, because a shell script with CRLF endings fails on Linux with an error that names
    # the wrong thing entirely.
    #
    # The sudo check is written the long way round, with an empty then-branch, rather than as
    # `if ! command -v sudo`. A '!' is one delayed-expansion setting away from being eaten
    # somewhere on the trip through cmd, and losing it would invert the test: every host that HAS
    # sudo would be told it has none. The long form cannot fail that way.
    # $m is concatenated rather than interpolated: the literal above is single-quoted so that
    # it can hold no double quotes of its own, and that property is what keeps it intact on the
    # trip through cmd. Interpolating would mean a double-quoted string and a quoting problem.
    $m = ' ' + (Get-RelayInstallArgs $relay)
    $remote = 'set -e; mkdir -p ~/.gpb-deploy; tar -xzf - -C ~/.gpb-deploy; cd ~/.gpb-deploy/deploy; sed -i ''s/\r$//'' *.sh; chmod +x *.sh; if [ $(id -u) -eq 0 ]; then ./install.sh' + $m + '; exit; fi; if command -v sudo >/dev/null 2>&1; then :; else exit 91; fi; if sudo -n true 2>/dev/null; then sudo -n ./install.sh' + $m + '; exit; fi; exit 90'

    $modeLabel = if ($relay.Mode -eq 'token') { 'token mode' } else { 'PSK mode' }
    if ([int]$relay.MaxClients -gt 0) {
        Write-Host "==> Deploying to $($relay.Name) at $($relay.Target) ($modeLabel, one connection), max $($relay.MaxClients) clients" -ForegroundColor Cyan
    } else {
        Write-Host "==> Deploying to $($relay.Name) at $($relay.Target) ($modeLabel, one connection), no client limit" -ForegroundColor Cyan
    }
    cmd /c "ssh $sshArgs `"$remote`" < `"$payload`""
    $staged = $LASTEXITCODE

    if ($staged -eq 91) {
        throw "the account on $($relay.Name) is not root and has no sudo. Deploy as root, or install sudo there."
    }
    if ($staged -eq 90) {
        Invoke-RemoteInstall $relay
    } elseif ($staged -ne 0) {
        throw "deploy failed - see the output above"
    }
} finally {
    Remove-Item $payload -Force -ErrorAction SilentlyContinue
    if ($packageDir) { Remove-Item $packageDir -Recurse -Force -ErrorAction SilentlyContinue }
    Disable-GpbAskpass -Helper $askpass
}

# The two values install.sh just printed go to two different places, and putting the endpoint in
# gpb.conf - the file this deploy was configured from - is the mistake worth heading off.
Write-Host ""
Write-Host "Done." -ForegroundColor Green
if ($relay.Endpoint) {
    Write-Host "  The endpoint goes into a profile, not into gpb.conf:"
    Write-Host "    `"relays`": [ { `"id`": `"$($relay.Name)`", `"name`": `"$($relay.Name)`", `"endpoint`": `"$($relay.Endpoint)`" } ]"
    if ($relay.Mode -eq 'token') {
        Write-Host "  This relay uses licence tokens; the profile entry also needs its relay public key."
    } else {
        Write-Host "  The PSK goes into client\config.json, with `"defaultRelayId`": `"$($relay.Name)`"."
    }
} else {
    if ($relay.Mode -eq 'token') {
        Write-Host "  Copy the endpoint and relay public key into a profile's `"relays`" list."
    } else {
        Write-Host "  Copy the endpoint into a profile's `"relays`" list, and the PSK into client\config.json."
    }
}
Write-Host "Follow the relay log:  .\gpb.ps1 relay logs $($relay.Name)"
