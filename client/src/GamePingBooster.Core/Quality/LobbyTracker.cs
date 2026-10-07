namespace GamePingBooster.Core.Quality;

/// <summary>
/// One stretch of the game open with no match, on one way into the relay: every way's round trip to relayd over the
/// whole of it - the way in use by its pong, first, then the others by their Probes - and its last
/// <see cref="MaxTicks"/> quarter seconds as they were. <see cref="EndedBy"/>: "match" (the game started sending),
/// "moved" (another way or lane), "left" (nothing compared for <see cref="LobbyTracker.GapTicks"/>), or whatever the
/// caller passed to <see cref="LobbyTracker.Flush"/>.
/// </summary>
public sealed record LobbySummary(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int TickCount,
    string EndedBy,
    string Door,
    IReadOnlyList<string> DoorIds,
    IReadOnlyList<KeyValuePair<string, DoorStats>> Ways,
    IReadOnlyList<QualityTick> Ticks)
{
    /// <summary>Two minutes of quarter seconds: about 12 KB written with four other ways, under the server's 64 KB a record.</summary>
    public const int MaxTicks = 2 * 60 * SpikeDetector.TicksPerSecond;

    public double Seconds => TickCount * SpikeDetector.TickMs / 1000.0;

    /// <summary>Quarter seconds of the stretch before the first one in <see cref="Ticks"/>: a long lobby keeps only its end.</summary>
    public int TicksSkipped => TickCount - Ticks.Count;
}

/// <summary>
/// Cuts the settled ticks the switch policy judges into lobby stretches, for the lobby record (QualityFile.WriteLobby).
///
/// Entry switching compares the ways in during the lobby (QualityTick.Lobby) as in a match, but until 2026-10-07 none of
/// it was kept: spikes are written only in a match. So whether a way was steadily faster while a player waited in the
/// lobby - the moment a move costs nothing - could not be checked: the owner waited 30 s in the lobby on sg-2 that
/// evening and nothing could say what vn-5-sg2 measured meanwhile. Pure, like the policy: fed ticks, holds no clock.
/// </summary>
public sealed class LobbyTracker
{
    /// <summary>A lobby shorter than this is the walk from one match to the next queue, not a wait worth a record.</summary>
    public const int MinTicks = 20 * SpikeDetector.TicksPerSecond;

    /// <summary>Quarter seconds in a row that compared nothing, past which the lobby is over: a hole is one or two.</summary>
    public const int GapTicks = 2 * SpikeDetector.TicksPerSecond;

    private Stretch? _open;

    /// <summary>A stretch is being recorded: the caller takes what the record should say about the session now.</summary>
    public bool Open => _open is not null;

    private sealed class Stretch
    {
        public required string Door { get; init; }
        public required string[] DoorIds { get; init; }
        public required int Lane { get; init; }
        public required DateTimeOffset StartUtc { get; init; }
        public DateTimeOffset LastUtc;
        public int Ticks;
        public int Gap;
        public readonly Queue<QualityTick> Tail = new();
        public readonly List<double> Current = [];
        public int CurrentSent, CurrentLost;
        public required List<double>[] Other { get; init; }
        public required int[] OtherSent { get; init; }
        public required int[] OtherLost { get; init; }
    }

    /// <summary>
    /// One settled quarter second. Returns the stretch it ended - when it ended one at least <see cref="MinTicks"/>
    /// long - and whether it began a new one, so the caller knows when to take the session's labels.
    /// </summary>
    public (LobbySummary? Ended, bool Started) Feed(QualityTick tick)
    {
        if (!tick.Lobby || tick.DoorIds is not { } ids || tick.CurrentDoor is not { } door)
        {
            if (_open is not { } open) return (null, false);
            if (tick.Active) return (Flush("match"), false);
            return ++open.Gap > GapTicks ? (Flush("left"), false) : (null, false);
        }

        LobbySummary? ended = null;
        if (_open is { } was && (!string.Equals(was.Door, door, StringComparison.OrdinalIgnoreCase) ||
                                 !ReferenceEquals(was.DoorIds, ids) || was.Lane != tick.CurrentLane))
        {
            ended = Flush("moved");
        }

        var started = _open is null;
        var lobby = _open ??= new Stretch
        {
            Door = door,
            DoorIds = ids,
            Lane = tick.CurrentLane,
            StartUtc = tick.StartUtc,
            Other = ids.Select(_ => new List<double>()).ToArray(),
            OtherSent = new int[ids.Length],
            OtherLost = new int[ids.Length],
        };
        lobby.Gap = 0;
        lobby.Ticks++;
        lobby.LastUtc = tick.StartUtc;
        lobby.Tail.Enqueue(tick);
        while (lobby.Tail.Count > LobbySummary.MaxTicks) lobby.Tail.Dequeue();

        if (tick.RelayProcessSent)
        {
            lobby.CurrentSent++;
            if (tick.RelayProcessMs is { } pong) lobby.Current.Add(pong);
            else lobby.CurrentLost++;
        }
        if (tick.DoorSent is { } sent && tick.DoorMs is { } ms)
        {
            for (var slot = 0; slot < ids.Length && slot < sent.Length && slot < ms.Length; slot++)
            {
                if (!sent[slot]) continue;
                lobby.OtherSent[slot]++;
                if (ms[slot] is { } probe) lobby.Other[slot].Add(probe);
                else lobby.OtherLost[slot]++;
            }
        }
        return (ended, started);
    }

    /// <summary>Ends the stretch being recorded, if any. Null when there was none, or it was shorter than <see cref="MinTicks"/>.</summary>
    public LobbySummary? Flush(string endedBy)
    {
        var lobby = _open;
        _open = null;
        if (lobby is null || lobby.Ticks < MinTicks) return null;

        var ways = new List<KeyValuePair<string, DoorStats>>
        {
            new(lobby.Door, DoorSwitchPolicy.Stats(lobby.Current, lobby.CurrentSent, lobby.CurrentLost)),
        };
        for (var slot = 0; slot < lobby.DoorIds.Length; slot++)
        {
            ways.Add(new(lobby.DoorIds[slot], DoorSwitchPolicy.Stats(lobby.Other[slot], lobby.OtherSent[slot], lobby.OtherLost[slot])));
        }

        return new LobbySummary(lobby.StartUtc, lobby.LastUtc + TimeSpan.FromMilliseconds(SpikeDetector.TickMs), lobby.Ticks,
            endedBy, lobby.Door, lobby.DoorIds, ways, lobby.Tail.ToList());
    }
}
