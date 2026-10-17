using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GamePingBooster.App.Services;

/// <summary>
/// One reading of the whole machine. CPU, disk and GPU are over the interval since the last reading; RAM is now.
/// </summary>
public sealed record MachineLoad(
    double CpuPercent,
    double RamAvailableMb,
    double RamTotalMb,
    DiskLoad? Disk,
    GpuLoad? Gpu);

/// <summary>
/// The slowest physical disk that is doing real work: how long a read or write takes (Avg. Disk sec/Transfer).
/// Latency, not "active time": on 2026-10-17 a WinDirStat scan of an NVMe C: read 30-100 MB/s at 0.02 ms and
/// never above 32 % active, while 500+ MB/s of reads stayed under 0.5 ms. Stutter is a slow answer, not a busy disk.
/// </summary>
public sealed record DiskLoad(double LatencyMs, double TransfersPerSecond);

/// <summary>3D engine use on the busiest GPU, in total and per process id, as Task Manager's GPU column.</summary>
public sealed record GpuLoad(double TotalPercent, IReadOnlyDictionary<int, double> ByPid);

/// <summary>
/// A process using a lot of one resource, with its share (CPU %, GPU %, disk MB/s, RAM MB). Name is what the player
/// reads (see <see cref="PcResources.Describe"/>); Key stays the process name, to tell whether it still runs.
/// </summary>
public sealed record TopProcess(string Name, double Amount)
{
    public string Key { get; init; } = Name;
}

/// <summary>
/// Reads CPU, GPU, RAM and disk for the "Tài nguyên máy" line of the checklist. CPU from GetSystemTimes, RAM from
/// GlobalMemoryStatusEx (free MB, not the load %: 85 % of 32 GB still leaves 5 GB), disk latency and GPU engines from
/// PDH with English counter names, so any UI language works.
///
/// Not thread-safe: one instance, one caller. Dispose closes the PDH query.
/// </summary>
public sealed class PcResources : IDisposable
{
    /// <summary>Below this many transfers a second a disk's average latency is one or two requests: noise.</summary>
    private const double BusyDiskTransfers = 10;

    private long _idle, _kernel, _user;
    private IntPtr _query;
    private IntPtr _diskLatency, _diskTransfers, _gpu;

    public PcResources()
    {
        try
        {
            if (PdhOpenQueryW(IntPtr.Zero, IntPtr.Zero, out _query) != 0) return;
            _diskLatency = Add(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer");
            _diskTransfers = Add(@"\PhysicalDisk(*)\Disk Transfers/sec");
            // Missing on drivers without WDDM 2.x GPU counters; then GPU is simply not shown.
            _gpu = Add(@"\GPU Engine(*engtype_3D)\Utilization Percentage");
            PdhCollectQueryData(_query); // rates need two collections; Read() makes the second
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _query = IntPtr.Zero;
        }
    }

    private IntPtr Add(string path) =>
        PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter) == 0 ? counter : IntPtr.Zero;

    /// <summary>A new reading. The first call after construction has no CPU interval yet and reads 0 % CPU.</summary>
    public MachineLoad Read()
    {
        double cpu = 0;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            // Kernel time includes idle time.
            var dIdle = idle - _idle;
            var dTotal = kernel - _kernel + (user - _user);
            if (_kernel != 0 && dTotal > 0) cpu = Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
            (_idle, _kernel, _user) = (idle, kernel, user);
        }

        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        double availableMb = 0, totalMb = 0;
        if (GlobalMemoryStatusEx(ref memory))
        {
            availableMb = memory.AvailPhys / 1048576.0;
            totalMb = memory.TotalPhys / 1048576.0;
        }

        DiskLoad? disk = null;
        GpuLoad? gpu = null;
        if (_query != IntPtr.Zero && PdhCollectQueryData(_query) == 0)
        {
            disk = ReadDisk();
            gpu = ReadGpu();
        }
        return new MachineLoad(cpu, availableMb, totalMb, disk, gpu);
    }

    /// <summary>The disk with the slowest answers among those doing real work; a quiet machine reads 0 ms.</summary>
    private DiskLoad? ReadDisk()
    {
        var latency = ReadArray(_diskLatency);
        var transfers = ReadArray(_diskTransfers);
        if (latency is null || transfers is null) return null;

        var rates = transfers.Where(t => t.Name != "_Total").ToDictionary(t => t.Name, t => t.Value);
        DiskLoad worst = new(0, rates.Values.DefaultIfEmpty(0).Max());
        foreach (var (name, seconds) in latency)
        {
            if (name == "_Total" || !rates.TryGetValue(name, out var rate) || rate < BusyDiskTransfers) continue;
            if (seconds * 1000 > worst.LatencyMs) worst = new DiskLoad(seconds * 1000, rate);
        }
        return worst;
    }

    /// <summary>
    /// 3D engine use per process on the busiest adapter. Instances read
    /// "pid_1234_luid_0x00000000_0x0000D1C2_phys_0_eng_0_engtype_3D".
    /// </summary>
    private GpuLoad? ReadGpu()
    {
        var engines = ReadArray(_gpu);
        if (engines is null) return null;

        var byAdapter = new Dictionary<string, Dictionary<int, double>>();
        foreach (var (name, value) in engines)
        {
            var parts = name.Split('_');
            // pid_<n>_luid_<a>_<b>_phys_<p>_...
            if (parts.Length < 8 || parts[0] != "pid" || !int.TryParse(parts[1], out var pid)) continue;
            var adapter = string.Join('_', parts[3], parts[4], parts[6]);
            if (!byAdapter.TryGetValue(adapter, out var pids)) byAdapter[adapter] = pids = [];
            pids[pid] = pids.GetValueOrDefault(pid) + value;
        }
        if (byAdapter.Count == 0) return new GpuLoad(0, new Dictionary<int, double>());

        var busiest = byAdapter.Values.MaxBy(p => p.Values.Sum())!;
        return new GpuLoad(Math.Clamp(busiest.Values.Sum(), 0, 100), busiest);
    }

    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const uint PdhMoreData = 0x800007D2;
    private const int ItemSize = 24; // PDH_FMT_COUNTERVALUE_ITEM_W on x64: szName(8) CStatus(4) pad(4) double(8)

    /// <summary>Every instance of a wildcard counter with valid data, or null when it cannot be read.</summary>
    private static List<(string Name, double Value)>? ReadArray(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        uint size = 0;
        var status = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out var count, IntPtr.Zero);
        if (status != PdhMoreData || size == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out count, buffer) != 0) return null;
            var items = new List<(string, double)>((int)count);
            for (var i = 0; i < count; i++)
            {
                var item = buffer + i * ItemSize;
                if (Marshal.ReadInt32(item, 8) != 0) continue; // CStatus: PDH_CSTATUS_VALID_DATA = 0
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                items.Add((name, BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16))));
            }
            return items;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    // ------------------------------------------------------------ per process

    /// <summary>Per-process CPU time, I/O bytes and working set at one moment.</summary>
    public sealed class ProcessSnapshot
    {
        internal readonly Dictionary<int, (string Name, long Cpu, ulong Io, double RamMb, int Session)> Rows = [];
        internal long At;
    }

    private const int SystemProcessInformation = 5;
    private const uint StatusInfoLengthMismatch = 0xC0000004;
    private static int _snapshotBuffer = 1 << 20;

    /// <summary>
    /// Every process in one NtQuerySystemInformation call - Task Manager's source - with no handle opened to any of
    /// them. v1 opened each process for GetProcessTimes; that fails on processes the player's account cannot open -
    /// most likely the game under BattlEye: on 2026-10-17 PUBG read "game 1%" beside 30 % of "background" with no app
    /// to name (the game is never named). Opening the game process is also what principle 3 of the checklist
    /// rules out.
    ///
    /// SYSTEM_PROCESS_INFORMATION on x64 (offsets checked against Get-Process on the owner's PC): NextEntryOffset 0,
    /// UserTime 40, KernelTime 48, ImageName 56 (Length u16, Buffer ptr at 64), UniqueProcessId 80,
    /// SessionId 100, WorkingSetSize 144, ReadTransferCount 232, WriteTransferCount 240.
    /// </summary>
    public static ProcessSnapshot TakeSnapshot()
    {
        var snapshot = new ProcessSnapshot { At = Stopwatch.GetTimestamp() };
        if (!Environment.Is64BitProcess) return snapshot;

        var size = _snapshotBuffer;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            uint status;
            while ((status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out var needed)) == StatusInfoLengthMismatch)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                size = Math.Max(size * 2, needed + 65536);
                buffer = Marshal.AllocHGlobal(size);
            }
            _snapshotBuffer = size;
            if (status != 0) return snapshot;

            var offset = 0;
            while (true)
            {
                var entry = buffer + offset;
                var pid = (int)Marshal.ReadIntPtr(entry, 80);
                var nameBytes = (ushort)Marshal.ReadInt16(entry, 56);
                if (pid is not (0 or 4) && nameBytes > 0) // Idle, System
                {
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry, 64), nameBytes / 2);
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4]; // as Process.ProcessName
                    var cpu = Marshal.ReadInt64(entry, 40) + Marshal.ReadInt64(entry, 48);
                    var io = (ulong)Marshal.ReadInt64(entry, 232) + (ulong)Marshal.ReadInt64(entry, 240);
                    var ramMb = Marshal.ReadInt64(entry, 144) / 1048576.0;
                    var session = Marshal.ReadInt32(entry, 100);
                    snapshot.Rows[pid] = (name, cpu, io, ramMb, session);
                }
                var next = Marshal.ReadInt32(entry, 0);
                if (next == 0) break;
                offset += next;
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
        return snapshot;
    }

    /// <summary>
    /// One process's use between two snapshots: CPU as % of the whole machine (Task Manager), I/O in MB/s (all I/O,
    /// so a downloader counts too), working set in MB at the second snapshot.
    /// </summary>
    public sealed record Usage(int Pid, string Name, double CpuPercent, double IoMbPerSecond, double RamMb, int Session);

    public static List<Usage> Between(ProcessSnapshot before, ProcessSnapshot after)
    {
        var seconds = (after.At - before.At) / (double)Stopwatch.Frequency;
        var result = new List<Usage>(after.Rows.Count);
        if (seconds <= 0) return result;
        var cpuTicks = seconds * 10_000_000 * Environment.ProcessorCount; // FILETIME is 100 ns

        foreach (var (pid, row) in after.Rows)
        {
            if (!before.Rows.TryGetValue(pid, out var old) || old.Name != row.Name) continue;
            var cpu = Math.Max(0, row.Cpu - old.Cpu) / cpuTicks * 100;
            var io = row.Io >= old.Io ? (row.Io - old.Io) / seconds / 1e6 : 0;
            result.Add(new Usage(pid, row.Name, cpu, io, row.RamMb, row.Session));
        }
        return result;
    }

    /// <summary>
    /// The biggest few by <paramref name="amount"/>, biggest first, summed by name: Chrome is dozens of processes but
    /// one name.
    /// </summary>
    public static List<TopProcess> Top<T>(IEnumerable<T> rows, Func<T, string> name, Func<T, double> amount, int count = 5) =>
        rows.GroupBy(name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TopProcess(g.Key, g.Sum(amount)))
            .OrderByDescending(t => t.Amount)
            .Take(count)
            .ToList();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Descriptions =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> Paths =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string WindowsDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    /// <summary>
    /// A part of Windows rather than an app the player started: a service (session 0 - WmiPrvSE, Defender, svchost,
    /// any vendor's service) or an exe under the Windows folder (TextInputHost, ShellExperienceHost...). None of them
    /// can be closed like an app, so none is ever named (owner, 2026-10-17: WmiPrvSE was listed as "nên tắt"). Told
    /// apart by where they run, not by a list of names nobody could keep complete. The path is looked up once per
    /// process name; this opens no handle to a session-0 process at all.
    /// </summary>
    public static bool IsWindowsPart(Usage process) =>
        process.Session == 0
        || ImagePath(process.Name, process.Pid) is { } path && path.StartsWith(WindowsDirectory, StringComparison.OrdinalIgnoreCase);

    /// <summary>The exe behind a process name, or null when the process cannot be opened. Cached by name.</summary>
    private static string? ImagePath(string name, int pid) => Paths.GetOrAdd(name, _ =>
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    });

    /// <summary>
    /// The same process under the name Task Manager shows - the exe's FileDescription ("Desktop Window Manager",
    /// "Google Chrome") - or its process name when the file has none or cannot be opened. Cached by process name.
    /// </summary>
    public static List<TopProcess> Describe(List<TopProcess> tops, IReadOnlyDictionary<string, int> pidByName) =>
        tops.Select(top => pidByName.TryGetValue(top.Key, out var pid)
                ? top with { Name = Descriptions.GetOrAdd(top.Key, name => FileDescription(name, pid) ?? name) }
                : top)
            .ToList();

    private static string? FileDescription(string name, int pid)
    {
        if (ImagePath(name, pid) is not { } path) return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var text = (info.FileDescription ?? "").Trim();
            if (text.Length == 0) text = (info.ProductName ?? "").Trim();
            return text.Length > 0 ? text : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Process ids that own a window the player can see: visible, titled, not a tool window. An app without one is
    /// still running with nothing on screen to close - Cakewalk Sonar after its window was closed (owner, 2026-10-17)
    /// - so the only way to close it is to end it. Walks the top-level windows with FindWindowEx: no callback.
    /// </summary>
    public static HashSet<int> PidsWithWindow()
    {
        var pids = new HashSet<int>();
        var window = IntPtr.Zero;
        while ((window = FindWindowExW(IntPtr.Zero, window, null, null)) != IntPtr.Zero)
        {
            if (!IsWindowVisible(window) || GetWindowTextLengthW(window) == 0) continue;
            if ((GetWindowLongPtrW(window, GwlExStyle).ToInt64() & WsExToolWindow) != 0) continue;
            GetWindowThreadProcessId(window, out var pid);
            pids.Add((int)pid);
        }
        return pids;
    }

    /// <summary>
    /// Ends every process by this name in the player's own session. False when any could not be ended (elevated,
    /// already gone mid-way is fine). Only for a named background app with no window: never the game, never Windows,
    /// never a service - none of those is ever named.
    /// </summary>
    public static bool EndProcesses(string name)
    {
        var session = Process.GetCurrentProcess().SessionId;
        var ok = true;
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != session) continue;
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // Exited already.
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    ok = false;
                }
            }
        }
        return ok;
    }

    // ------------------------------------------------------------ native

    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? title);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetWindowTextLengthW(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    // DllImport rather than LibraryImport, as elsewhere in this app: see Loc.cs.

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, [Out] char[] name, ref uint size);

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(IntPtr dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize,
        out uint itemCount, IntPtr items);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);
}
