using System.Runtime.InteropServices;

namespace GamePingBooster.Core.Native;

/// <summary>
/// The Windows power plan, its "Power mode" overlay and the battery, for item 5 of the PC tune-up panel ("Chế độ nguồn
/// + card mạng", pc-checklist-handoff.md). In Core because both halves need the same answer: the app reads it to say
/// what it found, the service reads it again before and after it changes anything.
///
/// Only powrprof.dll's documented scheme calls (Vista and later) plus the overlay calls Settings itself uses (Windows
/// 10 1709 and later). An overlay call missing from an older Windows reads as "not supported", never as a crash.
///
/// How a plan is judged: by its personality - the "Power plan type" every scheme carries, OEM and custom ones too
/// ("ASUS Recommended" is a Balanced underneath) - not by its name or GUID. High performance (and Ultimate, whose type is
/// High performance) is fast. Balanced is fast only with the "Best performance" overlay on top - what Windows 11's
/// Settings › Power mode sets; Windows ignores the overlay on any other plan (measured 2026-10-17: effective overlay
/// stays empty on High performance even with one stored).
/// </summary>
public static class PowerPlan
{
    private const string Dll = "powrprof.dll";

    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    /// <summary>The "Power mode" overlays (Settings › System › Power). Empty is Windows' own "Balanced".</summary>
    public static readonly Guid OverlayBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");
    public static readonly Guid OverlayDefault = Guid.Empty;

    private static readonly Guid NoSubgroup = new("fea3413e-7e05-4911-9a71-700331f1c294");
    private static readonly Guid PersonalitySetting = new("245d8541-3943-4422-b025-13a784f679b7");

    private const uint AccessScheme = 16;
    private const uint ErrorNoMoreItems = 259;

    public enum Personality
    {
        Unknown = -1,
        PowerSaver = 0,
        HighPerformance = 1,
        Balanced = 2,
    }

    /// <summary>What the plan looks like now. <see cref="Readable"/> false when powrprof would not say.</summary>
    public sealed record State(
        bool Readable,
        Guid Scheme,
        Personality Type,
        bool OverlaySupported,
        Guid Overlay,
        bool OnBattery,
        bool BatterySaver)
    {
        /// <summary>Windows is not holding the CPU back for power: High performance, or Balanced at Best performance.</summary>
        public bool IsFast => Type == Personality.HighPerformance
                              || (Type == Personality.Balanced && Overlay == OverlayBestPerformance);
    }

    [DllImport(Dll, EntryPoint = "PowerGetActiveScheme", ExactSpelling = true)]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport(Dll, EntryPoint = "PowerSetActiveScheme", ExactSpelling = true)]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport(Dll, EntryPoint = "PowerReadACValueIndex", ExactSpelling = true)]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid,
        ref Guid powerSettingGuid, out uint acValueIndex);

    [DllImport(Dll, EntryPoint = "PowerEnumerate", ExactSpelling = true)]
    private static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupGuid,
        uint accessFlags, uint index, out Guid buffer, ref uint bufferSize);

    [DllImport(Dll, EntryPoint = "PowerGetEffectiveOverlayScheme", ExactSpelling = true)]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    [DllImport(Dll, EntryPoint = "PowerGetActualOverlayScheme", ExactSpelling = true)]
    private static extern uint PowerGetActualOverlayScheme(out Guid overlay);

    [DllImport(Dll, EntryPoint = "PowerSetActiveOverlayScheme", ExactSpelling = true)]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlay);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetSystemPowerStatus", ExactSpelling = true)]
    private static extern int GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr mem);

    /// <summary>Reads the plan, the overlay and the battery. Never throws.</summary>
    public static State Read()
    {
        var (onBattery, saver) = Battery();
        var scheme = ActiveScheme();
        if (scheme is not { } active) return new State(false, Guid.Empty, Personality.Unknown, false, Guid.Empty, onBattery, saver);
        var overlay = EffectiveOverlay();
        return new State(true, active, TypeOf(active), overlay is not null, overlay ?? Guid.Empty, onBattery, saver);
    }

    /// <summary>The active scheme, or null when powrprof would not say.</summary>
    public static Guid? ActiveScheme()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero) return null;
            try
            {
                return Marshal.PtrToStructure<Guid>(ptr);
            }
            finally
            {
                LocalFree(ptr);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The scheme's "Power plan type". Unknown when it has none or it cannot be read.</summary>
    public static Personality TypeOf(Guid scheme)
    {
        try
        {
            var sub = NoSubgroup;
            var setting = PersonalitySetting;
            if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var value) != 0) return Personality.Unknown;
            return value <= 2 ? (Personality)value : Personality.Unknown;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return Personality.Unknown;
        }
    }

    /// <summary>The schemes the player can pick (powercfg /list). Hidden templates are not in it.</summary>
    public static List<Guid> ListedSchemes()
    {
        var schemes = new List<Guid>();
        try
        {
            for (uint i = 0; i < 64; i++)
            {
                uint size = 16;
                var result = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, i, out var guid, ref size);
                if (result == ErrorNoMoreItems || result != 0) break;
                schemes.Add(guid);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return schemes;
    }

    /// <summary>The overlay Windows applies now, or null when this Windows has no overlays.</summary>
    public static Guid? EffectiveOverlay()
    {
        try
        {
            return PowerGetEffectiveOverlayScheme(out var g) == 0 ? g : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The overlay stored for the current power source (applied or not), or null when unsupported.</summary>
    public static Guid? ActualOverlay()
    {
        try
        {
            return PowerGetActualOverlayScheme(out var g) == 0 ? g : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Makes a scheme active. 0 on success, else the Win32 error. Needs no more than the player's rights.</summary>
    public static uint SetActive(Guid scheme)
    {
        try
        {
            return PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return 127; // ERROR_PROC_NOT_FOUND
        }
    }

    /// <summary>Sets the overlay for the current power source. 0 on success, else the Win32 error.</summary>
    public static uint SetOverlay(Guid overlay)
    {
        try
        {
            return PowerSetActiveOverlayScheme(overlay);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return 127;
        }
    }

    /// <summary>
    /// (on battery, battery saver on). A desktop, or a laptop Windows cannot read, is (false, false): the line only
    /// warns about what it knows.
    /// </summary>
    public static (bool OnBattery, bool Saver) Battery()
    {
        try
        {
            if (GetSystemPowerStatus(out var s) == 0) return (false, false);
            var hasBattery = s.BatteryFlag is not (128 or 255);
            return (hasBattery && s.AcLineStatus == 0, s.SystemStatusFlag == 1);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return (false, false);
        }
    }

    /// <summary>
    /// What a boost would switch to: Windows' own High performance if the player can pick it, else Ultimate
    /// Performance if listed, else Balanced with the Best performance overlay (laptops with Modern Standby list only
    /// Balanced, and so do many clean Windows 11 installs). Null when there is nothing faster to switch to.
    /// Never a plan the player cannot see, and never a new plan: nothing is created on their PC.
    /// </summary>
    public static (Guid Scheme, Guid? Overlay)? BoostTarget(State now)
    {
        if (!now.Readable || now.IsFast) return null;
        var listed = ListedSchemes();
        if (listed.Contains(HighPerformance) && TypeOf(HighPerformance) == Personality.HighPerformance) return (HighPerformance, null);
        if (listed.Contains(UltimatePerformance) && TypeOf(UltimatePerformance) == Personality.HighPerformance) return (UltimatePerformance, null);
        if (now.OverlaySupported && listed.Contains(Balanced)) return (Balanced, OverlayBestPerformance);
        return null;
    }
}
