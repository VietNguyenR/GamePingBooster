using GamePingBooster.Core.Ipc;
using GamePingBooster.App.Services.Localization;

namespace GamePingBooster.App.Services;

/// <summary>
/// Says once, on the first start with connection-quality sharing on, that the app now sends it.
///
/// Once per Windows user, remembered by a marker file beside the refresh token. Not repeated after
/// that: a notice on every start is a notice nobody reads, and the switch stays in Settings.
/// </summary>
public static class QualityNotice
{
    public static string Text => Loc.T("notice.quality");

    private static string MarkerPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GamePingBooster", "quality-notice-shown");

    /// <summary>True the first time sharing is seen switched on for a signed-in installation; records that it was shown.</summary>
    public static bool ShouldShow(StatusMessage status)
    {
        // Signed in, too: an installation with no account sends nothing, and telling it otherwise would be
        // both untrue and a notice spent before it means anything.
        if (status.QualitySharing is not true || string.IsNullOrWhiteSpace(status.LicenceUrl) || !status.HasToken) return false;

        try
        {
            if (File.Exists(MarkerPath)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, DateTimeOffset.UtcNow.ToString("O"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Better silent than shown on every start.
            return false;
        }
    }
}
