namespace GamePingBooster.Core.Quality;

/// <summary>
/// Pings to a relay: how many went out and how many came back. Nothing more - the rule that reads it is
/// <see cref="RelayLoss"/>.
/// </summary>
public readonly record struct PingLoss(int Sent, int Answered)
{
    /// <summary>Nothing was measured. Never lossy, so a path measured without pings is compared as it always was.</summary>
    public static readonly PingLoss Unknown = new(0, 0);

    public int Lost => Math.Max(0, Sent - Answered);

    public double Share => Sent > 0 ? (double)Lost / Sent : 0;

    /// <summary>See <see cref="RelayLoss.IsLossy"/>.</summary>
    public bool IsLossy => RelayLoss.IsLossy(this);

    public override string ToString() => $"lost {Lost} of {Sent} pings ({Share * 100:F0}%)";
}

/// <summary>
/// When a path loses too many packets to be compared on its round trip alone, and what that costs it.
///
/// Until 2026-09-29 every relay comparison - connect, the region plan, the rescan between matches - ranked paths
/// on the round trip and nothing else. A median of eight echoes lets two of them go missing without a word, so a
/// relay that lost a quarter of everything still produced a number, and a small one. That evening the owner's line
/// (Viettel) lost 26% of its UDP into Da Nang and 35% into Ha Noi, both hosted on VNPT, while Ho Chi Minh and
/// Singapore lost nothing - counted with tcpdump on the relays, 300 datagrams each way. Connect chose Da Nang at
/// 13 ms, and the region plan kept Ho Chi Minh's region on it because vn-3 at 34 ms was not 5 ms faster than 38.
///
/// So a path that loses packets now carries a penalty of <see cref="PenaltyMs"/> in every comparison. A penalty
/// rather than a ban: when every path loses, the fastest of them still wins, and the tunnel still connects. And
/// big enough that a path losing a tenth of the game's packets never beats a clean one on speed alone - a game
/// hides 30 ms of ping far better than one packet in ten.
///
/// A threshold rather than a scale. A single lost ping out of sixteen happens on healthy lines, and a penalty that
/// grew with it would move players on that one ping; <see cref="MinLost"/> keeps the noise out, and the
/// hysteresis every comparison already has keeps a path that sits on the line from flapping.
/// </summary>
public static class RelayLoss
{
    /// <summary>Pings in one burst: enough that 26% loss shows as lossy 95 times in 100 (at most one lost: 5%).</summary>
    public const int BurstPings = 16;

    /// <summary>Between pairs of pings in a burst. On Windows the timer rounds this up to its tick, about 15 ms.</summary>
    public const int BurstSpacingMs = 10;

    /// <summary>After the last ping of a burst, how long an answer is waited for. A relay ping is a round trip to a
    /// machine already talking to us - it comes back quickly or not at all - so this is TunnelClient's ping timeout.</summary>
    public const int BurstWaitMs = 500;

    /// <summary>At least this many lost before a path is lossy: one lost ping is noise on any line.</summary>
    public const int MinLost = 2;

    /// <summary>And at least this share of what was sent.</summary>
    public const double LossyShare = 0.10;

    /// <summary>What a lossy path adds to its round trip when paths are compared.</summary>
    public const double PenaltyMs = 100;

    /// <summary>The window a live tunnel's loss is read over: keepalives go once a second.</summary>
    public const int LiveWindowSeconds = 60;

    public static bool IsLossy(PingLoss loss) =>
        loss.Sent > 0 && loss.Lost >= MinLost && loss.Lost >= LossyShare * loss.Sent;

    /// <summary>The number a path is compared on: its round trip, plus <see cref="PenaltyMs"/> when it loses packets.</summary>
    public static double Score(double ms, PingLoss loss) => IsLossy(loss) ? ms + PenaltyMs : ms;

    /// <summary>The same, for a caller that already knows whether the path is lossy.</summary>
    public static double Score(double ms, bool lossy) => lossy ? ms + PenaltyMs : ms;

    /// <summary>" (lost 4 of 16 pings (25%))" for the log when anything was lost, "" otherwise.</summary>
    public static string Note(PingLoss loss) => loss.Lost > 0 ? $" ({loss})" : "";
}
