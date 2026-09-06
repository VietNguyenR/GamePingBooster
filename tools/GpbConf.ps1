<#
.SYNOPSIS
    Reads gpb.conf and turns a relay name into the ssh arguments that reach it.

.DESCRIPTION
    Dot-sourced by gpb.ps1 and relay\deploy.ps1 so there is one parser rather than two that
    drift. The POSIX half of this, in ./gpb, is deliberately the same rules:

      - plain KEY=value, one per line, value taken literally to the end of the line
      - a line starting with # is a comment; there are no inline comments, so a password may
        contain a space, a #, a $ or a backslash without any escaping
      - only RELAY_* keys are read; anything else in the file is ignored

    A name that gpb.conf does not declare is handed to ssh unchanged, so an alias from
    %USERPROFILE%\.ssh\config or a plain user@host still works.
#>

function Get-GpbConfPath {
    param([string]$RepoRoot)
    return (Join-Path $RepoRoot 'gpb.conf')
}

function Read-GpbConf {
    param([string]$Path)

    $map = @{}
    if (-not (Test-Path -LiteralPath $Path)) { return $map }

    foreach ($line in (Get-Content -LiteralPath $Path)) {
        # Get-Content splits on CRLF or LF, but a file that mixes them can still leave a CR here.
        $text = $line.TrimEnd([char]13)
        if ($text -match '^\s*#') { continue }

        # IndexOf, not -split: a value may itself contain '=' and must survive whole.
        $i = $text.IndexOf('=')
        if ($i -lt 1) { continue }

        $key = $text.Substring(0, $i).Trim()
        $value = $text.Substring($i + 1).Trim()

        # Anything else is a malformed line rather than a setting, and is skipped rather than
        # guessed at.
        if ($key -notmatch '^RELAY_[A-Za-z0-9_]*$') { continue }
        $map[$key.ToUpperInvariant()] = $value
    }
    return $map
}

function Get-GpbRelayNames {
    param([string]$RepoRoot)

    $conf = Read-GpbConf (Get-GpbConfPath $RepoRoot)
    $names = @()
    foreach ($key in $conf.Keys) {
        if ($key -match '^RELAY_([A-Za-z0-9_]+)_HOST$') { $names += $Matches[1].ToLowerInvariant() }
    }
    return ($names | Sort-Object -Unique)
}

<#
.SYNOPSIS
    Everything needed to reach one relay, or $null when no name was given and there is no default.

.OUTPUTS
    Hashtable with Name, Declared, Target, Port, Key, Password, SudoPassword, Listen,
    Endpoint and SshArgs.
    SshArgs is ready to splat at ssh; Endpoint is the address a PLAYER connects to, which is a
    different port from the SSH one and belongs in a profile, not in gpb.conf.
#>
function Get-GpbRelay {
    param(
        [string]$Name,
        [string]$RepoRoot
    )

    $conf = Read-GpbConf (Get-GpbConfPath $RepoRoot)
    if (-not $Name) { $Name = $conf['RELAY_DEFAULT'] }
    if (-not $Name) { return $null }

    # PowerShell 5.1 has no null-coalescing operator, so every default below is an explicit if.
    $slug = ($Name.ToUpperInvariant() -replace '[^A-Z0-9]', '_')
    $hostName = $conf["RELAY_${slug}_HOST"]

    $password = $conf["RELAY_${slug}_PASSWORD"]
    if (-not $password) { $password = $conf['RELAY_PASSWORD'] }

    # sudo on the far end usually wants the same password used to log in - a VPS with password
    # authentication has one password, not two. The separate field is for the case where they
    # differ, most often key-based login plus a sudo password.
    $sudoPassword = $conf["RELAY_${slug}_SUDO_PASSWORD"]
    if (-not $sudoPassword) { $sudoPassword = $password }

    if (-not $hostName) {
        # Not declared here: an ssh alias, or user@host. Let ssh's own configuration answer for
        # the port, the user and the key.
        return @{
            Name         = $Name
            Declared     = $false
            Target       = $Name
            Port         = ''
            Key          = ''
            Password     = $password
            SudoPassword = $sudoPassword
            Listen       = ''
            Endpoint     = ''
            MaxClients   = '0'
            SshArgs      = @($Name)
        }
    }

    $user = $conf["RELAY_${slug}_USER"]
    if (-not $user) { $user = 'root' }
    $port = $conf["RELAY_${slug}_PORT"]
    if (-not $port) { $port = '22' }
    $listen = $conf["RELAY_${slug}_LISTEN"]
    if (-not $listen) { $listen = '51820' }

    # How many clients this relay accepts at once. Empty means 0, which relayd reads as "no cap
    # beyond the address pool" - the behaviour every relay had before the setting existed, so an
    # existing gpb.conf keeps working untouched.
    $max = $conf["RELAY_${slug}_MAX"]
    if (-not $max) { $max = '0' }
    if ($max -notmatch '^\d+$') {
        throw "RELAY_${slug}_MAX is '$max'. It must be a whole number - the count of clients this relay accepts at once, or 0 for no limit."
    }

    $key = $conf["RELAY_${slug}_KEY"]
    if ($key -and ($key.StartsWith('~/') -or $key.StartsWith('~\'))) {
        # ssh expands ~ itself, but only reliably on the Unix-style form. Expanding it here means
        # the same gpb.conf line works from PowerShell and from Git Bash.
        $key = Join-Path $env:USERPROFILE $key.Substring(2)
    }

    $target = "$user@$hostName"
    # Named sshArgs, not the obvious short name: that one is an automatic variable holding a
    # function's own unbound arguments, and shadowing it reads like a bug to the next person.
    $sshArgs = @('-p', $port)
    if ($key) {
        # IdentitiesOnly stops ssh offering every key the agent holds before the one that was
        # named - on a host with MaxAuthTries 3 and four keys loaded it never gets there.
        $sshArgs += @('-i', $key, '-o', 'IdentitiesOnly=yes')
    }
    $sshArgs += $target

    return @{
        Name         = $Name
        Declared     = $true
        Target       = $target
        Port         = $port
        Key          = $key
        Password     = $password
        SudoPassword = $sudoPassword
        Listen       = $listen
        Endpoint     = "${hostName}:${listen}"
        MaxClients   = $max
        SshArgs      = $sshArgs
    }
}

<#
.SYNOPSIS
    Arm SSH_ASKPASS so ssh takes a password from gpb.conf instead of prompting.

.DESCRIPTION
    OpenSSH will not read a password from a flag, a file or an environment variable - that is
    deliberate on its part. The one supported way to supply one without a human at the keyboard
    is SSH_ASKPASS: ssh runs the named program and reads the password from its output. On Windows
    that program has to be something Windows can execute, so the helper is a .cmd rather than a
    shell script. It reads the password from the environment rather than containing it.

    Returns the helper's path, which must be handed to Disable-GpbAskpass in a finally block.
    Returns $null when there is no password, which is the normal case.
#>
function Enable-GpbAskpass {
    param([string]$Password)

    if (-not $Password) { return $null }

    $helper = [System.IO.Path]::GetTempFileName() + '.cmd'
    Set-Content -Path $helper -Value '@echo %GPB_SSH_PASSWORD%' -Encoding ascii
    $env:GPB_SSH_PASSWORD = $Password
    $env:SSH_ASKPASS = $helper
    # Without force, ssh only consults the helper when there is no terminal, and goes back to
    # prompting the moment one exists - which is exactly when you run this by hand.
    $env:SSH_ASKPASS_REQUIRE = 'force'
    return $helper
}

<#
.SYNOPSIS
    Undo Enable-GpbAskpass. Safe to call with $null, so it belongs in a finally block.
#>
function Disable-GpbAskpass {
    param([string]$Helper)

    if ($Helper) { Remove-Item $Helper -Force -ErrorAction SilentlyContinue }
    # Clear these from this process too, so the password does not linger in the environment of
    # anything else started from the same shell afterwards.
    Remove-Item Env:GPB_SSH_PASSWORD, Env:SSH_ASKPASS, Env:SSH_ASKPASS_REQUIRE -ErrorAction SilentlyContinue
}

<#
.SYNOPSIS
    Quote ssh arguments for a command line handed to cmd.exe.

.DESCRIPTION
    deploy.ps1 has to go through cmd because PowerShell 5.1 has no input redirection operator for
    native commands. A key path with a space in it - normal under C:\Users\First Last - would
    otherwise arrive as two arguments.
#>
function ConvertTo-GpbCmdArgs {
    param([string[]]$Arguments)

    $quoted = foreach ($a in $Arguments) {
        if ($a -match '[\s"]') { '"' + $a + '"' } else { $a }
    }
    return ($quoted -join ' ')
}

<#
.SYNOPSIS
    Run ssh with one line written to its stdin as raw UTF-8 bytes. Returns ssh's exit code.

.DESCRIPTION
    Used to hand sudo a password. `$text | & ssh ...` looks like the obvious way to do this and
    is wrong twice over in PowerShell 5.1, both times silently - measured, not assumed:

      - it appends CRLF. sudo -S strips the newline and keeps the carriage return, so the
        password it compares is the real one with a stray \r on the end, and authentication
        fails for a password that is perfectly correct.
      - it cannot carry a non-ASCII character. Every byte outside ASCII is written as 0x3F, '?',
        whatever $OutputEncoding and [Console]::OutputEncoding are set to. There is no setting
        that fixes it.

    Writing to the process's stdin stream directly avoids both: the bytes are exactly the ones
    asked for, terminated by a single LF.

    stdout and stderr are deliberately NOT redirected, so install.sh's output appears live.
#>
function Invoke-GpbSshWithStdin {
    param(
        [string[]]$SshArgs,
        [string]$RemoteCommand,
        [string]$StdinLine
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'ssh'
    # ProcessStartInfo.ArgumentList does not exist in .NET Framework, so the arguments have to be
    # one quoted string. None of them contains a double quote - the remote command is written
    # without any, for the cmd path - so quoting whatever holds whitespace is enough.
    $psi.Arguments = ConvertTo-GpbCmdArgs ($SshArgs + $RemoteCommand)
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true

    $proc = [System.Diagnostics.Process]::Start($psi)
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($StdinLine + "`n")
        $proc.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
        $proc.StandardInput.BaseStream.Flush()
    } finally {
        $proc.StandardInput.Close()
    }
    $proc.WaitForExit()
    return $proc.ExitCode
}
