using GamePingBooster.Core.Quality;

namespace GamePingBooster.QualityCheck;

/// <summary>
/// Drives the spike detector with synthetic quarter seconds and checks every verdict it can reach.
///
/// A healthy connection only ever reaches one of them - "no spike" - so the branches that matter,
/// the ones a player's report will be judged by, are reachable only from here. Run it after touching
/// SpikeDetector:
///
///     dotnet run --project client/src/GamePingBooster.QualityCheck
///
/// A plain console program for the same reason ProtocolCheck is one: this repository carries no test
/// framework, and a list of assertions is not worth a dependency.
///
/// The synthetic path is a Vietnamese line to Singapore as measured: the router at 3 ms, the relay's
/// kernel at 42, relayd a millisecond behind it, the datacentre another millisecond on, a game server
/// sending about fifty packets a second, and a millisecond and a half of jitter on everything. The
/// scenarios marked 2026-09-15 replay shapes from the first real session, where the first version
/// of the detector got them wrong.
/// </summary>
internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        CalmSessionHasNoSpikes();
        SpikeOnTheWayToTheRelay();
        SpikeInTheHomeNetwork();
        RouterThatIgnoresPingsLeavesItOnTheWayToTheRelay();
        SpikeAtTheRelay();
        SpikePastTheRelay();
        LossPastTheRelay();
        LossOnlyOfProbesIsLowConfidence();
        LossOnTheWayToTheRelay();
        ServerSilenceWithAProbeLostIsUnclear();
        RelaySpikeWhileThePcWasBusyIsLowConfidence();
        ServerSilenceWhilePathsAreClean();
        ServerSilenceWithoutADatacentreProbeIsLowConfidence();
        RelayChangeIsNotASpike();
        RouterDroppingPingsIsNotTheHomeNetwork();
        RelayDroppingPingsIsNotTheRoute();
        SustainedDegradationIsMarked();
        MatchCountsLowConfidenceApart();
        ShortServerSilenceIsLowConfidence();
        SilenceFromThisPc();
        OneSmallTickIsNotASpike();
        OneLargeTickIsASpike();
        BouncesCloseTogetherAreOneSpike();
        WithoutALandmarkRelaydDecides();
        NothingBeforeThereIsANormal();
        MatchEndingIsNotASpike();
        LastStragglerIsNotAStall();
        LoadingScreenIsNotAStall();
        CadenceChangeIsNotAStall();
        BusyLineIsFlagged();
        MatchSummaryCountsSpikes();

        Console.WriteLine();
        Console.WriteLine("Moving between ways into one relay:");
        RouteToTheRelayBadWhileTheEntryIsCleanMoves();
        AFiveSecondSpikeDoesNotMove();
        EveryWaySlowTogetherDoesNotMove();
        SteadyLossOnTheCurrentWayMoves();
        OneShortOutageDoesNotMove();
        AnOlderRelayThatNeverAnswersProbesIsNeverAChoice();
        AnEntryLosingProbesIsNotClean();
        OnlyAClearDifferenceMoves();
        NothingMovesAgainForFiveMinutes();
        ADifferentWayInStartsTheWindowAgain();
        TheBetterOfTwoWaysIsChosen();
        TheWayLeftIsReturnedToOnceItRecovers();
        AWayLeftThatStillLosesProbesIsNotReturnedTo();
        AWayLeftThatIsOnlyAsFastIsNotReturnedTo();
        AWayLeftWithTheWorseTailIsNotReturnedTo();
        NothingIsReturnedToWhenTheMoveWasNotMade();
        AMoveToAFasterWayDoesNotBounceBack();
        TheLobbyIsJudgedToo();
        ADetourTakenAtConnectIsLeftOnceTheRoadRecovers();

        Console.WriteLine();
        Console.WriteLine("Between matches:");
        AMatchEndingOpensOneGap();
        AResultScreenTrickleIsNotAGap();
        ALobbyBeforeAnyMatchIsNotAGap();
        AMatchKeepsItsGapClosed();
        TheGapIsTimedFromTheLastPacket();
        RescanComparesMediansByAClearMargin();

        Console.WriteLine();
        if (_failures == 0)
        {
            Console.WriteLine("All spike detector checks passed.");
            return 0;
        }
        Console.WriteLine($"{_failures} spike detector check(s) FAILED.");
        return 1;
    }

    // ------------------------------------------------------------------ scenarios

    private static void CalmSessionHasNoSpikes()
    {
        var s = new Session();
        s.Calm(4 * 120);
        s.Finish();
        Check("a calm two minutes has no spikes", s.Events.Count == 0, Got(s));
    }

    private static void SpikeOnTheWayToTheRelay()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(8, () => s.Tick(wire: 40, process: 40, datacentre: 40));
        s.Finish();
        ExpectOne("+40 ms from the relay's wire outwards, router flat, is on the way to the relay", s, SpikeVerdicts.Relay);
        if (s.Events.Count == 1)
        {
            var e = s.Events[0];
            Check("  ...measured on the datacentre echo", e.Measured == QualitySignal.Datacentre, e.Measured.ToString());
            Check("  ...peak excess is about 40 ms", e.PeakExcessMs is > 36 and < 44, $"{e.PeakExcessMs:F1}");
            Check("  ...lasts two seconds", Math.Abs(e.DurationMs - 2000) < 1, $"{e.DurationMs}");
            Check("  ...carries context either side", e.EventFrom == 40 && e.Ticks.Count > e.EventTo + 1,
                $"from {e.EventFrom}, to {e.EventTo}, of {e.Ticks.Count}");
            Check("  ...is not low confidence", !e.LowConfidence, "LowConfidence true");
        }
    }

    private static void SpikeInTheHomeNetwork()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(8, () => s.Tick(gateway: 40, wire: 40, process: 40, datacentre: 40));
        s.Finish();
        ExpectOne("the router rising with everything past it is the home network", s, SpikeVerdicts.Router);
    }

    private static void RouterThatIgnoresPingsLeavesItOnTheWayToTheRelay()
    {
        var s = new Session { Gateway = false };
        s.Calm(240);
        s.Repeat(8, () => s.Tick(wire: 40, process: 40, datacentre: 40));
        s.Finish();
        ExpectOne("with a router that does not answer, the verdict stays on the way to the relay", s, SpikeVerdicts.Relay);
    }

    private static void SpikeAtTheRelay()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(8, () => s.Tick(process: 40, datacentre: 40));
        s.Finish();
        ExpectOne("relayd and the datacentre up, the wire flat, is the relay itself", s, SpikeVerdicts.RelayProcess);
        if (s.Events.Count == 1)
        {
            Check("  ...and is not low confidence while the PC was idle", !s.Events[0].LowConfidence, $"{s.Events[0].LowConfidenceReason}");
        }
    }

    private static void SpikePastTheRelay()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(8, () => s.Tick(datacentre: 40));
        s.Finish();
        ExpectOne("only the datacentre echo up is past the relay", s, SpikeVerdicts.Game);
    }

    private static void LossPastTheRelay()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(3, () => s.Tick(loseDatacentre: true));
        s.Feed(s.Tick(loseDatacentre: true, downGap: 400));
        s.Finish();
        ExpectOne("datacentre echoes lost while relayd answers, felt by the game, is past the relay", s, SpikeVerdicts.Game);
        if (s.Events.Count == 1)
        {
            Check("  ...and is not low confidence", !s.Events[0].LowConfidence, $"{s.Events[0].LowConfidenceReason}");
        }
    }

    private static void LossOnlyOfProbesIsLowConfidence()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(4, () => s.Tick(loseDatacentre: true));
        s.Finish();
        ExpectOne("datacentre echoes lost while the game's packets flow on time", s, SpikeVerdicts.Game);
        if (s.Events.Count == 1)
        {
            Check("  ...is low confidence: only probes were lost",
                s.Events[0].LowConfidenceReason == LowConfidenceReasons.LossNotFelt, $"{s.Events[0].LowConfidenceReason}");
        }
    }

    private static void LossOnTheWayToTheRelay()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(3, () => s.Tick(loseWire: true, loseProcess: true, loseDatacentre: true));
        s.Feed(s.Tick(loseWire: true, loseProcess: true, loseDatacentre: true, downGap: 900));
        s.Finish();
        ExpectOne("everything past the router lost at once is on the way to the relay", s, SpikeVerdicts.Relay);
        if (s.Events.Count == 1)
        {
            Check("  ...and is not low confidence", !s.Events[0].LowConfidence, $"{s.Events[0].LowConfidenceReason}");
        }
    }

    private static void ServerSilenceWithAProbeLostIsUnclear()
    {
        var s = new Session();
        s.Calm(240);
        s.Feed(s.Tick(downPackets: 0, loseProcess: true, loseDatacentre: true));
        s.Feed(s.Tick(downPackets: 0));
        s.Feed(s.Tick(downGap: 600));
        s.Finish();
        ExpectOne("a server silence while a probe on the path was lost is not placed on the server", s, SpikeVerdicts.Unclear);
        if (s.Events.Count == 1)
        {
            Check("  ...and says why",
                s.Events[0].LowConfidenceReason == LowConfidenceReasons.NetworkDuringSilence, $"{s.Events[0].LowConfidenceReason}");
        }
    }

    private static void RelaySpikeWhileThePcWasBusyIsLowConfidence()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(8, () => s.Tick(process: 40, datacentre: 40, localLag: 120));
        s.Finish();
        ExpectOne("relayd and the datacentre late while this PC was busy", s, SpikeVerdicts.RelayProcess);
        if (s.Events.Count == 1)
        {
            Check("  ...is low confidence: the PC was busy",
                s.Events[0].LowConfidenceReason == LowConfidenceReasons.PcBusy, $"{s.Events[0].LowConfidenceReason}");
        }
    }

    private static void ServerSilenceWhilePathsAreClean()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(2, () => s.Tick(downPackets: 0));
        s.Feed(s.Tick(downGap: 600));
        s.Finish();
        ExpectOne("600 ms of silence from the server with clean probes is the game server", s, SpikeVerdicts.GameServer);
        if (s.Events.Count == 1)
        {
            Check("  ...lasts as long as the silence", Math.Abs(s.Events[0].SilenceMs - 600) < 1, $"{s.Events[0].SilenceMs}");
            Check("  ...is not low confidence", !s.Events[0].LowConfidence, "LowConfidence true");
        }
    }

    private static void ShortServerSilenceIsLowConfidence()
    {
        // 2026-09-15, 15:12:24: 250 ms from the server, every probe flat.
        var s = new Session();
        s.Calm(240);
        s.Feed(s.Tick(downGap: 250));
        s.Finish();
        ExpectOne("250 ms of silence from the server is the game server", s, SpikeVerdicts.GameServer);
        if (s.Events.Count == 1)
        {
            Check("  ...but short enough to be low confidence",
                s.Events[0].LowConfidenceReason == LowConfidenceReasons.ShortSilence, $"{s.Events[0].LowConfidenceReason}");
        }
    }

    private static void ServerSilenceWithoutADatacentreProbeIsLowConfidence()
    {
        // VALORANT: no landmark, so nothing measures the route from the relay to the game.
        var s = new Session { Landmark = false };
        s.Calm(240);
        s.Repeat(2, () => s.Tick(downPackets: 0));
        s.Feed(s.Tick(downGap: 600));
        s.Finish();
        ExpectOne("a server silence in a game with no datacentre probe", s, SpikeVerdicts.GameServer);
        if (s.Events.Count == 1)
        {
            Check("  ...is low confidence: the route past the relay is unmeasured",
                s.Events[0].LowConfidenceReason == LowConfidenceReasons.NoDatacentreProbe, $"{s.Events[0].LowConfidenceReason}");
        }
    }

    private static void RouterDroppingPingsIsNotTheHomeNetwork()
    {
        // The ISP route slows down while the home router, which answers a ping or two a second, drops ours.
        var s = new Session();
        s.Calm(240);
        s.Repeat(8, () => s.Tick(wire: 40, process: 40, datacentre: 40, loseGateway: true));
        s.Finish();
        ExpectOne("a slow ISP route while the router drops pings stays on the way to the relay", s, SpikeVerdicts.Relay);
    }

    private static void RelayDroppingPingsIsNotTheRoute()
    {
        // relayd slow while the relay's kernel pings are dropped: still the relay, not the route to it.
        var s = new Session();
        s.Calm(240);
        s.Repeat(8, () => s.Tick(process: 40, datacentre: 40, loseWire: true));
        s.Finish();
        ExpectOne("a slow relayd while its ICMP is dropped stays at the relay", s, SpikeVerdicts.RelayProcess);
    }

    private static void SustainedDegradationIsMarked()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(300, () => s.Tick(wire: 40, process: 40, datacentre: 40));
        s.Calm(40);
        s.Finish();
        Check("ping 40 ms higher for 75 s is reported", s.Events.Count >= 1, Got(s));
        if (s.Events.Count >= 1)
        {
            Check("  ...and the first part is marked sustained", s.Events[0].Sustained, "Sustained false");
            Check("  ...and a normal short spike is not", !s.Events[^1].Sustained || s.Events.Count == 1, "last also sustained");
        }
    }

    private static void MatchCountsLowConfidenceApart()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(4, () => s.Tick(loseDatacentre: true));
        s.Calm(80);
        s.Repeat(8, () => s.Tick(wire: 40, process: 40, datacentre: 40));
        s.Finish();
        var summary = s.Detector.EndMatch();
        Check("the match summary counts two spikes, one of them low confidence",
            summary is { Spikes: 2, LowConfidenceSpikes: 1 }, $"{summary?.Spikes} / {summary?.LowConfidenceSpikes}");
    }

    private static void RelayChangeIsNotASpike()
    {
        // A failover from a 43 ms relay to one 27 ms further away, with the detector told the path changed.
        var s = new Session();
        s.Calm(240);
        s.Events.AddRange(s.Detector.Restart());
        s.Repeat(240, () => s.Tick(wire: 27, process: 27, datacentre: 27));
        s.Finish();
        Check("a relay change followed by a restart of normal is not a spike", s.Events.Count == 0, Got(s));
    }

    private static void SilenceFromThisPc()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(2, () => s.Tick(upPackets: 0));
        // A PC that froze also answers relayd late - the verdict must still be the PC.
        s.Feed(s.Tick(upGap: 700, process: 30, datacentre: 30));
        s.Finish();
        ExpectOne("700 ms of silence from this PC is the PC", s, SpikeVerdicts.Pc);
    }

    private static void OneSmallTickIsNotASpike()
    {
        var s = new Session();
        s.Calm(240);
        s.Feed(s.Tick(datacentre: 18));
        s.Finish();
        Check("a single quarter second 18 ms over normal is not a spike", s.Events.Count == 0, Got(s));
    }

    private static void OneLargeTickIsASpike()
    {
        var s = new Session();
        s.Calm(240);
        s.Feed(s.Tick(wire: 50, process: 50, datacentre: 50));
        s.Finish();
        ExpectOne("a single quarter second 50 ms over normal is a spike", s, SpikeVerdicts.Relay);
    }

    private static void BouncesCloseTogetherAreOneSpike()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(3, () => s.Tick(wire: 40, process: 40, datacentre: 40));
        s.Calm(4);
        s.Repeat(3, () => s.Tick(wire: 40, process: 40, datacentre: 40));
        s.Finish();
        ExpectOne("two bursts a second apart are one spike", s, SpikeVerdicts.Relay);
    }

    private static void WithoutALandmarkRelaydDecides()
    {
        var s = new Session { Landmark = false };
        s.Calm(240);
        s.Repeat(8, () => s.Tick(wire: 40, process: 40));
        s.Finish();
        ExpectOne("with no landmark, relayd and the wire up is on the way to the relay", s, SpikeVerdicts.Relay);
        if (s.Events.Count == 1)
        {
            Check("  ...measured on relayd", s.Events[0].Measured == QualitySignal.RelayProcess, s.Events[0].Measured.ToString());
        }
    }

    private static void NothingBeforeThereIsANormal()
    {
        var s = new Session();
        s.Calm(20);
        s.Repeat(8, () => s.Tick(wire: 40, process: 40, datacentre: 40));
        s.Finish();
        Check("no verdict in the first five seconds, before there is a normal", s.Events.Count == 0, Got(s));
    }

    private static void MatchEndingIsNotASpike()
    {
        var s = new Session();
        s.Calm(240);
        // The server stops. Probes carry on for five seconds, then the recorder goes idle. The
        // silence never ends, so it is never recorded as a gap.
        s.Repeat(20, () => s.Tick(downPackets: 0, upPackets: 0));
        s.Repeat(40, () => s.Idle());
        s.Finish();
        Check("a match ending is not a spike", s.Events.Count == 0, Got(s));
    }

    private static void LastStragglerIsNotAStall()
    {
        // 2026-09-15, 15:25:43: one last packet 236 ms after the one before, then nothing - reported "pc".
        var s = new Session();
        s.Calm(240);
        s.Feed(s.Tick(upGap: 236));
        s.Repeat(20, () => s.Tick(downPackets: 0, upPackets: 0));
        s.Repeat(40, () => s.Idle());
        s.Finish();
        Check("a last packet straggling out before the stream stops is not a stall", s.Events.Count == 0, Got(s));
    }

    private static void LoadingScreenIsNotAStall()
    {
        // 2026-09-15, 15:15:28: the client sending in bursts seconds apart while a match loaded,
        // reported as a 3.2 s "pc" stall. The shape of the upGap column, tick by tick.
        var s = new Session();
        s.Calm(240);
        s.Feed(s.Tick(upPackets: 0));
        s.Feed(s.Tick(upGap: 1796));
        s.Feed(s.Tick(upGap: 34.9));
        s.Feed(s.Tick(upGap: 21.4));
        s.Repeat(8, () => s.Tick(upPackets: 0));
        s.Feed(s.Tick(upGap: 2323));
        s.Repeat(2, () => s.Tick(upPackets: 0));
        s.Feed(s.Tick(upGap: 707));
        s.Feed(s.Tick(upPackets: 0));
        s.Feed(s.Tick(upGap: 371));
        s.Repeat(10, () => s.Tick(upPackets: 0));
        s.Feed(s.Tick(upGap: 3197));
        s.Finish();
        Check("a loading screen's bursts are not a stall", s.Events.Count == 0, Got(s));
    }

    private static void CadenceChangeIsNotAStall()
    {
        // 2026-09-15: the server going from a packet every 25 ms to one every 200 ms as a match handed over.
        var s = new Session();
        s.Calm(240);
        s.Repeat(40, () => s.Tick(downGap: 200));
        s.Finish();
        Check("a server changing pace is not a stall", s.Events.Count == 0, Got(s));
    }

    private static void BusyLineIsFlagged()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(8, () => s.Tick(wire: 40, process: 40, datacentre: 40, lineDown: 45));
        s.Finish();
        ExpectOne("a spike while the line carries 45 Mbps", s, SpikeVerdicts.Relay);
        if (s.Events.Count == 1)
        {
            Check("  ...is flagged as a busy line", s.Events[0].BusyLine, "BusyLine false");
        }
    }

    private static void MatchSummaryCountsSpikes()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(8, () => s.Tick(wire: 40, process: 40, datacentre: 40));
        s.Calm(80);
        s.Repeat(8, () => s.Tick(datacentre: 40));
        s.Finish();
        var summary = s.Detector.EndMatch();

        Check("the match summary exists", summary is not null, "null");
        if (summary is null) return;
        Check("  ...counts two spikes", summary.Spikes == 2, $"{summary.Spikes}");
        Check("  ...one on the way to the relay and one past it",
            summary.SpikesByVerdict.GetValueOrDefault(SpikeVerdicts.Relay) == 1 &&
            summary.SpikesByVerdict.GetValueOrDefault(SpikeVerdicts.Game) == 1,
            string.Join(", ", summary.SpikesByVerdict.Select(p => $"{p.Key} {p.Value}")));
        Check("  ...has a datacentre median near 44 ms", summary.DatacentreP50 is > 42 and < 46, $"{summary.DatacentreP50:F1}");
        Check("  ...and nothing after it is ended", s.Detector.EndMatch() is null, "a second summary");
    }

    // ------------------------------------------------------------------ moving between ways in

    private static void RouteToTheRelayBadWhileTheEntryIsCleanMoves()
    {
        // 2026-09-15, 19:18: Viettel to sg-2 at 77 ms against 44 normal, the entry's road untouched.
        var w = new Ways();
        w.Run(120, 44, 46);
        Check("thirty calm seconds do not move", w.Decisions.Count == 0, $"{w.Decisions.Count} decision(s)");
        w.Run(120, 77, 46);
        Check("thirty seconds 31 ms worse than the entry moves to it",
            w.Decisions.Count == 1 && w.Decisions[0] is { From: "sg-2", To: "sg-2-vn" },
            string.Join(", ", w.Decisions.Select(d => $"{d.From}->{d.To}")));
        if (w.Decisions.Count == 1)
        {
            Check("  ...and says what the entry measured", w.Decisions[0].ToStats.P50 is > 44 and < 48,
                $"{w.Decisions[0].ToStats.P50:F1}");
        }
    }

    private static void AFiveSecondSpikeDoesNotMove()
    {
        var w = new Ways();
        w.Run(120, 44, 46);
        w.Run(20, 120, 46);
        w.Run(400, 44, 46);
        Check("a five-second spike on the current way does not move", w.Decisions.Count == 0, $"{w.Decisions.Count} decision(s)");
    }

    private static void EveryWaySlowTogetherDoesNotMove()
    {
        // Wi-Fi, the relay itself or its route to the game: every way rises at once.
        var w = new Ways();
        w.Run(400, 84, 86);
        Check("every way slow together does not move", w.Decisions.Count == 0, $"{w.Decisions.Count} decision(s)");
    }

    private static void SteadyLossOnTheCurrentWayMoves()
    {
        var w = new Ways();
        w.Run(240, i => i % 4 == 0 ? null : 44, _ => 46);
        Check("a quarter of the current way's pongs lost for thirty seconds moves, at normal latency",
            w.Decisions.Count == 1, $"{w.Decisions.Count} decision(s)");
    }

    private static void OneShortOutageDoesNotMove()
    {
        var w = new Ways();
        w.Run(120, 44, 46);
        w.Run(14, (double?)null, 46);
        w.Run(300, 44, 46);
        Check("one outage of three and a half seconds does not move", w.Decisions.Count == 0, $"{w.Decisions.Count} decision(s)");
    }

    private static void AnOlderRelayThatNeverAnswersProbesIsNeverAChoice()
    {
        var w = new Ways();
        w.Run(400, 84, (double?)null);
        Check("a way that never answers a probe is never moved to", w.Decisions.Count == 0, $"{w.Decisions.Count} decision(s)");
    }

    private static void AnEntryLosingProbesIsNotClean()
    {
        var w = new Ways();
        w.Run(400, _ => 84, i => i % 24 == 0 ? null : 46);
        Check("a way losing five probes in thirty seconds is not moved to", w.Decisions.Count == 0, $"{w.Decisions.Count} decision(s)");
    }

    private static void OnlyAClearDifferenceMoves()
    {
        var small = new Ways();
        small.Run(400, 52, 44);
        Check("8 ms better is not a reason to move", small.Decisions.Count == 0, $"{small.Decisions.Count} decision(s)");

        var clear = new Ways();
        clear.Run(400, 56, 44);
        Check("12 ms better is", clear.Decisions.Count == 1, $"{clear.Decisions.Count} decision(s)");
    }

    private static void NothingMovesAgainForFiveMinutes()
    {
        var w = new Ways();
        w.Run(DoorSwitchPolicy.CooldownTicks + 100, 84, 46);
        Check("still bad, nothing is decided again inside five minutes", w.Decisions.Count == 1, $"{w.Decisions.Count} decision(s)");
        w.Run(40, 84, 46);
        Check("  ...and is after them", w.Decisions.Count == 2 &&
            w.Decisions[1].TickIndex - w.Decisions[0].TickIndex >= DoorSwitchPolicy.CooldownTicks,
            string.Join(", ", w.Decisions.Select(d => d.TickIndex)));
    }

    private static void ADifferentWayInStartsTheWindowAgain()
    {
        var w = new Ways();
        w.Run(100, 84, 46);
        w.Current = "sg-2-vn";
        w.Others = ["sg-2"];
        w.Run(119, 84, 46);
        Check("thirty seconds on a way count only from when the tunnel took it", w.Decisions.Count == 0, $"{w.Decisions.Count} decision(s)");
        w.Run(1, 84, 46);
        Check("  ...and then they do", w.Decisions.Count == 1 && w.Decisions[0].From == "sg-2-vn", $"{w.Decisions.Count} decision(s)");
    }

    private static void TheBetterOfTwoWaysIsChosen()
    {
        var w = new Ways { Others = ["sg-2-vn", "sg-2-vn2"] };
        w.Run(120, _ => 90, _ => 60, _ => 45);
        Check("with two better ways, the faster one is taken",
            w.Decisions.Count == 1 && w.Decisions[0].To == "sg-2-vn2",
            string.Join(", ", w.Decisions.Select(d => d.To)));
    }

    /// <summary>The tunnel moved from sg-4 to vn-1-sg4, as on 2026-09-18 at 20:54.</summary>
    private static Ways LeftSg4()
    {
        var w = new Ways { Current = "sg-4", Others = ["vn-1-sg4"] };
        w.Run(120, i => i % 5 == 0 ? null : 90, _ => 60);
        w.Current = "vn-1-sg4";
        w.Others = ["sg-4"];
        return w;
    }

    private static void TheWayLeftIsReturnedToOnceItRecovers()
    {
        // 2026-09-18: sg-4 at 43 ms against 53 on the entry for the rest of the hour, and the player stayed on
        // the entry - 10 ms or more in only 40% of quarter seconds. Replayed from that session's own samples.
        var w = LeftSg4();
        var pairs = Session20260918.Pairs();
        w.Replay(pairs.Take(DoorSwitchPolicy.CooldownTicks - 1));
        Check("2026-09-18: nothing goes back inside five minutes of leaving sg-4", w.Decisions.Count == 1,
            string.Join(", ", w.Decisions.Select(d => $"{d.From}->{d.To}")));
        w.Replay(pairs.Skip(DoorSwitchPolicy.CooldownTicks - 1));
        var back = w.Decisions.Skip(1).FirstOrDefault();
        Check("  ...then goes back to sg-4, recovered at 43 ms against 53",
            back is { Return: true, From: "vn-1-sg4", To: "sg-4" },
            string.Join(", ", w.Decisions.Select(d => $"{d.From}->{d.To}{(d.Return ? " (return)" : "")}")));
        if (back is null) return;
        Check("  ...as soon as the five minutes are up",
            back.TickIndex - w.Decisions[0].TickIndex == DoorSwitchPolicy.CooldownTicks,
            $"{(back.TickIndex - w.Decisions[0].TickIndex) / SpikeDetector.TicksPerSecond} s after leaving");
        Check("  ...judged over two minutes", back.WindowTicks == DoorSwitchPolicy.ReturnWindowTicks, $"{back.WindowTicks}");
        Check("  ...and says what each way measured",
            back.ToStats.P50 is > 42 and < 45 && back.FromStats.P50 is > 52 and < 55,
            $"sg-4 {back.ToStats.P50:F1}, vn-1-sg4 {back.FromStats.P50:F1}");
    }

    private static void AWayLeftThatStillLosesProbesIsNotReturnedTo()
    {
        var w = LeftSg4();
        w.Run(DoorSwitchPolicy.CooldownTicks * 2, _ => 53, i => i % 50 == 0 ? null : 43);
        Check("a way left still losing one probe in fifty is not gone back to", w.Decisions.Count == 1,
            $"{w.Decisions.Count} decision(s)");
    }

    private static void AWayLeftThatIsOnlyAsFastIsNotReturnedTo()
    {
        var w = LeftSg4();
        w.Run(DoorSwitchPolicy.CooldownTicks * 2, 52, 51);
        Check("a way left that is only a millisecond faster is not gone back to", w.Decisions.Count == 1,
            $"{w.Decisions.Count} decision(s)");
    }

    private static void AWayLeftWithTheWorseTailIsNotReturnedTo()
    {
        var w = LeftSg4();
        w.Run(DoorSwitchPolicy.CooldownTicks * 2, _ => 53, i => i % 10 == 0 ? 95 : 43);
        Check("a way left, faster at the median but spiking every few seconds, is not gone back to",
            w.Decisions.Count == 1, $"{w.Decisions.Count} decision(s)");
    }

    private static void NothingIsReturnedToWhenTheMoveWasNotMade()
    {
        // Record mode: the decision is written down and the tunnel stays where it was.
        var w = new Ways { Current = "sg-4", Others = ["vn-1-sg4"] };
        w.Run(120, i => i % 5 == 0 ? null : 90, _ => 60);
        w.Run(DoorSwitchPolicy.CooldownTicks * 2, 43, 53);
        Check("a decision not acted on is never gone back from", w.Decisions.Count == 1 && !w.Decisions.Any(d => d.Return),
            string.Join(", ", w.Decisions.Select(d => $"{d.From}->{d.To}")));
    }

    private static void AMoveToAFasterWayDoesNotBounceBack()
    {
        var w = new Ways();
        w.Run(120, 56, 44);
        w.Current = "sg-2-vn";
        w.Others = ["sg-2"];
        w.Run(DoorSwitchPolicy.CooldownTicks * 2, 44, 56);
        Check("a move to a way that stays faster is not undone", w.Decisions.Count == 1, $"{w.Decisions.Count} decision(s)");
    }

    private static void TheLobbyIsJudgedToo()
    {
        // 2026-09-23 20:03: VALORANT open in the lobby, hk at 73-115 ms, and nothing compared it with anything.
        var lobby = new Ways { Current = "hk", Others = ["vn-2-hk"], Mode = TickMode.Lobby };
        lobby.Run(120, 98, 50);
        Check("in the lobby, a way 48 ms slower is left like in a match",
            lobby.Decisions.Count == 1 && lobby.Decisions[0].To == "vn-2-hk", $"{lobby.Decisions.Count} decision(s)");

        var closed = new Ways { Current = "hk", Others = ["vn-2-hk"], Mode = TickMode.Idle };
        closed.Run(400, 98, 50);
        Check("  ...but not with the game closed", closed.Decisions.Count == 0, $"{closed.Decisions.Count} decision(s)");

        var intoMatch = new Ways { Current = "hk", Others = ["vn-2-hk"], Mode = TickMode.Lobby };
        intoMatch.Run(60, 98, 50);
        intoMatch.Mode = TickMode.Match;
        intoMatch.Run(60, 98, 50);
        Check("  ...and the lobby's seconds count towards the match's thirty", intoMatch.Decisions.Count == 1,
            $"{intoMatch.Decisions.Count} decision(s)");
    }

    private static void ADetourTakenAtConnectIsLeftOnceTheRoadRecovers()
    {
        // The connect started on vn-2-hk because hk was slow. hk is back at 43 against 53: inside the "worse" margin,
        // so only the return rule can bring the player back - as after the 2026-09-18 move off sg-4.
        var plain = new Ways { Current = "vn-2-hk", Others = ["hk"] };
        plain.Run(DoorSwitchPolicy.ReturnWindowTicks * 2, 53, 43);
        Check("without a detour at connect, 43 against 53 moves nobody", plain.Decisions.Count == 0,
            $"{plain.Decisions.Count} decision(s)");

        var detour = new Ways { Current = "vn-2-hk", Others = ["hk"] };
        detour.Policy.StartedOnDetour("hk", "vn-2-hk");
        detour.Run(DoorSwitchPolicy.ReturnWindowTicks - 1, 53, 43);
        Check("  ...with one, not before two minutes", detour.Decisions.Count == 0, $"{detour.Decisions.Count} decision(s)");
        detour.Run(1, 53, 43);
        Check("  ...then back to hk, with no five-minute wait",
            detour.Decisions is [{ Return: true, From: "vn-2-hk", To: "hk" }],
            string.Join(", ", detour.Decisions.Select(d => $"{d.From}->{d.To}{(d.Return ? " (return)" : "")}")));

        var elsewhere = new Ways { Current = "vn-1-hk", Others = ["hk", "vn-2-hk"] };
        elsewhere.Policy.StartedOnDetour("hk", "vn-2-hk");
        elsewhere.Run(DoorSwitchPolicy.ReturnWindowTicks * 2, _ => 53, _ => 43, _ => 60);
        Check("  ...and never while the tunnel is on another way", elsewhere.Decisions.Count == 0,
            $"{elsewhere.Decisions.Count} decision(s)");
    }

    // ------------------------------------------------------------------ between matches

    /// <summary>The supervisor's five-second readings of the tunnel's game UDP count, at a given rate.</summary>
    private sealed class Readings
    {
        private long _now = 1_000_000;
        private long _packets;
        public MatchGap Gap { get; } = new();
        public List<long> FiredAtMs { get; } = [];
        public long LastPacketAtMs { get; private set; } = -1;

        public void Run(int seconds, double packetsPerSecond)
        {
            for (var elapsed = 0; elapsed < seconds; elapsed += 5)
            {
                _now += 5000;
                var sent = (long)(packetsPerSecond * 5);
                _packets += sent;
                if (sent > 0) LastPacketAtMs = _now;
                if (Gap.Feed(_now, _packets)) FiredAtMs.Add(_now);
            }
        }
    }

    private static void AMatchEndingOpensOneGap()
    {
        // 2026-09-18 20:02:41: a match at about 25 packets a second, then the lobby for 44 s.
        var r = new Readings();
        r.Run(600, 25);
        r.Run(45, 0);
        Check("a match ending opens the gap once", r.FiredAtMs.Count == 1, $"{r.FiredAtMs.Count} time(s)");
        if (r.FiredAtMs.Count == 1)
        {
            var quiet = (r.FiredAtMs[0] - r.LastPacketAtMs) / 1000;
            Check("  ...ten seconds after the last packet", quiet == 10, $"{quiet} s");
        }
        r.Run(3600, 0);
        Check("  ...and an hour in the lobby after it does not open it again", r.FiredAtMs.Count == 1,
            $"{r.FiredAtMs.Count} time(s)");
    }

    private static void AResultScreenTrickleIsNotAGap()
    {
        var r = new Readings();
        r.Run(600, 25);
        r.Run(60, 2);
        Check("a result screen still trickling to the match's server is not a gap", r.FiredAtMs.Count == 0,
            $"{r.FiredAtMs.Count} time(s)");
        r.Run(20, 0);
        Check("  ...leaving it is", r.FiredAtMs.Count == 1, $"{r.FiredAtMs.Count} time(s)");
    }

    private static void ALobbyBeforeAnyMatchIsNotAGap()
    {
        var r = new Readings();
        r.Run(600, 0);
        Check("a lobby before any match is not a gap", r.FiredAtMs.Count == 0, $"{r.FiredAtMs.Count} time(s)");
    }

    private static void AMatchKeepsItsGapClosed()
    {
        // Five-second readings never see a sub-second stall; a match that slows but keeps sending is still one.
        var r = new Readings();
        r.Run(300, 25);
        r.Run(30, 3);
        r.Run(300, 25);
        Check("a match that slows down but keeps sending is not a gap", r.FiredAtMs.Count == 0, $"{r.FiredAtMs.Count} time(s)");
    }

    private static void TheGapIsTimedFromTheLastPacket()
    {
        // Readings every 5 s; the match's last packet 1.5 s after the reading at 1005 s.
        var gap = new MatchGap();
        long packets = 0;
        for (long t = 1000_000; t <= 1005_000; t += 5000)
        {
            packets += 125;
            gap.Feed(t, packets, t);
        }
        packets += 10;
        gap.Feed(1010_000, packets, 1006_500);
        var fired = gap.Feed(1015_000, packets, 1006_500);
        var due = gap.DueInMs(1015_000);
        Check("known exactly, the quiet counts from the last packet, not the reading before it",
            !fired && due == 1_500, $"fired {fired}, due in {due} ms");
        Check("  ...and completes the gap ten seconds after it", gap.Feed(1016_500, packets, 1006_500), "not fired");
    }

    private static void RescanComparesMediansByAClearMargin()
    {
        Check("eight answers give a median",
            RescanScore.Median([44, 45, 43, 90, 44, 46, 44, 45]) is > 43.5 and < 45.5,
            $"{RescanScore.Median([44, 45, 43, 90, 44, 46, 44, 45])}");
        Check("one slow echo does not move the median", RescanScore.Median([44, 44, 44, 44, 44, 44, 44, 300]) == 44,
            $"{RescanScore.Median([44, 44, 44, 44, 44, 44, 44, 300])}");
        Check("three lost of eight is too few to compare", RescanScore.Median([44, null, 45, null, 44, 46, null, 45]) is null,
            "scored");
        Check("5 ms faster than 50 is worth moving", RescanScore.WorthMoving(50, 45), "not moved");
        Check("4 ms faster than 50 is not", !RescanScore.WorthMoving(50, 46), "moved");
        Check("9 ms faster than 80 is worth moving, 7 is not",
            RescanScore.WorthMoving(80, 71) && !RescanScore.WorthMoving(80, 73), "wrong margin");
    }

    /// <summary>
    /// The pongs down the current way and the probes down the others, quarter second by quarter second. Values
    /// are the round trip to relayd; null is sent and never answered.
    /// </summary>
    private enum TickMode { Match, Lobby, Idle }

    private sealed class Ways
    {
        private readonly Random _random = new(20260916);
        private long _index;

        public DoorSwitchPolicy Policy { get; } = new();
        public List<DoorDecision> Decisions { get; } = [];
        public string Current { get; set; } = "sg-2";
        public string[] Others { get; set; } = ["sg-2-vn"];
        public TickMode Mode { get; set; } = TickMode.Match;

        public void Run(int ticks, double? current, params double?[] others) =>
            Run(ticks, _ => current, others.Select(o => (Func<int, double?>)(_ => o)).ToArray());

        public void Run(int ticks, Func<int, double?> current, params Func<int, double?>[] others)
        {
            for (var i = 0; i < ticks; i++)
            {
                var tick = new QualityTick(_index, new DateTimeOffset(2026, 9, 15, 12, 18, 0, TimeSpan.Zero)
                    .AddMilliseconds(_index * SpikeDetector.TickMs))
                {
                    Active = Mode == TickMode.Match,
                    Lobby = Mode == TickMode.Lobby,
                    RelayProcessSent = true,
                    RelayProcessMs = current(i) is { } c ? c + Noise() : null,
                    CurrentDoor = Current,
                    DoorIds = Others,
                    DoorSent = Enumerable.Repeat(true, Others.Length).ToArray(),
                    DoorMs = others.Select(o => o(i) is { } v ? v + Noise() : (double?)null).ToArray(),
                };
                _index++;
                if (Policy.Feed(tick) is { } decision) Decisions.Add(decision);
            }
        }

        /// <summary>Measured pairs as they were, without added noise, against the one other way.</summary>
        public void Replay(IEnumerable<(double? Current, bool CurrentSent, double? Other, bool OtherSent)> pairs)
        {
            foreach (var (current, currentSent, other, otherSent) in pairs)
            {
                var tick = new QualityTick(_index, new DateTimeOffset(2026, 9, 18, 13, 55, 0, TimeSpan.Zero)
                    .AddMilliseconds(_index * SpikeDetector.TickMs))
                {
                    Active = true,
                    RelayProcessSent = currentSent,
                    RelayProcessMs = current,
                    CurrentDoor = Current,
                    DoorIds = Others,
                    DoorSent = [otherSent],
                    DoorMs = [other],
                };
                _index++;
                if (Policy.Feed(tick) is { } decision) Decisions.Add(decision);
            }
        }

        private double Noise() => (_random.NextDouble() - 0.5) * 3;
    }

    // ------------------------------------------------------------------ the synthetic path

    private sealed class Session
    {
        private const double GatewayNormal = 3;
        private const double WireNormal = 42;
        private const double ProcessNormal = 43;
        private const double DatacentreNormal = 44;

        private readonly Random _random = new(20260915);
        private long _index;

        public SpikeDetector Detector { get; } = new();
        public List<SpikeEvent> Events { get; } = [];
        public bool Landmark { get; init; } = true;
        public bool Gateway { get; init; } = true;

        public void Feed(QualityTick tick) => Events.AddRange(Detector.Feed(tick));

        public void Repeat(int count, Func<QualityTick> make)
        {
            for (var i = 0; i < count; i++) Feed(make());
        }

        public void Calm(int ticks) => Repeat(ticks, () => Tick());

        public void Finish()
        {
            Calm(12);
            Events.AddRange(Detector.Flush());
        }

        public QualityTick Idle() => new(_index++, Time(_index)) { Active = false };

        public QualityTick Tick(
            double gateway = 0, double wire = 0, double process = 0, double datacentre = 0,
            bool loseWire = false, bool loseProcess = false, bool loseDatacentre = false, bool loseGateway = false,
            double downGap = -1, int downPackets = 13,
            double upGap = -1, int upPackets = 15,
            double? lineDown = null, double localLag = 0)
        {
            var tick = new QualityTick(_index, Time(_index)) { Active = true, LocalLagMs = localLag };
            _index++;

            if (Gateway)
            {
                tick.GatewaySent = true;
                tick.GatewayMs = loseGateway ? null : GatewayNormal + Noise() + gateway;
            }
            tick.RelayWireSent = true;
            tick.RelayWireMs = loseWire ? null : WireNormal + Noise() + wire;
            tick.RelayProcessSent = true;
            tick.RelayProcessMs = loseProcess ? null : ProcessNormal + Noise() + process;
            if (Landmark)
            {
                tick.DatacentreSent = true;
                tick.DatacentreMs = loseDatacentre ? null : DatacentreNormal + Noise() + datacentre;
            }

            tick.DownPackets = downGap >= 0 ? Math.Max(1, downPackets) : downPackets;
            tick.DownGapMs = downGap >= 0 ? downGap : downPackets > 0 ? 20 + 4 * Noise() : 0;
            tick.UpPackets = upGap >= 0 ? Math.Max(1, upPackets) : upPackets;
            tick.UpGapMs = upGap >= 0 ? upGap : upPackets > 0 ? 17 + 2 * Noise() : 0;

            tick.LineDownMbps = lineDown ?? 0.4;
            tick.LineUpMbps = 0.3;
            return tick;
        }

        private static DateTimeOffset Time(long index) =>
            new DateTimeOffset(2026, 9, 15, 21, 0, 0, TimeSpan.Zero).AddMilliseconds(index * SpikeDetector.TickMs);

        private double Noise() => (_random.NextDouble() - 0.5) * 3;
    }

    // ------------------------------------------------------------------ assertions

    private static void ExpectOne(string name, Session s, string verdict) =>
        Check(name, s.Events.Count == 1 && s.Events[0].Verdict == verdict, $"expected one '{verdict}', got {Got(s)}");

    private static string Got(Session s) =>
        s.Events.Count == 0 ? "no spikes" : "[" + string.Join(", ", s.Events.Select(e => e.Verdict)) + "]";

    private static void Check(string name, bool ok, string detail)
    {
        if (ok)
        {
            Console.WriteLine($"  ok    {name}");
            return;
        }
        _failures++;
        Console.WriteLine($"  FAIL  {name}: {detail}");
    }
}
