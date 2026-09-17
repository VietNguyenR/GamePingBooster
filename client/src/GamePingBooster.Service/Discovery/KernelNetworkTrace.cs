using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static GamePingBooster.Service.Discovery.EtwNative;

namespace GamePingBooster.Service.Discovery;

internal enum NetDirection : byte { Send, Receive }

/// <summary>
/// One UDP send or receive, exactly as the event carried it. Nothing here is interpreted yet:
/// which of the two addresses is the remote one, and which byte order the ports and IPv4
/// addresses were written in, is what <see cref="DecodingRules"/> settles by experiment.
/// </summary>
internal readonly struct RawUdpEvent
{
    public required NetDirection Direction { get; init; }
    public required bool IsV6 { get; init; }
    public required uint Pid { get; init; }
    public required uint PayloadBytes { get; init; }

    // Addresses as the bytes appear in the event, read big-endian. IPv4 uses Lo only.
    public required ulong DaddrHi { get; init; }
    public required ulong DaddrLo { get; init; }
    public required ulong SaddrHi { get; init; }
    public required ulong SaddrLo { get; init; }

    // Ports as the two bytes appear in the event, read little-endian.
    public required ushort DportRaw { get; init; }
    public required ushort SportRaw { get; init; }

    /// <summary>QueryPerformanceCounter units - the same clock as Stopwatch.GetTimestamp.</summary>
    public required long Timestamp { get; init; }
}

internal interface IUdpEventSink
{
    /// <summary>Called on the ETW processing thread, once per decoded event. Keep it cheap.</summary>
    void OnUdpEvent(in RawUdpEvent e);
}

/// <summary>
/// A real-time ETW session on Microsoft-Windows-Kernel-Network, delivering every UDP send and
/// receive on the machine with the process that owns the socket.
///
/// This is the whole trick, and it is why no driver is needed: the TCP/IP stack already reports
/// this, to anyone with Administrator rights who asks. Nothing is attached to the game, and the
/// packets themselves are never seen - only their addresses, ports, sizes and owning process.
///
/// Only the four UDP events are asked for (42/43 IPv4, 58/59 IPv6), through an event-id filter so
/// the kernel does not even emit TCP data events into the session. Whether Windows honoured that
/// filter is not assumed: <see cref="OtherEvents"/> counts anything else that arrives, and
/// <see cref="EventIdFilterApplied"/> says whether it was accepted at all.
///
/// Field offsets are read from the provider's own manifest through TDH, once per event version,
/// not written down here. That keeps a Windows build that reorders or extends the template from
/// silently decoding the wrong bytes - a layout that lacks a needed field is reported instead.
///
/// ETW sessions outlive the process that started them. <see cref="Dispose"/> stops this one, and
/// a session left behind by a crash is stopped and replaced on the next start.
/// </summary>
internal sealed unsafe class KernelNetworkTrace : IDisposable
{
    /// <summary>
    /// The ETW session's name. The service and gpb-etwwatch each use their own: a session left
    /// behind under this name is stopped as stale on start, and with one shared name the tool
    /// started next to a running service would stop the service's session.
    /// </summary>
    public string SessionName { get; }

    private static readonly Guid KernelNetworkProvider = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    private const ushort UdpSendV4 = 42;
    private const ushort UdpRecvV4 = 43;
    private const ushort UdpSendV6 = 58;
    private const ushort UdpRecvV6 = 59;

    private const ulong KeywordIpv4 = 0x10;
    private const ulong KeywordIpv6 = 0x20;

    private const int LoggerNameChars = 256;

    private readonly Action<string> _log;
    private readonly IUdpEventSink _sink;
    private ulong _sessionHandle;
    private ulong _consumerHandle = InvalidProcessTraceHandle;
    private GCHandle _self;
    private char* _loggerName;
    private Thread? _thread;
    private uint _processTraceResult;

    // Layouts by (event id << 8 | version). Touched only on the processing thread.
    private readonly Dictionary<int, EventLayout?> _layouts = [];

    private long _udpEvents;
    private long _otherEvents;
    private long _undecodedEvents;
    private long _callbackTicks;

    public bool EventIdFilterApplied { get; private set; }

    /// <summary>UDP events decoded and handed to the sink.</summary>
    public long UdpEvents => Volatile.Read(ref _udpEvents);

    /// <summary>Events that were not one of the four UDP ids - non-zero means the id filter is not
    /// doing its job and the session is paying for TCP events it throws away.</summary>
    public long OtherEvents => Volatile.Read(ref _otherEvents);

    /// <summary>UDP events whose layout could not be read, or that were shorter than it.</summary>
    public long UndecodedEvents => Volatile.Read(ref _undecodedEvents);

    /// <summary>Total time spent inside the callback, in Stopwatch ticks.</summary>
    public long CallbackTicks => Volatile.Read(ref _callbackTicks);

    /// <summary>Layouts resolved so far, for the self-test's report.</summary>
    public IReadOnlyList<string> DescribeLayouts()
    {
        lock (_layouts) return _layouts.Where(p => p.Value is not null).Select(p => p.Value!.ToString()).ToList();
    }

    private readonly ThreadPriority _priority;

    private KernelNetworkTrace(string sessionName, IUdpEventSink sink, Action<string> log, ThreadPriority priority)
    {
        SessionName = sessionName;
        _sink = sink;
        _log = log;
        _priority = priority;
    }

    /// <param name="priority">The consumer thread's. The service passes BelowNormal: under CPU
    /// pressure this thread must lose to the tunnel's pumps, and what it loses is events - counted
    /// in <see cref="QueryLosses"/> - never game packets.</param>
    public static KernelNetworkTrace Start(string sessionName, IUdpEventSink sink, Action<string> log,
        ThreadPriority priority = ThreadPriority.Normal)
    {
        SelfCheck();
        var trace = new KernelNetworkTrace(sessionName, sink, log, priority);
        try
        {
            trace.StartSession();
            trace.EnableProvider();
            trace.StartConsumer();
            return trace;
        }
        catch
        {
            trace.Dispose();
            throw;
        }
    }

    private static EventTraceProperties* AllocateProperties(out int size)
    {
        size = sizeof(EventTraceProperties) + LoggerNameChars * sizeof(char);
        var properties = (EventTraceProperties*)NativeMemory.AllocZeroed((nuint)size);
        properties->Wnode.BufferSize = (uint)size;
        properties->LoggerNameOffset = (uint)sizeof(EventTraceProperties);
        return properties;
    }

    private void StartSession()
    {
        var properties = AllocateProperties(out _);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                properties->Wnode.Flags = WnodeFlagTracedGuid;
                properties->Wnode.ClientContext = ClockQpc;
                properties->LogFileMode = EventTraceRealTimeMode;
                properties->FlushTimer = 1;
                // 256 KB buffers, up to 64 of them. Real-time sessions drop whole buffers when the
                // consumer falls behind; EventsLost says whether that ever happened, so this is a
                // starting point to be corrected by measurement, not a guess to be trusted.
                properties->BufferSize = 256;
                properties->MinimumBuffers = 4;
                properties->MaximumBuffers = 64;

                var result = StartTrace(out _sessionHandle, SessionName, properties);
                if (result == ErrorSuccess) return;

                if (result == ErrorAlreadyExists && attempt == 0)
                {
                    // Left behind by a run that did not get to Dispose. It is ours by name, and a
                    // stale session would otherwise keep costing the machine until reboot.
                    _log($"An old {SessionName} session was still running - stopping it.");
                    StopByName();
                    NativeMemory.Clear(properties, (nuint)sizeof(EventTraceProperties));
                    properties->Wnode.BufferSize = (uint)(sizeof(EventTraceProperties) + LoggerNameChars * sizeof(char));
                    properties->LoggerNameOffset = (uint)sizeof(EventTraceProperties);
                    continue;
                }

                throw new InvalidOperationException($"Could not start the ETW session: {Describe(result)}.");
            }
        }
        finally
        {
            NativeMemory.Free(properties);
        }
    }

    private uint StopByName()
    {
        var properties = AllocateProperties(out _);
        try { return ControlTrace(0, SessionName, properties, EventTraceControlStop); }
        finally { NativeMemory.Free(properties); }
    }

    private void EnableProvider()
    {
        var provider = KernelNetworkProvider;
        const int eventCount = 4;
        var filterSize = 4 + eventCount * sizeof(ushort);   // EVENT_FILTER_EVENT_ID: FilterIn, Reserved, Count, Events[]
        var filter = stackalloc byte[filterSize];
        filter[0] = 1;   // FilterIn: these ids are the ones wanted
        filter[1] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(filter + 2, 2), eventCount);
        var ids = new[] { UdpSendV4, UdpRecvV4, UdpSendV6, UdpRecvV6 };
        for (var i = 0; i < eventCount; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(filter + 4 + i * 2, 2), ids[i]);
        }

        var descriptor = new EventFilterDescriptor { Ptr = (ulong)(nint)filter, Size = (uint)filterSize, Type = EventFilterTypeEventId };
        var parameters = new EnableTraceParameters
        {
            Version = EnableTraceParametersVersion2,
            EnableFilterDesc = &descriptor,
            FilterDescCount = 1,
        };

        var result = EnableTraceEx2(_sessionHandle, &provider, EventControlCodeEnableProvider, TraceLevelVerbose,
            KeywordIpv4 | KeywordIpv6, 0, 0, &parameters);
        if (result == ErrorSuccess)
        {
            EventIdFilterApplied = true;
            return;
        }

        // Not fatal: the callback drops other ids anyway. It only costs more, and OtherEvents will
        // show how much.
        _log($"The event-id filter was refused ({Describe(result)}); enabling without it.");
        result = EnableTraceEx2(_sessionHandle, &provider, EventControlCodeEnableProvider, TraceLevelVerbose,
            KeywordIpv4 | KeywordIpv6, 0, 0, null);
        if (result != ErrorSuccess)
        {
            throw new InvalidOperationException($"Could not enable Microsoft-Windows-Kernel-Network: {Describe(result)}.");
        }
    }

    private void StartConsumer()
    {
        _self = GCHandle.Alloc(this);
        _loggerName = (char*)NativeMemory.AllocZeroed((nuint)((SessionName.Length + 1) * sizeof(char)));
        SessionName.AsSpan().CopyTo(new Span<char>(_loggerName, SessionName.Length));

        var logfile = new EventTraceLogfile
        {
            LoggerName = _loggerName,
            ProcessTraceMode = ProcessTraceModeRealTime | ProcessTraceModeEventRecord,
            EventRecordCallback = &OnEventRecord,
            Context = GCHandle.ToIntPtr(_self),
        };

        _consumerHandle = OpenTrace(&logfile);
        if (_consumerHandle == InvalidProcessTraceHandle)
        {
            throw new InvalidOperationException(
                $"Could not open the ETW session for reading: {Describe((uint)Marshal.GetLastPInvokeError())}.");
        }

        // A dedicated thread: ProcessTrace blocks for the life of the session and delivers every
        // callback on the thread that called it.
        _thread = new Thread(() =>
        {
            var handle = _consumerHandle;
            _processTraceResult = ProcessTrace(&handle, 1, 0, 0);
        })
        {
            IsBackground = true,
            Name = "ETW consumer",
            Priority = _priority,
        };
        _thread.Start();
    }

    /// <summary>Buffer and loss counters as the session itself reports them.</summary>
    public (uint EventsLost, uint RealTimeBuffersLost, uint BuffersWritten) QueryLosses()
    {
        var properties = AllocateProperties(out _);
        try
        {
            var result = ControlTrace(_sessionHandle, null, properties, EventTraceControlQuery);
            return result == ErrorSuccess
                ? (properties->EventsLost, properties->RealTimeBuffersLost, properties->BuffersWritten)
                : (0u, 0u, 0u);
        }
        finally
        {
            NativeMemory.Free(properties);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnEventRecord(EventRecord* record)
    {
        try
        {
            if (GCHandle.FromIntPtr(record->UserContext).Target is KernelNetworkTrace trace) trace.Handle(record);
        }
        catch
        {
            // An exception escaping an UnmanagedCallersOnly method ends the process. Losing one
            // event is the better outcome, and UndecodedEvents already counts the kind of failure
            // that could get here.
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Handle(EventRecord* record)
    {
        var started = Stopwatch.GetTimestamp();

        if (record->ProviderId != KernelNetworkProvider)
        {
            // The session's own header event arrives first; it is not network traffic.
            return;
        }

        var id = record->EventId;
        NetDirection direction;
        bool isV6;
        switch (id)
        {
            case UdpSendV4: direction = NetDirection.Send; isV6 = false; break;
            case UdpRecvV4: direction = NetDirection.Receive; isV6 = false; break;
            case UdpSendV6: direction = NetDirection.Send; isV6 = true; break;
            case UdpRecvV6: direction = NetDirection.Receive; isV6 = true; break;
            default:
                _otherEvents++;
                _callbackTicks += Stopwatch.GetTimestamp() - started;
                return;
        }

        var key = (id << 8) | record->EventVersion;
        if (!_layouts.TryGetValue(key, out var layout))
        {
            layout = EventLayout.Resolve(record, isV6 ? 16 : 4, _log);
            lock (_layouts) _layouts[key] = layout;
        }

        if (layout is null || record->UserDataLength < layout.MinLength)
        {
            _undecodedEvents++;
            _callbackTicks += Stopwatch.GetTimestamp() - started;
            return;
        }

        var data = new ReadOnlySpan<byte>(record->UserData, record->UserDataLength);
        var addrLen = isV6 ? 16 : 4;
        var e = new RawUdpEvent
        {
            Direction = direction,
            IsV6 = isV6,
            Pid = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(layout.Pid, 4)),
            PayloadBytes = layout.Size >= 0 ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(layout.Size, 4)) : 0,
            DaddrHi = isV6 ? BinaryPrimitives.ReadUInt64BigEndian(data.Slice(layout.Daddr, 8)) : 0,
            DaddrLo = isV6 ? BinaryPrimitives.ReadUInt64BigEndian(data.Slice(layout.Daddr + 8, 8)) : BinaryPrimitives.ReadUInt32BigEndian(data.Slice(layout.Daddr, addrLen)),
            SaddrHi = isV6 ? BinaryPrimitives.ReadUInt64BigEndian(data.Slice(layout.Saddr, 8)) : 0,
            SaddrLo = isV6 ? BinaryPrimitives.ReadUInt64BigEndian(data.Slice(layout.Saddr + 8, 8)) : BinaryPrimitives.ReadUInt32BigEndian(data.Slice(layout.Saddr, addrLen)),
            DportRaw = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(layout.Dport, 2)),
            SportRaw = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(layout.Sport, 2)),
            Timestamp = record->TimeStamp,
        };

        _udpEvents++;
        _sink.OnUdpEvent(in e);
        _callbackTicks += Stopwatch.GetTimestamp() - started;
    }

    private int _disposed;

    /// <summary>Safe to call twice and from two threads: the console's exit handler and the main
    /// thread's <c>finally</c> can both get here.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (_sessionHandle != 0)
        {
            var properties = AllocateProperties(out _);
            try
            {
                var result = ControlTrace(_sessionHandle, null, properties, EventTraceControlStop);
                if (result != ErrorSuccess && result != ErrorWmiInstanceNotFound)
                {
                    _log($"Stopping the ETW session failed: {Describe(result)}. It may keep running until reboot; " +
                         $"`logman stop {SessionName} -ets` stops it by hand.");
                }
            }
            finally
            {
                NativeMemory.Free(properties);
            }
            _sessionHandle = 0;
        }

        if (_consumerHandle != InvalidProcessTraceHandle)
        {
            CloseTrace(_consumerHandle);
            _consumerHandle = InvalidProcessTraceHandle;
        }

        if (_thread is not null && !_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _log("The ETW consumer thread did not finish within 5 s of the session stopping.");
        }
        else if (_thread is not null && _processTraceResult is not ErrorSuccess and not ErrorCancelled)
        {
            _log($"ProcessTrace ended with {Describe(_processTraceResult)}.");
        }
        _thread = null;

        if (_self.IsAllocated) _self.Free();
        if (_loggerName is not null)
        {
            NativeMemory.Free(_loggerName);
            _loggerName = null;
        }
    }

    /// <summary>
    /// Where the fields this tool needs sit in one event version's payload, read from the manifest
    /// through TDH. Offsets are computed by walking the properties in order, which only works
    /// while every property before a needed one has a fixed size - true of this provider, and
    /// checked rather than assumed.
    /// </summary>
    private sealed class EventLayout
    {
        public required ushort EventId { get; init; }
        public required byte Version { get; init; }
        public required int Pid { get; init; }
        public required int Size { get; init; }
        public required int Daddr { get; init; }
        public required int Saddr { get; init; }
        public required int Dport { get; init; }
        public required int Sport { get; init; }
        public required int MinLength { get; init; }
        public required string Fields { get; init; }

        public override string ToString() => $"event {EventId} v{Version}: {Fields}";

        public static EventLayout? Resolve(EventRecord* record, int addressLength, Action<string> log)
        {
            uint size = 0;
            var result = TdhGetEventInformation(record, 0, 0, null, ref size);
            if (result != ErrorInsufficientBuffer)
            {
                log($"TDH has no layout for event {record->EventId} v{record->EventVersion}: {Describe(result)}.");
                return null;
            }

            var buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                result = TdhGetEventInformation(record, 0, 0, p, ref size);
                if (result != ErrorSuccess)
                {
                    log($"TDH failed for event {record->EventId} v{record->EventVersion}: {Describe(result)}.");
                    return null;
                }
            }

            var span = buffer.AsSpan();
            var topLevel = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(TraceEventInfoTopLevelPropertyCount, 4));
            var pointerSize = (record->HeaderFlags & EventHeaderFlag32BitHeader) != 0 ? 4 : 8;

            var offsets = new Dictionary<string, (int Offset, int Size)>(StringComparer.OrdinalIgnoreCase);
            var described = new List<string>();
            var offset = 0;
            for (var i = 0; i < topLevel; i++)
            {
                var info = span.Slice(TraceEventInfoPropertyArray + i * EventPropertyInfoSize, EventPropertyInfoSize);
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(info);
                var name = ReadName(span, BinaryPrimitives.ReadInt32LittleEndian(info.Slice(4)));
                var inType = BinaryPrimitives.ReadUInt16LittleEndian(info.Slice(8));
                var count = BinaryPrimitives.ReadUInt16LittleEndian(info.Slice(16));
                var length = BinaryPrimitives.ReadUInt16LittleEndian(info.Slice(18));

                // PropertyStruct, PropertyParamLength, PropertyParamCount: the size lives in another
                // field of the payload, so nothing after this property has a fixed offset.
                if ((flags & 0x7) != 0) break;

                var one = FixedSize(inType, length, pointerSize);
                if (one < 0) break;
                var total = one * Math.Max((int)count, 1);

                offsets[name] = (offset, total);
                described.Add($"{name}@{offset}/{total}");
                offset += total;
            }

            var fields = string.Join(" ", described);
            if (!Find("PID", 4, out var pid) || !Find("daddr", addressLength, out var daddr) ||
                !Find("saddr", addressLength, out var saddr) || !Find("dport", 2, out var dport) ||
                !Find("sport", 2, out var sport))
            {
                log($"Event {record->EventId} v{record->EventVersion} does not have the fields this tool reads " +
                    $"(PID, daddr, saddr, dport, sport at fixed offsets). Its layout: {fields}");
                return null;
            }

            var payload = Find("size", 4, out var sizeOffset) ? sizeOffset : -1;
            var min = new[] { pid + 4, daddr + addressLength, saddr + addressLength, dport + 2, sport + 2, payload + 4 }.Max();
            return new EventLayout
            {
                EventId = record->EventId, Version = record->EventVersion,
                Pid = pid, Size = payload, Daddr = daddr, Saddr = saddr, Dport = dport, Sport = sport,
                MinLength = min, Fields = fields,
            };

            bool Find(string field, int expectedSize, out int at)
            {
                if (offsets.TryGetValue(field, out var found) && found.Size == expectedSize)
                {
                    at = found.Offset;
                    return true;
                }
                at = -1;
                return false;
            }
        }

        private static string ReadName(ReadOnlySpan<byte> buffer, int offset)
        {
            if (offset <= 0 || offset >= buffer.Length) return "";
            var chars = MemoryMarshal.Cast<byte, char>(buffer[offset..]);
            var end = chars.IndexOf('\0');
            return new string(end < 0 ? chars : chars[..end]);
        }

        /// <summary>Bytes taken by one value of a TDH in-type, or -1 when it has no fixed size.</summary>
        private static int FixedSize(ushort inType, ushort length, int pointerSize) => inType switch
        {
            3 or 4 => 1,                        // Int8, UInt8
            5 or 6 => 2,                        // Int16, UInt16
            7 or 8 or 11 or 13 or 20 => 4,      // Int32, UInt32, Float, Boolean, HexInt32
            9 or 10 or 12 or 17 or 21 => 8,     // Int64, UInt64, Double, FILETIME, HexInt64
            15 or 18 => 16,                     // GUID, SYSTEMTIME
            16 => pointerSize,                  // Pointer
            14 when length > 0 => length,       // Binary with a fixed length (IPv6 addresses)
            _ => -1,
        };
    }
}
