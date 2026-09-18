using System.Buffers;
using System.Text.Json;
using GamePingBooster.Core.Quality;

namespace GamePingBooster.Service.Tunnel;

/// <summary>What a record says about the session it came from. No addresses, by construction.</summary>
internal readonly record struct QualityMeta(
    string? AppVersion,
    string? GameId,
    string? RelayId,
    string? EntryId,
    string? RegionName,
    string? LinkType);

/// <summary>
/// A move to another relay between matches and how the next match went, for <see cref="QualityFile.WriteRelayMove"/>.
/// <paramref name="Reason"/> is "rescan" for a faster relay, "game" for leaving one not set to carry the game.
/// <paramref name="FollowEnded"/> says why following stopped: "held", "no-match" (none within ten minutes),
/// "tunnel-replaced" or "disconnected".
/// </summary>
internal sealed record RelayMoveRecord(
    string Reason,
    DateTimeOffset AtUtc,
    string From,
    string To,
    double MeasureSeconds,
    double SilenceSeconds,
    DoorStats FromStats,
    DoorStats ToStats,
    double? NextMatchAfterSeconds,
    bool? StillSendingAfter90s,
    string FollowEnded);

/// <summary>
/// The spike recorder's record: one JSON object per line, one file per day, under
/// %ProgramData%\GamePingBooster\quality.
///
/// Two kinds of line. <c>"type":"spike"</c> is one spike with a verdict, its excess over normal on
/// every signal, and ten seconds of quarter-second samples either side so the verdict can be checked
/// by eye. <c>"type":"match"</c> is one match with how many spikes it had and what normal looked
/// like - the denominator, without which "eight spikes on sg-3" cannot be told from "sg-3 is busy".
/// <c>"type":"switch"</c> is one decision of entry switching and the minute after it - see WriteMove.
/// Spikes also carry <c>door</c>, the way into the relay in use, and <c>ticks.doors</c>, the probes down
/// the others, when entry switching is not off.
///
/// In the sample arrays null means the probe was not sent and -1 means it was sent and never
/// answered. Those are different findings and the file keeps them apart. <c>downPackets</c> and
/// <c>upPackets</c> are plain counts per quarter second, added within schema 2 - a reader must not
/// expect them on records from before.
///
/// Schema 2 added the home router (A0), <c>silenceMs</c> and <c>lowConfidence</c>, and changed what
/// counts as a stall - see SpikeDetector. Do not tally schema 1 "pc" and "game-server" spikes with
/// schema 2 ones: schema 1 counted loading screens and match ends.
///
/// THIS IS WHAT A LATER VERSION WILL UPLOAD, so what it leaves out is decided here: no IP address of
/// any kind - not the router's, not the relay's, not the game server's - and nothing else from the
/// player's own network. The relay is named by its id, the game by its id.
///
/// Local and bounded: fourteen days kept, eight megabytes a day at most. A write that fails is logged
/// once and dropped; a diagnostic file is never a reason for the service to misbehave.
/// </summary>
internal sealed class QualityFile(Action<string> log)
{
    public const int Schema = 2;
    private const int KeepDays = 14;
    private const long MaxBytesPerDay = 8L * 1024 * 1024;

    internal static string DirectoryPath => Path.Combine(ServiceConfig.DefaultDirectory, "quality");

    private bool _warned;
    private bool _warnedFull;
    private bool _warnedDropped;
    private string? _prunedFor;

    public void WriteSpike(SpikeEvent spike, QualityMeta meta) => Append((w, id) =>
    {
        w.WriteStartObject();
        w.WriteString("id", id);
        w.WriteString("type", "spike");
        w.WriteNumber("schema", Schema);
        w.WriteString("utc", spike.StartUtc);
        WriteMeta(w, meta);

        w.WriteString("verdict", spike.Verdict);
        w.WriteBoolean("lowConfidence", spike.LowConfidence);
        String(w, "lowConfidenceReason", spike.LowConfidenceReason);
        w.WriteNumber("maxLocalLagMs", Round(spike.MaxLocalLagMs));
        w.WriteBoolean("sustained", spike.Sustained);
        w.WriteNumber("durationMs", Round(spike.DurationMs));
        w.WriteString("measured", Name(spike.Measured));
        // The way into the relay the tunnel was on: the relay's id, or an entry's. Null when entry switching is off.
        String(w, "door", spike.Ticks.LastOrDefault(t => t.CurrentDoor is not null)?.CurrentDoor);
        w.WriteNumber("peakExcessMs", Round(spike.PeakExcessMs));
        w.WriteNumber("silenceMs", Round(spike.SilenceMs));

        w.WriteStartObject("excessMs");
        w.WriteNumber("gateway", Round(spike.GatewayExcessMs));
        w.WriteNumber("relayWire", Round(spike.RelayWireExcessMs));
        w.WriteNumber("relayProcess", Round(spike.RelayProcessExcessMs));
        w.WriteNumber("datacentre", Round(spike.DatacentreExcessMs));
        w.WriteNumber("serverPath", Round(spike.ServerPathExcessMs));
        w.WriteEndObject();

        w.WriteStartObject("lost");
        w.WriteNumber("gateway", spike.GatewayLost);
        w.WriteNumber("relayWire", spike.RelayWireLost);
        w.WriteNumber("relayProcess", spike.RelayProcessLost);
        w.WriteNumber("datacentre", spike.DatacentreLost);
        w.WriteNumber("serverPath", spike.ServerPathLost);
        w.WriteEndObject();

        w.WriteNumber("maxDownGapMs", Round(spike.MaxDownGapMs));
        w.WriteNumber("maxUpGapMs", Round(spike.MaxUpGapMs));
        Number(w, "carriedToRelay", spike.CarriedToRelay, 2);
        Number(w, "carriedToWire", spike.CarriedToWire, 2);
        Number(w, "carriedToGateway", spike.CarriedToGateway, 2);
        Number(w, "lineDownMbps", spike.LineDownMbps, 1);
        Number(w, "lineUpMbps", spike.LineUpMbps, 1);
        w.WriteBoolean("busyLine", spike.BusyLine);

        w.WriteStartObject("baseline");
        Number(w, "gateway", spike.Baseline.GatewayMs, 1);
        Number(w, "relayWire", spike.Baseline.RelayWireMs, 1);
        Number(w, "relayProcess", spike.Baseline.RelayProcessMs, 1);
        Number(w, "datacentre", spike.Baseline.DatacentreMs, 1);
        Number(w, "serverPath", spike.Baseline.ServerPathMs, 1);
        Number(w, "downGap", spike.Baseline.DownGapMs, 1);
        Number(w, "upGap", spike.Baseline.UpGapMs, 1);
        w.WriteEndObject();

        w.WriteNumber("tickMs", SpikeDetector.TickMs);
        w.WriteNumber("eventFrom", spike.EventFrom);
        w.WriteNumber("eventTo", spike.EventTo);

        w.WriteStartObject("ticks");
        Series(w, "gateway", spike.Ticks, t => Probe(t.GatewaySent, t.GatewayMs));
        Series(w, "relayWire", spike.Ticks, t => Probe(t.RelayWireSent, t.RelayWireMs));
        Series(w, "relayProcess", spike.Ticks, t => Probe(t.RelayProcessSent, t.RelayProcessMs));
        Series(w, "datacentre", spike.Ticks, t => Probe(t.DatacentreSent, t.DatacentreMs));
        Series(w, "serverPath", spike.Ticks, t => Probe(t.ServerPathSent, t.ServerPathMs));
        Series(w, "downGap", spike.Ticks, t => t.DownPackets > 0 ? t.DownGapMs : null);
        Series(w, "upGap", spike.Ticks, t => t.UpPackets > 0 ? t.UpGapMs : null);
        // The counts behind the two gap series. A stall only counts inside a steady stream, and whether
        // a stream was steady - or a loading screen sending in bursts - cannot be read from gaps alone.
        Counts(w, "downPackets", spike.Ticks, t => t.DownPackets);
        Counts(w, "upPackets", spike.Ticks, t => t.UpPackets);
        // The other ways into the relay, by id: the question an incident on the current way raises is
        // whether moving would have helped, and this is the answer for that spike.
        var doorIds = spike.Ticks
            .Where(t => t.DoorIds is not null)
            .SelectMany(t => t.DoorIds!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (doorIds.Count > 0)
        {
            w.WriteStartObject("doors");
            foreach (var door in doorIds) Series(w, door, spike.Ticks, t => DoorValue(t, door));
            w.WriteEndObject();
        }
        Series(w, "localLag", spike.Ticks, t => t.LocalLagMs);
        w.WriteStartArray("active");
        foreach (var tick in spike.Ticks) w.WriteNumberValue(tick.Active ? 1 : 0);
        w.WriteEndArray();
        w.WriteEndObject();

        w.WriteEndObject();
    });

    public void WriteMatch(MatchSummary match, QualityMeta meta) => Append((w, id) =>
    {
        w.WriteStartObject();
        w.WriteString("id", id);
        w.WriteString("type", "match");
        w.WriteNumber("schema", Schema);
        w.WriteString("startUtc", match.StartUtc);
        w.WriteString("endUtc", match.EndUtc);
        WriteMeta(w, meta);

        w.WriteNumber("activeSeconds", Round(match.ActiveSeconds));
        w.WriteNumber("spikes", match.Spikes);
        w.WriteNumber("lowConfidenceSpikes", match.LowConfidenceSpikes);
        w.WriteStartObject("spikesByVerdict");
        foreach (var (verdict, count) in match.SpikesByVerdict) w.WriteNumber(verdict, count);
        w.WriteEndObject();

        w.WriteStartObject("datacentre");
        Number(w, "p50", match.DatacentreP50, 1);
        Number(w, "p95", match.DatacentreP95, 1);
        w.WriteNumber("sent", match.DatacentreSent);
        w.WriteNumber("lost", match.DatacentreLost);
        w.WriteEndObject();

        w.WriteStartObject("relayProcess");
        Number(w, "p50", match.RelayProcessP50, 1);
        Number(w, "p95", match.RelayProcessP95, 1);
        w.WriteNumber("sent", match.RelayProcessSent);
        w.WriteNumber("lost", match.RelayProcessLost);
        w.WriteEndObject();

        w.WriteNumber("maxDownGapMs", Round(match.MaxDownGapMs));
        w.WriteEndObject();
    });

    /// <summary>
    /// One decision of the entry-switching policy and the minute after it: <c>"type":"switch"</c>.
    ///
    /// Written in both modes. With moves off it is the evidence for turning them on - whether the other way
    /// stayed clean while the current one was bad. With moves on it is the check that they helped: "after"
    /// holds both ways over the following minute, and "moved" whether the tunnel really went.
    /// </summary>
    public void WriteMove(DoorDecision decision, bool movesEnabled, bool requested, bool moved, double followedSeconds,
        DoorStats afterFrom, DoorStats afterTo, QualityMeta meta) => Append((w, id) =>
    {
        w.WriteStartObject();
        w.WriteString("id", id);
        w.WriteString("type", "switch");
        w.WriteNumber("schema", Schema);
        w.WriteString("utc", decision.AtUtc);
        WriteMeta(w, meta);

        w.WriteString("mode", movesEnabled ? "on" : "record");
        w.WriteBoolean("requested", requested);
        w.WriteBoolean("moved", moved);
        w.WriteString("from", decision.From);
        w.WriteString("to", decision.To);
        w.WriteString("reason", decision.Return ? "return" : "worse");
        w.WriteNumber("windowSeconds", decision.WindowTicks / SpikeDetector.TicksPerSecond);
        w.WriteNumber("worseShare", Math.Round(decision.WorseShare, 2));
        w.WriteNumber("comparable", decision.Comparable);

        w.WriteStartObject("before");
        DoorFigures(w, "from", decision.FromStats);
        DoorFigures(w, "to", decision.ToStats);
        w.WriteEndObject();

        w.WriteNumber("afterSeconds", Round(followedSeconds));
        w.WriteStartObject("after");
        DoorFigures(w, "from", afterFrom);
        DoorFigures(w, "to", afterTo);
        w.WriteEndObject();

        w.WriteEndObject();
    });

    /// <summary>
    /// A move to another relay made between matches, and what the next match did after it - see
    /// TunnelEngine.RescanBetweenMatchesAsync. Written as a <c>"switch"</c> with <c>"reason":"rescan"</c> so it
    /// lands beside entry switching's moves on the admin page with no change there. <c>before</c> is each path's
    /// median to the region's landmark when the choice was made; there is no <c>after</c> for the path left,
    /// whose session ended with the move. <c>nextMatch</c> is the question the move is judged by: did the
    /// game start sending on the new relay, how soon, and was it still sending 90 s later - a match that
    /// failed to join stops.
    /// </summary>
    public void WriteRelayMove(RelayMoveRecord move, QualityMeta meta) => Append((w, id) =>
    {
        w.WriteStartObject();
        w.WriteString("id", id);
        w.WriteString("type", "switch");
        w.WriteNumber("schema", Schema);
        w.WriteString("utc", move.AtUtc);
        WriteMeta(w, meta);

        w.WriteString("mode", "on");
        w.WriteBoolean("requested", true);
        w.WriteBoolean("moved", true);
        w.WriteString("from", move.From);
        w.WriteString("to", move.To);
        w.WriteString("reason", move.Reason);
        w.WriteNumber("windowSeconds", Round(move.MeasureSeconds));
        w.WriteNumber("worseShare", 0);
        w.WriteNumber("silenceSeconds", Round(move.SilenceSeconds));

        w.WriteStartObject("before");
        DoorFigures(w, "from", move.FromStats);
        DoorFigures(w, "to", move.ToStats);
        w.WriteEndObject();

        w.WriteStartObject("nextMatch");
        Number(w, "startedAfterSeconds", move.NextMatchAfterSeconds, 1);
        if (move.StillSendingAfter90s is { } held) w.WriteBoolean("stillSendingAfter90s", held);
        else w.WriteNull("stillSendingAfter90s");
        w.WriteString("followEnded", move.FollowEnded);
        w.WriteEndObject();

        w.WriteEndObject();
    });

    private static void DoorFigures(Utf8JsonWriter w, string name, DoorStats stats)
    {
        w.WriteStartObject(name);
        Number(w, "p50", stats.P50, 1);
        Number(w, "p95", stats.P95, 1);
        w.WriteNumber("sent", stats.Sent);
        w.WriteNumber("lost", stats.Lost);
        w.WriteEndObject();
    }

    /// <summary>One other way's probe in one quarter second, in the sample-array convention: null not sent, -1 lost.</summary>
    private static double? DoorValue(QualityTick tick, string door)
    {
        if (tick.DoorIds is not { } ids || tick.DoorSent is not { } sent || tick.DoorMs is not { } ms) return null;
        var slot = Array.FindIndex(ids, id => string.Equals(id, door, StringComparison.OrdinalIgnoreCase));
        return slot < 0 ? null : Probe(sent[slot], ms[slot]);
    }

    private static void WriteMeta(Utf8JsonWriter w, QualityMeta meta)
    {
        String(w, "app", meta.AppVersion);
        String(w, "game", meta.GameId);
        // The relayd the traffic exits through - also on a path through an entry, so every spike on one
        // relay groups together - and the entry separately, so paths through one can be compared.
        String(w, "relay", meta.RelayId);
        String(w, "entry", meta.EntryId);
        String(w, "region", meta.RegionName);
        String(w, "link", meta.LinkType);
    }

    /// <summary>
    /// Writes one record: into the upload queue, then into today's local file. The id is random and
    /// is how the licence server recognises a record it already has, so a retried upload never counts
    /// one twice.
    /// </summary>
    private void Append(Action<Utf8JsonWriter, string> write)
    {
        var id = Guid.NewGuid().ToString("N");
        var buffer = new ArrayBufferWriter<byte>(8192);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer, id);
        }

        // Queued first and on its own terms: the day's local file has a size cap, the queue has its
        // own, and a full diary is no reason to stop sending.
        var dropped = QualityOutbox.Add(id, buffer.WrittenSpan);
        if (dropped > 0 && !_warnedDropped)
        {
            // Once per connection. The queue is full because nothing has been uploading - signed out, or
            // the app not running - and the analysis on the server will be missing the oldest records.
            _warnedDropped = true;
            log($"The connection-quality queue is full ({QualityOutbox.MaxFiles} records waiting): the oldest are " +
                "being dropped. The app uploads them while signed in and running.");
        }

        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (_prunedFor != today)
            {
                Prune();
                _prunedFor = today;
                _warnedFull = false;
            }

            var path = Path.Combine(DirectoryPath, $"quality-{today}.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length >= MaxBytesPerDay)
            {
                if (!_warnedFull)
                {
                    _warnedFull = true;
                    log($"The spike record for {today} reached {MaxBytesPerDay / (1024 * 1024)} MB - nothing more is written today.");
                }
                return;
            }

            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(buffer.WrittenSpan);
            stream.WriteByte((byte)'\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_warned)
            {
                _warned = true;
                log($"Could not write the spike record ({ex.Message}). Spikes are still logged here.");
            }
        }
    }

    private static void Prune()
    {
        var cutoff = DateTime.Now.AddDays(-KeepDays);
        foreach (var file in Directory.GetFiles(DirectoryPath, "quality-*.jsonl"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
            catch (IOException)
            {
                // In use or already gone; the next day's prune tries again.
            }
        }
    }

    private static double? Probe(bool sent, double? value) => !sent ? null : value ?? -1;

    private static double Round(double value) => Math.Round(value, 1);

    private static void Number(Utf8JsonWriter w, string name, double? value, int digits)
    {
        if (value is { } v) w.WriteNumber(name, Math.Round(v, digits));
        else w.WriteNull(name);
    }

    private static void String(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }

    private static void Series(Utf8JsonWriter w, string name, IReadOnlyList<QualityTick> ticks, Func<QualityTick, double?> select)
    {
        w.WriteStartArray(name);
        foreach (var tick in ticks)
        {
            if (select(tick) is { } v) w.WriteNumberValue(Math.Round(v, 1));
            else w.WriteNullValue();
        }
        w.WriteEndArray();
    }

    private static void Counts(Utf8JsonWriter w, string name, IReadOnlyList<QualityTick> ticks, Func<QualityTick, int> select)
    {
        w.WriteStartArray(name);
        foreach (var tick in ticks) w.WriteNumberValue(select(tick));
        w.WriteEndArray();
    }

    private static string Name(QualitySignal signal) => signal switch
    {
        QualitySignal.Gateway => "gateway",
        QualitySignal.RelayWire => "relayWire",
        QualitySignal.RelayProcess => "relayProcess",
        QualitySignal.Datacentre => "datacentre",
        QualitySignal.ServerPath => "serverPath",
        QualitySignal.DownGap => "downGap",
        QualitySignal.UpGap => "upGap",
        _ => "unknown",
    };
}
