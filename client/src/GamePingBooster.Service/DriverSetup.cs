using System.ComponentModel;
using System.Runtime.InteropServices;
using GamePingBooster.Service.Native;

namespace GamePingBooster.Service;

/// <summary>
/// Installing and removing the Wintun driver, for the installer to call.
///
/// Why this exists at all: the driver is installed by Windows the first time an adapter is
/// created, and that takes a few seconds and pops a driver-installation notification. Doing it
/// when the user presses Connect means their first impression of the product is a pause and a
/// system dialog. Doing it during setup, when a progress bar is already on screen and nobody is
/// waiting to play, costs nothing.
///
/// Both paths need LocalSystem, which they get: an MSI custom action runs as SYSTEM. Neither
/// reads config.json - at install time there may not be one yet, and a missing file must not
/// turn into a failed installation.
/// </summary>
internal static class DriverSetup
{
    /// <summary>
    /// A name no real adapter would have, so a leftover from a failed run is recognisable and
    /// can never be confused with the working adapter.
    /// </summary>
    private const string TempAdapterName = "GamePingBooster Setup";

    /// <summary>
    /// Forces the driver to be installed now, by creating one adapter and immediately closing
    /// it. Returns a process exit code: 0 on success.
    /// </summary>
    public static int Install(Action<string> log)
    {
        if (!OperatingSystem.IsWindows())
        {
            log("The Wintun driver only exists on Windows.");
            return 1;
        }

        nint handle;
        try
        {
            handle = WintunInterop.WintunCreateAdapter(TempAdapterName, "GamePingBooster", nint.Zero);
        }
        catch (DllNotFoundException)
        {
            log("wintun.dll was not found next to this executable. The installer must place it there.");
            return 2;
        }

        if (handle == nint.Zero)
        {
            var err = Marshal.GetLastPInvokeError();
            var hint = err == 5
                // 5 is ERROR_ACCESS_DENIED. Administrator is NOT enough for Wintun; this is the
                // single most common way to get here, and the raw message does not say so.
                ? " This needs LocalSystem - Administrator is not enough. An MSI custom action " +
                  "runs as SYSTEM; by hand, use psexec -s."
                : string.Empty;
            log($"Could not create the temporary adapter (error {err}).{hint}");
            return 3;
        }

        // Closing it removes the adapter but leaves the driver installed, which is the entire
        // point: the next create - the real one, when the user connects - is then instant.
        WintunInterop.WintunCloseAdapter(handle);

        var version = WintunInterop.WintunGetRunningDriverVersion();
        if (version == 0)
        {
            // Not fatal. The adapter was created and closed, so the driver is in place; only the
            // version query failed, and failing the install over that would be absurd.
            log("Driver installed. (Could not read its version, which does not matter here.)");
            return 0;
        }
        log($"Driver installed, version {version >> 16}.{version & 0xffff}.");
        return 0;
    }

    /// <summary>
    /// Removes the Wintun driver at uninstall time. Returns a process exit code.
    ///
    /// Leaving a network driver behind after an uninstall is the kind of thing that makes people
    /// distrust software, so this is worth getting right - but it must NEVER fail an uninstall.
    /// A user who has asked for the program to go away must not be told it cannot.
    /// </summary>
    public static int Remove(Action<string> log)
    {
        if (!OperatingSystem.IsWindows())
        {
            log("The Wintun driver only exists on Windows.");
            return 0;
        }

        try
        {
            if (WintunInterop.WintunDeleteDriver())
            {
                log("Wintun driver removed.");
            }
            else
            {
                log($"The driver could not be removed (error {Marshal.GetLastPInvokeError()}). " +
                    "It is harmless on disk and Windows will clean it up with the adapter.");
            }
        }
        catch (DllNotFoundException)
        {
            log("wintun.dll is already gone, so there is nothing to remove.");
        }
        catch (Win32Exception ex)
        {
            log($"The driver could not be removed: {ex.Message}");
        }

        // Always 0. See the summary: an uninstall that reports failure because of a leftover
        // driver leaves the user stuck with software they have asked to remove.
        return 0;
    }
}
