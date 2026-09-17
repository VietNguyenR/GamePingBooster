using System.Runtime.InteropServices;

namespace GamePingBooster.Service.Discovery;

/// <summary>
/// P/Invoke into the ETW consumer API (advapi32) and the event decoder (tdh).
///
/// Written against the x64 layouts only - the client is win-x64 and nothing else. The structs
/// that matter carry their size, and <see cref="SelfCheck"/> refuses to run if the compiler laid
/// one out differently, because a wrong offset here does not fail: it reads the wrong field.
///
/// Only the fields this tool touches are declared on the large structs. EVENT_TRACE_LOGFILEW is
/// 448 bytes, most of it a log-file header and a time zone that a real-time session never fills.
/// </summary>
internal static unsafe partial class EtwNative
{
    internal const uint ErrorSuccess = 0;
    internal const uint ErrorAccessDenied = 5;
    internal const uint ErrorAlreadyExists = 183;
    internal const uint ErrorCancelled = 1223;
    internal const uint ErrorWmiInstanceNotFound = 4201;
    internal const uint ErrorInsufficientBuffer = 122;
    internal const uint ErrorNotSupported = 50;
    internal const uint ErrorInvalidParameter = 87;

    internal const uint WnodeFlagTracedGuid = 0x00020000;
    internal const uint EventTraceRealTimeMode = 0x00000100;

    /// <summary>WNODE_HEADER.ClientContext: timestamps in QueryPerformanceCounter units, the clock
    /// Stopwatch.GetTimestamp reads, so an event time and a local time can be subtracted.</summary>
    internal const uint ClockQpc = 1;

    internal const uint EventTraceControlQuery = 0;
    internal const uint EventTraceControlStop = 1;

    internal const uint EventControlCodeDisableProvider = 0;
    internal const uint EventControlCodeEnableProvider = 1;
    internal const byte TraceLevelVerbose = 5;

    internal const uint ProcessTraceModeRealTime = 0x00000100;
    internal const uint ProcessTraceModeEventRecord = 0x10000000;

    internal const uint EnableTraceParametersVersion2 = 2;
    internal const uint EventFilterTypeEventId = 0x80000200;

    internal const ushort EventHeaderFlag32BitHeader = 0x0020;

    internal const ulong InvalidProcessTraceHandle = ulong.MaxValue;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    /// <summary>EVENT_TRACE_PROPERTIES, 120 bytes. The logger name is written after it, in the
    /// same allocation, at <see cref="LoggerNameOffset"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public nint LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    /// <summary>EVENT_TRACE_LOGFILEW, 448 bytes on x64.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 448)]
    internal struct EventTraceLogfile
    {
        [FieldOffset(0)] public char* LogFileName;
        [FieldOffset(8)] public char* LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(424)] public delegate* unmanaged<EventRecord*, void> EventRecordCallback;
        [FieldOffset(440)] public nint Context;
    }

    /// <summary>EVENT_RECORD, 112 bytes on x64 - its EVENT_HEADER flattened into it.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 112)]
    internal struct EventRecord
    {
        [FieldOffset(0)] public ushort HeaderSize;
        [FieldOffset(4)] public ushort HeaderFlags;
        [FieldOffset(12)] public uint ProcessId;
        [FieldOffset(16)] public long TimeStamp;
        [FieldOffset(24)] public Guid ProviderId;
        [FieldOffset(40)] public ushort EventId;
        [FieldOffset(42)] public byte EventVersion;
        [FieldOffset(86)] public ushort UserDataLength;
        [FieldOffset(96)] public byte* UserData;
        [FieldOffset(104)] public nint UserContext;
    }

    /// <summary>ENABLE_TRACE_PARAMETERS, 48 bytes on x64.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    internal struct EnableTraceParameters
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(4)] public uint EnableProperty;
        [FieldOffset(8)] public uint ControlFlags;
        [FieldOffset(12)] public Guid SourceId;
        [FieldOffset(32)] public EventFilterDescriptor* EnableFilterDesc;
        [FieldOffset(40)] public uint FilterDescCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventFilterDescriptor
    {
        public ulong Ptr;
        public uint Size;
        public uint Type;
    }

    // TRACE_EVENT_INFO and EVENT_PROPERTY_INFO are read out of a byte buffer TDH fills, at these
    // offsets, rather than declared: the first ends in a variable-length array.
    internal const int TraceEventInfoPropertyCount = 100;
    internal const int TraceEventInfoTopLevelPropertyCount = 104;
    internal const int TraceEventInfoPropertyArray = 112;
    internal const int EventPropertyInfoSize = 24;

    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint StartTrace(out ulong traceHandle, string instanceName, EventTraceProperties* properties);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint ControlTrace(ulong traceHandle, string? instanceName, EventTraceProperties* properties, uint controlCode);

    [LibraryImport("advapi32.dll", EntryPoint = "EnableTraceEx2")]
    internal static partial uint EnableTraceEx2(ulong traceHandle, Guid* providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, EnableTraceParameters* enableParameters);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)]
    internal static partial ulong OpenTrace(EventTraceLogfile* logfile);

    [LibraryImport("advapi32.dll", EntryPoint = "ProcessTrace")]
    internal static partial uint ProcessTrace(ulong* handleArray, uint handleCount, nint startTime, nint endTime);

    [LibraryImport("advapi32.dll", EntryPoint = "CloseTrace")]
    internal static partial uint CloseTrace(ulong traceHandle);

    [LibraryImport("tdh.dll", EntryPoint = "TdhGetEventInformation")]
    internal static partial uint TdhGetEventInformation(EventRecord* eventRecord, uint tdhContextCount, nint tdhContext,
        byte* buffer, ref uint bufferSize);

    /// <summary>Throws if a struct is not the size Windows expects. A mismatch would not crash - it
    /// would quietly decode garbage - so it is checked before anything is started.</summary>
    internal static void SelfCheck()
    {
        Check(sizeof(WnodeHeader), 48, "WNODE_HEADER");
        Check(sizeof(EventTraceProperties), 120, "EVENT_TRACE_PROPERTIES");
        Check(sizeof(EventTraceLogfile), 448, "EVENT_TRACE_LOGFILEW");
        Check(sizeof(EventRecord), 112, "EVENT_RECORD");
        Check(sizeof(EnableTraceParameters), 48, "ENABLE_TRACE_PARAMETERS");
        Check(sizeof(EventFilterDescriptor), 16, "EVENT_FILTER_DESCRIPTOR");

        static void Check(int actual, int expected, string name)
        {
            if (actual != expected)
            {
                throw new InvalidOperationException($"{name} is {actual} bytes here, Windows expects {expected}. " +
                                                    "This tool only supports 64-bit Windows.");
            }
        }
    }

    internal static string Describe(uint error) => error switch
    {
        ErrorSuccess => "success",
        ErrorAccessDenied => "access denied (5) - run from an Administrator terminal",
        ErrorAlreadyExists => "a session with that name already exists (183)",
        ErrorWmiInstanceNotFound => "no such session (4201)",
        ErrorNotSupported => "not supported (50)",
        ErrorInvalidParameter => "invalid parameter (87)",
        _ => $"Win32 error {error}: {new System.ComponentModel.Win32Exception((int)error).Message}",
    };
}
