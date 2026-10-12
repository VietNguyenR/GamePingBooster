namespace GamePingBooster.Core.Updates;

/// <summary>
/// Where an automatic update is, step by step, for the updater window (gpb-updater.exe) to show.
///
/// A file and not the pipe, because three processes write it and the window has to outlive two of them:
///   - the service, while it checks, downloads, verifies and starts setup (AutoUpdater);
///   - setup itself, while it closes the app, stops the service, copies and configures (the [Code] section
///     of GamePingBooster.iss writes the same lines - keep the phase names below and there in step);
///   - nobody, once setup has finished: the window then watches the service and the pipe by itself.
/// The pipe also takes one client at a time, and that client is the app.
///
/// The file lives in update\status\, which SYSTEM and Administrators write and Users only read (the rest
/// of update\ is not readable at all). It describes, it decides nothing: the worst a forged one can do is
/// show a wrong line in the window of the user who forged it.
///
/// Format: "key=value" lines. Unknown keys are ignored, so either side can add one.
///   phase    one of the constants below
///   version  the release being installed, without "v"
///   from     the version installed now
///   done     bytes downloaded, or setup's progress counter
///   total    what done counts up to
///   reason   for <see cref="None"/> and the failures: a short code, or a sentence from setup
/// Freshness is the file's write time: the window ignores anything written before it was started.
/// </summary>
public static class UpdateProgress
{
    public static string FolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GamePingBooster", "update", "status");

    public static string FilePath => Path.Combine(FolderPath, "progress.txt");

    // ---- written by the service
    public const string Checking = "checking";
    /// <summary>Nothing will be installed now; <c>reason</c> says why (see the Reason* codes).</summary>
    public const string None = "none";
    public const string Downloading = "downloading";
    public const string Verifying = "verifying";
    /// <summary>Setup has been started. From here the app is closed and the window carries on alone.</summary>
    public const string Starting = "starting";
    public const string Failed = "failed";

    // ---- written by setup (GamePingBooster.iss)
    public const string ClosingApp = "closing-app";
    public const string StoppingService = "stopping-service";
    public const string Copying = "copying";
    public const string RegisteringService = "registering-service";
    public const string StartingService = "starting-service";
    public const string InstallingDriver = "installing-driver";
    public const string Firewall = "firewall";
    public const string SetupDone = "setup-done";
    /// <summary>Setup refused before copying anything; <c>reason</c> is its sentence.</summary>
    public const string SetupFailed = "setup-failed";
    /// <summary>Setup ended without reaching <see cref="SetupDone"/>.</summary>
    public const string SetupEnded = "setup-ended";

    // ---- reasons for None and Failed
    public const string ReasonLatest = "latest";
    public const string ReasonOff = "off";
    public const string ReasonBusy = "busy";
    public const string ReasonGame = "game";
    public const string ReasonRefused = "refused";
    public const string ReasonLater = "later";
    public const string ReasonUnavailable = "unavailable";
    public const string ReasonDownload = "download";
    public const string ReasonSignature = "signature";
    public const string ReasonSetup = "setup";

    /// <summary>The lines of a progress file.</summary>
    public static string Format(string phase, string? version = null, string? from = null,
        long? done = null, long? total = null, string? reason = null)
    {
        var lines = new List<string> { $"phase={phase}" };
        if (version is not null) lines.Add($"version={version}");
        if (from is not null) lines.Add($"from={from}");
        if (done is not null) lines.Add($"done={done}");
        if (total is not null) lines.Add($"total={total}");
        // One line: a reason is a sentence at most.
        if (reason is not null) lines.Add($"reason={reason.Replace('\r', ' ').Replace('\n', ' ')}");
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>Reads a progress file's keys. Null when it has no phase - missing, half written, or not ours.</summary>
    public static Dictionary<string, string>? Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return values.ContainsKey("phase") ? values : null;
    }
}
