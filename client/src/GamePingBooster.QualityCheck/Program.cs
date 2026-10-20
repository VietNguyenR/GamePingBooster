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
internal static partial class Program
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
        Console.WriteLine("The home line (household / congested):");
        AnotherDeviceFillingTheLineIsHousehold();
        AShortSpikeIsNeverHousehold();
        ThisPcDownloadingIsNeverHousehold();
        AQuietHouseIsNotHouseholdButTheRouterQueued();
        WithoutCountersTheRouterQueueingIsCongested();
        ViettelMatchPhoneSpeedTestIsCongested();
        ViettelPhoneSpeedTestRecordingIsCongested();
        WeakSignalPhoneSpeedTestRecordingIsCongested();
        LateEchoesOnACalmFloorAreNotCongested();
        ASlowRouteIsNeitherHouseholdNorCongested();
        ARouterThatDoesNotAnswerPingsIsNeverCongested();
        MatchSummaryCountsTheHomeLine();
        HomeLineWatchChecks();

        Console.WriteLine();
        Console.WriteLine("Moving between ways into one relay:");
        RouteToTheRelayBadWhileTheEntryIsCleanMoves();
        AFiveSecondSpikeDoesNotMove();
        EveryWaySlowTogetherDoesNotMove();
        SteadyLossOnTheCurrentWayMoves();
        OneShortOutageDoesNotMove();
        AnOlderRelayThatNeverAnswersProbesIsNeverAChoice();
        AnEntryLosingProbesIsNotClean();
        ABlipOnTheEntryDoesNotHoldAMoveBack();
        OnlyAClearDifferenceMoves();
        NothingMovesAgainForFiveMinutes();
        ADifferentWayInStartsTheWindowAgain();
        ADifferentLaneStartsTheWindowAgain();
        TheBetterOfTwoWaysIsChosen();
        TheWayLeftIsReturnedToOnceItRecovers();
        AWayLeftThatStillLosesProbesIsNotReturnedTo();
        AWayLeftIsHeldToTheOldCleanBar();
        AWayLeftThatIsOnlyAsFastIsNotReturnedTo();
        AWayLeftWithTheWorseTailIsNotReturnedTo();
        NothingIsReturnedToWhenTheMoveWasNotMade();
        AMoveToAFasterWayDoesNotBounceBack();
        TheLobbyIsJudgedToo();
        ADetourTakenAtConnectIsLeftOnceTheRoadRecovers();
        TheOwnersMatchOnSg2MovesToVn5Sg2();
        ASteadyGapMovesAfterThirtySeconds();
        ASmallSteadyGapDoesNotMove();
        ASteadyGapWithTheWorseTailDoesNotMove();
        ASteadyGapLosingProbesDoesNotMove();

        Console.WriteLine();
        Console.WriteLine("The lobby record (LobbyTracker):");
        ALobbyBeforeAMatchIsOneRecord();
        AShortLobbyIsNoRecord();
        ALongLobbyKeepsItsLastTwoMinutes();
        AMoveInTheLobbyEndsTheRecord();
        AHoleDoesNotEndTheLobbyButLeavingDoes();

        Console.WriteLine();
        Console.WriteLine("Between matches:");
        AMatchEndingOpensOneGap();
        AResultScreenTrickleIsNotAGap();
        ALobbyBeforeAnyMatchIsNotAGap();
        AMatchKeepsItsGapClosed();
        TheGapIsTimedFromTheLastPacket();
        RescanComparesMediansByAClearMargin();
        ATunnelKeptByAMoveGoesAfterTheNextMatch();
        AGameThatHoldsItsSessionStaysOnceItHasSent();
        AReconnectKeepsATcpGamesRoutes();

        Console.WriteLine();
        Console.WriteLine("A region's tunnel, between matches (WayCheck):");
        WayCheckChecks();

        Console.WriteLine();
        Console.WriteLine("Which lane - source port - into the way in use (LanePick):");
        LanePickChecks();

        Console.WriteLine();
        Console.WriteLine("Which way into a relay the list shows and a connect starts on (DoorChoice):");
        DoorChoiceChecks();

        Console.WriteLine();
        Console.WriteLine("Paths that swing (Jitter):");
        JitterChecks();

        Console.WriteLine();
        Console.WriteLine("Which edges the unblock resolver probes and hands out (EdgeRanking):");
        EdgeRankingChecks();

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

    // A phone speed test on the same Wi-Fi (2026-10-20): everything from the router on rises together, the router's counters
    // show the phone, this PC is quiet.
    private static void AnotherDeviceFillingTheLineIsHousehold()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(16, () => s.Tick(gateway: 60, wire: 60, process: 60, datacentre: 60, other: 700));
        s.Finish();
        ExpectOne("four seconds slow while other devices move 700 Mbps", s, SpikeVerdicts.Router);
        if (s.Events.Count != 1) return;
        Check("  ...is household", s.Events[0].Household, "Household false");
        Check("  ...is not also congested", !s.Events[0].Congested, "Congested true");
        Check("  ...says what the others moved", s.Events[0].OtherMbps is > 650 and < 750, $"{s.Events[0].OtherMbps}");
    }

    private static void AShortSpikeIsNeverHousehold()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(8, () => s.Tick(gateway: 60, wire: 60, process: 60, datacentre: 60, other: 700));
        s.Finish();
        Check("two seconds slow with other traffic is not household",
            s.Events.Count == 1 && !s.Events[0].Household && !s.Events[0].Congested, Got(s));
    }

    private static void ThisPcDownloadingIsNeverHousehold()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(16, () => s.Tick(gateway: 60, wire: 60, process: 60, datacentre: 60, other: 700, lineDown: 300));
        s.Finish();
        Check("this PC downloading 300 Mbps is neither household nor congested",
            s.Events.Count == 1 && !s.Events[0].Household && !s.Events[0].Congested && s.Events[0].BusyLine, Got(s));
    }

    private static void AQuietHouseIsNotHouseholdButTheRouterQueued()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(16, () => s.Tick(gateway: 60, wire: 60, process: 60, datacentre: 60, other: 2));
        s.Finish();
        Check("the router queueing while other devices move 2 Mbps is congested, not household",
            s.Events.Count == 1 && !s.Events[0].Household && s.Events[0].Congested, Got(s));
    }

    private static void WithoutCountersTheRouterQueueingIsCongested()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(16, () => s.Tick(gateway: 60, wire: 60, process: 60, datacentre: 60));
        s.Finish();
        Check("the router queueing with no counters is congested, never household",
            s.Events.Count == 1 && !s.Events[0].Household && s.Events[0].Congested && s.Events[0].OtherMbps is null, Got(s));
    }

    // The owner's PUBG match on Viettel (ZTE F6601P, Wi-Fi 83 %), 2026-10-20 17:40:39 local, a phone speed test on the same
    // Wi-Fi, no router counters: the spike as recorded, tick by tick. The median rule missed it (gateway median excess 24 ms).
    private static void ViettelMatchPhoneSpeedTestIsCongested()
    {
        var s = new Session();
        s.Replay(1, 43,
            [2, 1, 7, 1, 21, 24, 13, 50, 227, 31, 15, 367, 125, 22, 5, 22, 31, 13, 25, 7, 28, 95, 13, 8, 30, 20, 14, 86, 29, 31, 87, 46, 19, 24, 9, 124, 9, 74, 21, 40, 2, 1, 1, 1],
            [43, 46, 43, 43, 65, 67, 61, 91, 269, 67, 50, 409, 162, 72, 51, 62, 71, 56, 70, 51, 63, 133, 46, 50, 72, 68, 56, 122, 65, 73, 127, 92, 65, 65, 45, 169, 51, 114, 61, 90, 43, 43, 43, 43],
            [41.6, 42.5, 41.5, 42.8, 70.4, 70.1, 61.6, 94.7, 266.1, 138.1, 56.3, 420.7, 172.8, 72.6, 54.4, 83.5, 71.8, 62.8, 71.5, 58.5, 66.3, 137.1, 52.6, 50.8, 75.2, 69.6, 59.9, 125.4, 67.3, 93.3, 133.8, 93.7, 65.5, 117.2, 46.2, 171.1, 54.5, 228, 66.6, 60.5, 41.7, 41.5, 41.8, 43.5],
            [42.5, 46.2, 42.3, 44, 70.3, 72.2, 64.4, 98.1, 273.7, 79.2, 56.2, 420.7, 172.7, 72.6, 54.3, 83.4, 71.7, 62.7, 71.4, 61.5, 66.3, 137, 53.3, 50.7, 75.1, 72.4, 59.7, 127.8, 70.2, 93.4, 134.1, 93.6, 72.4, 78.2, 46.2, 171.8, 55.1, 118.9, 76.9, 61.2, 43.1, 42.9, 42.6, 43.4]);
        Check("Viettel match, phone speed test, no counters: congested",
            s.Events.Count == 1 && s.Events[0].Verdict == SpikeVerdicts.Router && s.Events[0].Congested && !s.Events[0].Household, Got(s));
    }

    // Recorded 2026-10-20 18:12:55-18:13:20 on the same Viettel line (no game; 1.1.1.1 stands in for the relay): the phone's
    // speed test. Only the first 3 s window of the floor needs to rise, so any spike in it must be congested.
    private static void ViettelPhoneSpeedTestRecordingIsCongested()
    {
        var s = new Session();
        s.Replay(1.8, 30,
            [2.2, 1.6, 1.5, 3.4, 2.4, 2.5, 1.9, 2.4, 2.8, 2.6, 1.9, 1.5, 1.9, 2.7, 2.6, 2.2, 2.4, 570.5, 7.6, 18.6, 283.3, 712.8, 9.6, 18.5, 161.4, 53, 14.2, 14.2, 15, 118.8, 162.1, 41.2, null, null, 92.6, 57.5, 18.5, 124, 27.9, 569.1, 32.8, 27.7, null, 193.6, 3.1, 2.2, 19.2, 19.1, 3.9, 1.5, 1.7, 1.9, 2.1, 1.7, 2.9, 2.9, 3.8, 63.3, 14.3, 43.9, 33.5, null, 487.4, 2, 25.5, 53.8, 199.5, 9.8, 17.2, 55.8, 21.9, 36.6, 259.4, 71.7, 65.8, 2.3, 3.6, 1.8, 2.1, 5.2, 2.4, 1.3, 1.7, 7.4, 2.3, 2, 2, 2.4],
            [30.3, 29.9, 29.8, 31.1, 30.2, 29.7, 30, 33.1, 30.6, 30.6, 29.8, 29.6, 30.5, 30.3, 30.4, 29.6, 30.9, 597.9, 49.7, 409.1, 68.4, 733.4, 45.5, 160.6, 34.4, 83.5, 37.9, 46.8, 33.7, 142.3, 185.1, 65.9, null, null, 117, 70, 39.6, 114.2, 163.4, 137.1, 650.1, 36.9, 75.5, null, 223.3, 101.5, 35.3, 59, 46.7, 30.9, 31.3, 34.3, 29.8, 30.1, 29, 30.9, 30.8, 36.6, 94.7, 47.3, 68.6, 70.2, null, 512.2, 30.8, 29.9, 65.3, 36.4, 225.6, 37.6, 65.6, 83, 51.6, 59.1, 286.6, 69.5, 90.7, 30.9, 30.9, 29.9, 30.7, 34.2, 30.7, 30, 29.8, 40.2, 30.1, 29.7, 29.2, 30.8]);
        Check("Viettel recording, phone speed test: a congested spike",
            s.Events.Any(e => e.Congested) && s.Events.All(e => !e.Household), Got(s));
    }

    // Recorded 2026-10-20 18:25:25-18:25:48 on VNPT with the PC at 29 % signal (link 28 Mbps): the phone's speed test.
    private static void WeakSignalPhoneSpeedTestRecordingIsCongested()
    {
        var s = new Session();
        s.Replay(1.9, 24.5,
            [1.9, 1.5, 2.6, 2.9, 2.1, 2.3, 2.1, 2, 1.9, 9.1, 14.9, 9.6, null, 710.5, 29, 10.3, 5.8, null, 340.2, 98.7, 198.6, 2, 2, 38.2, 49.3, 304.9, 9.7, 355.8, 31.6, 34.8, 124.1, 22.4, 11, 39.6, 155.3, 31.7, 15.8, 532.3, 31.4, 60.7, 3.5, 4.8, 115.2, 3.8, 6.2, 1.6, 2, 1.9, 2, 1.7, 4.2, 6.3, 4.2, 275.7, null, null, 112.9, 13.9, 6, null, 10.2, 2.7, 13.5, 56, 2.6, 42.2, 2.4, 2.3, 2.4, 2, 1.6, 2.2, 2.2, 1.7, 1.6],
            [24.5, 24.7, 25.9, 25.2, 24.4, 24.2, 25, 23.9, 24.4, 127.9, 36.1, 72.2, null, 738.3, 111.1, null, 404.8, 77.6, 200.9, 49.2, 156.4, 156.6, 71.6, 336.6, 42.7, 383, 172.4, 31.2, 145.5, 57, 47, 86.3, 186.3, 55, 35.7, 594.6, 55.3, 47.6, 26.3, 31.4, 153.6, 26.2, 26.7, 24.5, 29.1, 24.6, 24.6, 24.4, 25.2, 31.9, 32.9, 323.9, null, null, 126.6, 44, 36.6, 32.8, 41.7, 72.8, 37.4, 78.2, 26.3, 67, 37.5, 25.3, 25.6, 23.7, 23.7, 24.6, 26, 24.2, 23.7]);
        Check("VNPT recording at 29 % signal, phone speed test: a congested spike",
            s.Events.Any(e => e.Congested) && s.Events.All(e => !e.Household), Got(s));
    }

    // What a weak signal or interference looks like: half the echoes retry and arrive late, the others are as quick as ever.
    // The median rule called this congested; the floor did not rise, so no queue - not another device.
    private static void LateEchoesOnACalmFloorAreNotCongested()
    {
        var s = new Session();
        var late = false;
        s.Calm(240);
        s.Repeat(40, () =>
        {
            late = !late;
            return s.Tick(gateway: late ? 150 : 0, wire: late ? 150 : 0, process: late ? 150 : 0, datacentre: late ? 150 : 0);
        });
        s.Finish();
        Check("every other echo 150 ms late on a calm floor: not congested",
            s.Events.Count >= 1 && s.Events.All(e => !e.Congested && !e.Household), Got(s));
    }

    private static void ASlowRouteIsNeitherHouseholdNorCongested()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(16, () => s.Tick(wire: 60, process: 60, datacentre: 60));
        s.Finish();
        Check("the route slow past a calm router is neither", s.Events.Count == 1 && s.Events[0].Verdict == SpikeVerdicts.Relay &&
            !s.Events[0].Household && !s.Events[0].Congested, Got(s));
    }

    private static void ARouterThatDoesNotAnswerPingsIsNeverCongested()
    {
        var s = new Session { Gateway = false };
        s.Calm(240);
        s.Repeat(16, () => s.Tick(wire: 60, process: 60, datacentre: 60));
        s.Finish();
        Check("no router ping, no counters: neither", s.Events.Count == 1 && !s.Events[0].Household && !s.Events[0].Congested, Got(s));
    }

    private static void MatchSummaryCountsTheHomeLine()
    {
        var s = new Session();
        s.Calm(240);
        s.Repeat(16, () => s.Tick(gateway: 60, wire: 60, process: 60, datacentre: 60, other: 700));
        s.Calm(80);
        s.Repeat(16, () => s.Tick(gateway: 60, wire: 60, process: 60, datacentre: 60, other: 1));
        s.Finish();
        var summary = s.Detector.EndMatch();
        Check("the match counts one household and one congested spike",
            summary is { HouseholdSpikes: 1, CongestedSpikes: 1 }, $"{summary?.HouseholdSpikes} / {summary?.CongestedSpikes}");
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
        Check("31 ms worse than the entry moves to it",
            w.Decisions.Count == 1 && w.Decisions[0] is { From: "sg-2", To: "sg-2-vn" },
            string.Join(", ", w.Decisions.Select(d => $"{d.From}->{d.To}")));
        if (w.Decisions.Count == 1)
        {
            var after = (w.Decisions[0].TickIndex - 120 + 1) / (double)SpikeDetector.TicksPerSecond;
            Check("  ...six seconds into it: three quarters of the eight", after == 6, $"{after} s");
            Check("  ...and says what the entry measured", w.Decisions[0].ToStats.P50 is > 44 and < 48,
                $"{w.Decisions[0].ToStats.P50:F1}");
        }
    }

    private static void ABlipOnTheEntryDoesNotHoldAMoveBack()
    {
        // 2026-10-02 18:21, the owner's match: sg-2 at 83 ms against 46-54 on vn-1-sg2, and the entry lost three probes
        // in a row 22 s in. With "at most two in thirty seconds" that kept it unclean for thirty seconds more - 58 s of lag.
        var w = new Ways { Current = "sg-2", Others = ["vn-1-sg2"] };
        w.Run(120, 52, 54);
        w.Run(16, 85, 54);
        w.Run(3, (double?)85, (double?)null);
        w.Run(120, 85, 45);
        Check("three probes lost in a row on the entry do not hold the move back",
            w.Decisions.Count == 1 && w.Decisions[0].TickIndex - 120 < 8 * SpikeDetector.TicksPerSecond,
            string.Join(", ", w.Decisions.Select(d => $"{(d.TickIndex - 120) / (double)SpikeDetector.TicksPerSecond} s")));
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
        w.Run(400, _ => 84, i => i % 8 == 0 ? null : 46);
        Check("a way losing one probe in eight is not moved to", w.Decisions.Count == 0, $"{w.Decisions.Count} decision(s)");

        var loss = new Ways();
        loss.Run(400, i => i % 4 == 0 ? null : 44, i => i % 24 == 0 ? null : 46);
        Check("  ...nor, for loss alone, one losing five in thirty seconds", loss.Decisions.Count == 0,
            $"{loss.Decisions.Count} decision(s)");
    }

    private static void OnlyAClearDifferenceMoves()
    {
        var small = new Ways();
        small.Run(DoorSwitchPolicy.SteadyWindowTicks - 1, 52, 44);
        Check("8 ms better is not a reason to move inside thirty seconds", small.Decisions.Count == 0,
            $"{small.Decisions.Count} decision(s)");

        var clear = new Ways();
        clear.Run(400, 56, 44);
        Check("12 ms better is", clear.Decisions.Count == 1 && !clear.Decisions[0].Steady, $"{clear.Decisions.Count} decision(s)");
    }

    // ------------------------------------------------------------------ steadily faster

    private static void TheOwnersMatchOnSg2MovesToVn5Sg2()
    {
        // 2026-10-07 17:40: sg-2 at 58 ms, vn-5-sg2 at 49, for the whole match - and nothing moved until it was over.
        var w = new Ways { Current = "sg-2", Others = Session20261007.Others };
        w.Replay(Session20261007.Ticks());
        Check("2026-10-07: sg-2 at 58 against vn-5-sg2 at 49 moves to vn-5-sg2 by the steady rule",
            w.Decisions is [{ From: "sg-2", To: "vn-5-sg2", Steady: true }],
            string.Join(", ", w.Decisions.Select(d => $"{d.From}->{d.To}{(d.Steady ? " (steady)" : "")}")));
        if (w.Decisions.Count != 1) return;
        var d = w.Decisions[0];
        Check("  ...thirty seconds in", d.TickIndex + 1 == DoorSwitchPolicy.SteadyWindowTicks,
            $"{(d.TickIndex + 1) / (double)SpikeDetector.TicksPerSecond} s");
        Check("  ...and says what each way measured",
            d.FromStats.P50 is > 57 and < 59.5 && d.ToStats.P50 is > 48 and < 51 && d.ToStats.P95 <= d.FromStats.P95,
            $"sg-2 {d.FromStats.P50:F1}/{d.FromStats.P95:F1}, vn-5-sg2 {d.ToStats.P50:F1}/{d.ToStats.P95:F1}");
    }

    private static void ASteadyGapMovesAfterThirtySeconds()
    {
        var w = new Ways();
        w.Run(DoorSwitchPolicy.SteadyWindowTicks - 1, 52, 44);
        Check("8 ms better for thirty seconds moves", w.Decisions.Count == 0, "moved before thirty seconds");
        w.Run(1, 52, 44);
        Check("  ...on the thirtieth second, by the steady rule", w.Decisions is [{ Steady: true, To: "sg-2-vn" }],
            $"{w.Decisions.Count} decision(s)");
        if (w.Decisions.Count == 1)
        {
            Check("  ...judged over thirty seconds", w.Decisions[0].WindowTicks == DoorSwitchPolicy.SteadyWindowTicks,
                $"{w.Decisions[0].WindowTicks}");
        }

        var lobby = new Ways { Current = "sg-2", Others = ["vn-5-sg2"], Mode = TickMode.Lobby };
        lobby.Run(DoorSwitchPolicy.SteadyWindowTicks, 58, 49);
        Check("  ...in the lobby as in a match", lobby.Decisions is [{ Steady: true }], $"{lobby.Decisions.Count} decision(s)");
    }

    private static void ASmallSteadyGapDoesNotMove()
    {
        var four = new Ways();
        four.Run(DoorSwitchPolicy.CooldownTicks, 48, 44);
        Check("4 ms better, however long, does not move", four.Decisions.Count == 0, $"{four.Decisions.Count} decision(s)");

        // A tenth of a slow way: 5 ms under 63 is not 10%, 7 is.
        var under = new Ways();
        under.Run(DoorSwitchPolicy.CooldownTicks, 68, 63);
        Check("  ...nor 5 ms better than a 63 ms way - under a tenth of it", under.Decisions.Count == 0,
            $"{under.Decisions.Count} decision(s)");
        var over = new Ways();
        over.Run(DoorSwitchPolicy.CooldownTicks, 70, 63);
        Check("  ...7 ms is", over.Decisions is [{ Steady: true }], $"{over.Decisions.Count} decision(s)");
    }

    private static void ASteadyGapWithTheWorseTailDoesNotMove()
    {
        var w = new Ways();
        w.Run(DoorSwitchPolicy.CooldownTicks, _ => 52, i => i % 10 == 0 ? 95 : 44);
        Check("8 ms faster at the median but spiking every few seconds does not move", w.Decisions.Count == 0,
            $"{w.Decisions.Count} decision(s)");
    }

    private static void ASteadyGapLosingProbesDoesNotMove()
    {
        var lossy = new Ways();
        lossy.Run(DoorSwitchPolicy.CooldownTicks, _ => 52, i => i % 15 == 0 ? null : 44);
        Check("8 ms faster but losing one probe in fifteen does not move", lossy.Decisions.Count == 0,
            $"{lossy.Decisions.Count} decision(s)");

        var clean = new Ways();
        clean.Run(DoorSwitchPolicy.CooldownTicks, _ => 52, i => i % 25 == 0 ? null : 44);
        Check("  ...one in twenty-five does", clean.Decisions is [{ Steady: true }], $"{clean.Decisions.Count} decision(s)");
    }

    private static void NothingMovesAgainForFiveMinutes()
    {
        var w = new Ways();
        w.Run(DoorSwitchPolicy.WindowTicks + DoorSwitchPolicy.CooldownTicks - 2, 84, 46);
        Check("still bad, nothing is decided again inside five minutes", w.Decisions.Count == 1, $"{w.Decisions.Count} decision(s)");
        w.Run(40, 84, 46);
        Check("  ...and is after them", w.Decisions.Count == 2 &&
            w.Decisions[1].TickIndex - w.Decisions[0].TickIndex >= DoorSwitchPolicy.CooldownTicks,
            string.Join(", ", w.Decisions.Select(d => d.TickIndex)));
    }

    private static void ADifferentWayInStartsTheWindowAgain()
    {
        var w = new Ways();
        w.Run(DoorSwitchPolicy.WindowTicks - 4, 84, 46);
        w.Current = "sg-2-vn";
        w.Others = ["sg-2"];
        w.Run(DoorSwitchPolicy.WindowTicks - 1, 84, 46);
        Check("eight seconds on a way count only from when the tunnel took it", w.Decisions.Count == 0, $"{w.Decisions.Count} decision(s)");
        w.Run(1, 84, 46);
        Check("  ...and then they do", w.Decisions.Count == 1 && w.Decisions[0].From == "sg-2-vn", $"{w.Decisions.Count} decision(s)");
    }

    /// <summary>
    /// 2026-09-30, the owner's line through vn-1-sg4: a lane hunt moved the tunnel off a 54 ms lane that lost Probes onto
    /// a 45 ms one, and twenty seconds later the policy left the entry on a window still two-thirds the old lane's.
    /// </summary>
    private static void ADifferentLaneStartsTheWindowAgain()
    {
        var w = new Ways { Current = "vn-1-sg4", Others = ["sg-4"], Lane = 50001 };
        w.Run(DoorSwitchPolicy.WindowTicks - 4, 84, 46);
        w.Lane = 50002;
        w.Run(DoorSwitchPolicy.WindowTicks - 1, 84, 46);
        Check("eight seconds on a way count only from when the tunnel took its lane", w.Decisions.Count == 0, $"{w.Decisions.Count} decision(s)");
        w.Run(1, 84, 46);
        Check("  ...and then they do", w.Decisions.Count == 1, $"{w.Decisions.Count} decision(s)");
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
        w.Run(DoorSwitchPolicy.WindowTicks, i => i % 5 == 0 ? null : 90, _ => 60);
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
        // 9 ms: inside the "worse" margin, so only the return rule could take the player back. At exactly 10 the
        // eight-second window's jitter takes a way that much faster now and then - as WayCheck does at its margin.
        var w = LeftSg4();
        w.Run(DoorSwitchPolicy.CooldownTicks * 2, _ => 52, i => i % 50 == 0 ? null : 43);
        Check("a way left still losing one probe in fifty is not gone back to", w.Decisions.Count == 1,
            $"{w.Decisions.Count} decision(s)");
    }

    private static void AWayLeftIsHeldToTheOldCleanBar()
    {
        // Left for being bad: a tenth lost in eight seconds is clean for any other way, not for this one.
        var lossy = LeftSg4();
        lossy.Run(DoorSwitchPolicy.CooldownTicks * 2, _ => 60, i => i % 20 == 0 ? null : 43);
        Check("a way left, 17 ms faster but losing one probe in twenty, is not moved back to", lossy.Decisions.Count == 1,
            $"{lossy.Decisions.Count} decision(s)");

        var clean = LeftSg4();
        clean.Run(DoorSwitchPolicy.CooldownTicks + DoorSwitchPolicy.WindowTicks, 90, 43);
        Check("  ...clean, with the entry gone bad, it is - once the five minutes are up",
            clean.Decisions.Count == 2 && clean.Decisions[1] is { From: "vn-1-sg4", To: "sg-4" } &&
            clean.Decisions[1].TickIndex - clean.Decisions[0].TickIndex == DoorSwitchPolicy.CooldownTicks,
            string.Join(", ", clean.Decisions.Select(d => $"{d.From}->{d.To} at {d.TickIndex}")));
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
        w.Run(DoorSwitchPolicy.WindowTicks, 56, 44);
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
        intoMatch.Run(DoorSwitchPolicy.WindowTicks / 2, 98, 50);
        intoMatch.Mode = TickMode.Match;
        intoMatch.Run(DoorSwitchPolicy.WindowTicks / 2, 98, 50);
        Check("  ...and the lobby's seconds count towards the match's eight", intoMatch.Decisions.Count == 1,
            $"{intoMatch.Decisions.Count} decision(s)");
    }

    private static void ADetourTakenAtConnectIsLeftOnceTheRoadRecovers()
    {
        // The connect started on vn-2-hk because hk was slow. hk is back at 43 against 53: inside the "worse" margin,
        // so only the return rule can bring the player back - as after the 2026-09-18 move off sg-4.
        // 52, not 53: 10 ms is exactly the margin, and over eight seconds jitter crosses it now and then.
        // Without a detour, hk is just a way 9 ms faster, and the steady rule takes it after thirty seconds. With one, hk
        // is the way left, which only the return rule's two minutes bring the player back to.
        var plain = new Ways { Current = "vn-2-hk", Others = ["hk"] };
        plain.Run(DoorSwitchPolicy.ReturnWindowTicks * 2, 52, 43);
        Check("without a detour at connect, 43 against 52 is taken by the steady rule, not as a return",
            plain.Decisions is [{ Steady: true, Return: false, To: "hk" }] &&
            plain.Decisions[0].TickIndex + 1 == DoorSwitchPolicy.SteadyWindowTicks,
            string.Join(", ", plain.Decisions.Select(d => $"{d.From}->{d.To} at {d.TickIndex}")));

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
        Check("  ...and never as a return while the tunnel is on another way", !elsewhere.Decisions.Any(d => d.Return),
            string.Join(", ", elsewhere.Decisions.Select(d => $"{d.From}->{d.To}{(d.Return ? " (return)" : "")}")));
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

    private static void ATunnelKeptByAMoveGoesAfterTheNextMatch()
    {
        // Readings every 5 s from the move at 0. The lobby sends no game UDP; a match sends 25 a second.
        {
            var clock = new HeldTunnelClock(0);
            string? why = null;
            long packets = 0;
            long t;
            for (t = 5_000; t <= 300_000 && why is null; t += 5_000) why = clock.Feed(t, packets);
            Check("a held tunnel stays while the player is in the lobby", why is null, $"went at {t - 5_000} ms: {why}");
            for (; t <= 600_000 && why is null; t += 5_000) why = clock.Feed(t, packets);
            Check("  ...and goes ten minutes after the move with no match", why == "time" && t - 5_000 == 600_000,
                $"{why} at {t - 5_000} ms");
        }
        {
            var clock = new HeldTunnelClock(0);
            long packets = 0;
            clock.Feed(30_000, packets);
            string? why = null;
            long t;
            for (t = 35_000; t <= 200_000 && why is null; t += 5_000)
            {
                packets += 125;
                why = clock.Feed(t, packets);
            }
            Check("the next match retires it once it has run a minute on the new relay",
                why == "match" && t - 5_000 == 90_000, $"{why} at {t - 5_000} ms");
        }
        {
            var clock = new HeldTunnelClock(0);
            long packets = 0;
            clock.Feed(5_000, packets);
            string? why = null;
            for (long t = 10_000; t <= 120_000 && why is null; t += 5_000)
            {
                // A trickle under the match rate - a result screen, a lobby ping - is not a match.
                packets += 20;
                why = clock.Feed(t, packets);
            }
            Check("a trickle under the match rate never retires it", why is null, why ?? "");
        }
        {
            var clock = new HeldTunnelClock(0);
            long packets = 0;
            clock.Feed(5_000, packets);
            for (long t = 10_000; t <= 40_000; t += 5_000) clock.Feed(t, packets += 125);
            // Home reconnects: the new tunnel counts from zero, and the match has to be seen for a minute on it again.
            var afterReconnect = clock.Feed(45_000, 0);
            string? why = null;
            packets = 0;
            long at = 0;
            for (long t = 50_000; t <= 200_000 && why is null; t += 5_000)
            {
                why = clock.Feed(t, packets += 125);
                at = t;
            }
            Check("a reconnect of home starts the minute again", afterReconnect is null && why == "match" && at == 105_000,
                $"{why} at {at} ms");
        }
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

    private static void AGameThatHoldsItsSessionStaysOnceItHasSent()
    {
        // Aniimo: one TCP connection from login to quit; a move to another relay throws the player back to login.
        var aniimo = new GamePingBooster.Core.Profiles.GameEntry { Id = "aniimo", TcpGameplay = true, PersistentSession = true };
        var pubg = new GamePingBooster.Core.Profiles.GameEntry { Id = "pubg" };
        Check("a game that holds its session is never re-measured between matches", !SessionHold.AllowsRescan(aniimo), "rescan allowed");
        Check("  ...nor moved once it has sent to its servers", !SessionHold.AllowsMove(aniimo, sessionBegun: true), "moved");
        Check("  ...but may still leave a relay before its first packet", SessionHold.AllowsMove(aniimo, sessionBegun: false), "held");
        Check("tcpGameplay alone does not hold the session",
            SessionHold.AllowsRescan(new GamePingBooster.Core.Profiles.GameEntry { Id = "x", TcpGameplay = true }), "held");
        Check("every other game is judged by the gap as before",
            SessionHold.AllowsRescan(pubg) && SessionHold.AllowsMove(pubg, sessionBegun: true) && SessionHold.AllowsRescan(null),
            "held");
        Check("a game that holds its session waits for the move off a relay not for it before its routes go in",
            SessionHold.RoutesWaitForMove(aniimo, relayServesGame: false), "routes in at once");
        Check("  ...and not on a relay that carries it", !SessionHold.RoutesWaitForMove(aniimo, relayServesGame: true), "waited");
        Check("every other game puts its routes in at once, on any relay",
            !SessionHold.RoutesWaitForMove(pubg, relayServesGame: false) && !SessionHold.RoutesWaitForMove(null, relayServesGame: false),
            "waited");
        var read = System.Text.Json.JsonSerializer.Deserialize(
            """{"games":[{"id":"aniimo","persistentSession":true},{"id":"pubg"}]}""",
            GamePingBooster.Core.Profiles.ProfileJsonContext.Default.ProfileBundle);
        Check("the profile field reads as persistentSession, absent as false",
            read is { Games: [{ PersistentSession: true }, { PersistentSession: false }] }, "not read");
    }

    private static void AReconnectKeepsATcpGamesRoutes()
    {
        // Aniimo's gateway, opened at launch over the line (192.168.1.5), and its match server through the tunnel (10.77.0.2).
        static System.Net.IPAddress Ip(string s) => System.Net.IPAddress.Parse(s);
        var tunnel = Ip("10.77.0.2");
        GamePingBooster.Core.Paths.TcpHold.Flow[] live =
        [
            new(Ip("192.168.1.5"), Ip("8.219.197.133")),   // gateway, on the line
            new(tunnel, Ip("8.219.10.20")),                // match, through the tunnel
            new(Ip("192.168.1.5"), Ip("142.250.1.1")),     // a browser, outside the game's ranges
        ];
        string[] ranges = ["8.219.0.0/16"];
        Check("connections through the tunnel are counted for a reconnect",
            GamePingBooster.Core.Paths.TcpHold.ThroughTunnel(live, ranges, tunnel) == 1 &&
            GamePingBooster.Core.Paths.TcpHold.ThroughTunnel(live, ["9.0.0.0/8"], tunnel) == 0 &&
            GamePingBooster.Core.Paths.TcpHold.ThroughTunnel(live, ranges, null) == 0, "wrong count");
        Check("  a malformed range is ignored, not thrown on",
            GamePingBooster.Core.Paths.TcpHold.ThroughTunnel(live, ["nonsense", "8.219.10.20/32"], tunnel) == 1, "wrong count");

        var aniimo = new GamePingBooster.Core.Profiles.GameEntry { Id = "aniimo", TcpGameplay = true };
        Check("a reconnect keeps the routes for a TCP game with connections through the tunnel",
            GamePingBooster.Core.Paths.TcpHold.HoldsRoutesOnReconnect(aniimo, riding: 1), "pulled");
        Check("  not with none, and never for a UDP game",
            !GamePingBooster.Core.Paths.TcpHold.HoldsRoutesOnReconnect(aniimo, riding: 0) &&
            !GamePingBooster.Core.Paths.TcpHold.HoldsRoutesOnReconnect(new GamePingBooster.Core.Profiles.GameEntry { Id = "pubg" }, riding: 3) &&
            !GamePingBooster.Core.Paths.TcpHold.HoldsRoutesOnReconnect(null, riding: 3), "held");
    }

    // ------------------------------------------------------------------ the lobby record

    private static void ALobbyBeforeAMatchIsOneRecord()
    {
        // The owner's wait on 2026-10-07: sg-2 in the lobby with vn-5-sg2 beside it, then a match.
        var w = new Ways { Current = "sg-2", Others = ["vn-5-sg2", "vn-3-sg2"], Mode = TickMode.Lobby };
        w.Run(100, _ => 58, _ => 49, i => i % 10 == 0 ? null : 70);
        Check("a lobby is not written while it lasts", w.Lobbies.Count == 0 && w.LobbiesStarted == 1,
            $"{w.Lobbies.Count} record(s), {w.LobbiesStarted} started");
        w.Mode = TickMode.Match;
        w.Run(1, 58, 49, 70);
        Check("  ...and is written once, when the match starts", w.Lobbies is [{ EndedBy: "match", TickCount: 100 }],
            string.Join(", ", w.Lobbies.Select(l => $"{l.EndedBy} {l.TickCount}")));
        if (w.Lobbies.Count != 1) return;
        var l = w.Lobbies[0];
        var ways = l.Ways.ToDictionary(p => p.Key, p => p.Value);
        Check("  ...with the way in use first, then the others",
            l.Ways.Select(p => p.Key).SequenceEqual(["sg-2", "vn-5-sg2", "vn-3-sg2"]), string.Join(", ", l.Ways.Select(p => p.Key)));
        Check("  ...each way's median",
            ways["sg-2"].P50 is > 57 and < 59 && ways["vn-5-sg2"].P50 is > 48 and < 50 && ways["vn-3-sg2"].P50 is > 69 and < 71,
            string.Join(", ", l.Ways.Select(p => $"{p.Key} {p.Value.P50:F1}")));
        Check("  ...and what each way lost", ways["vn-3-sg2"] is { Sent: 100, Lost: 10 } && ways["vn-5-sg2"].Lost == 0,
            $"vn-3-sg2 {ways["vn-3-sg2"].Lost}/{ways["vn-3-sg2"].Sent}");
        Check("  ...and every quarter second of it", l.Ticks.Count == 100 && l.TicksSkipped == 0 && l.Seconds == 25,
            $"{l.Ticks.Count} ticks, {l.TicksSkipped} skipped, {l.Seconds} s");
    }

    private static void AShortLobbyIsNoRecord()
    {
        var w = new Ways { Mode = TickMode.Lobby };
        w.Run(LobbyTracker.MinTicks - 1, 58, 49);
        w.Mode = TickMode.Match;
        w.Run(1, 58, 49);
        Check("a lobby under twenty seconds is not written", w.Lobbies.Count == 0, $"{w.Lobbies.Count} record(s)");
    }

    private static void ALongLobbyKeepsItsLastTwoMinutes()
    {
        var w = new Ways { Mode = TickMode.Lobby };
        w.Run(LobbySummary.MaxTicks + 240, 58, 49);
        w.Mode = TickMode.Match;
        w.Run(1, 58, 49);
        Check("a lobby of three minutes keeps its last two in the record, and its figures over all three",
            w.Lobbies is [{ TicksSkipped: 240 }] && w.Lobbies[0].Ticks.Count == LobbySummary.MaxTicks &&
            w.Lobbies[0].Ways[0].Value.Sent == LobbySummary.MaxTicks + 240,
            string.Join(", ", w.Lobbies.Select(l => $"{l.Ticks.Count} kept, {l.TicksSkipped} skipped, {l.Ways[0].Value.Sent} sent")));
    }

    private static void AMoveInTheLobbyEndsTheRecord()
    {
        var w = new Ways { Current = "sg-2", Others = ["vn-5-sg2"], Mode = TickMode.Lobby };
        w.Run(160, 58, 49);
        w.Current = "vn-5-sg2";
        w.Others = ["sg-2"];
        w.Run(100, 49, 58);
        Check("a move in the lobby ends its record and starts another",
            w.Lobbies is [{ EndedBy: "moved", Door: "sg-2", TickCount: 160 }] && w.LobbiesStarted == 2,
            string.Join(", ", w.Lobbies.Select(l => $"{l.EndedBy} {l.Door} {l.TickCount}")) + $", {w.LobbiesStarted} started");
        var lane = new Ways { Mode = TickMode.Lobby, Lane = 50001 };
        lane.Run(100, 58, 49);
        lane.Lane = 50002;
        lane.Run(1, 58, 49);
        Check("  ...so does a move to another lane", lane.Lobbies is [{ EndedBy: "moved" }], $"{lane.Lobbies.Count} record(s)");
    }

    private static void AHoleDoesNotEndTheLobbyButLeavingDoes()
    {
        var w = new Ways { Mode = TickMode.Lobby };
        w.Run(100, 58, 49);
        w.Mode = TickMode.Idle;
        w.Run(LobbyTracker.GapTicks, 58, 49);
        w.Mode = TickMode.Lobby;
        w.Run(20, 58, 49);
        Check("two seconds that compared nothing do not end a lobby", w.Lobbies.Count == 0 && w.LobbiesStarted == 1,
            $"{w.Lobbies.Count} record(s), {w.LobbiesStarted} started");
        w.Mode = TickMode.Idle;
        w.Run(LobbyTracker.GapTicks + 1, 58, 49);
        Check("  ...more do: the game left the lobby", w.Lobbies is [{ EndedBy: "left", TickCount: 120 }],
            string.Join(", ", w.Lobbies.Select(l => $"{l.EndedBy} {l.TickCount}")));
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
        public LobbyTracker Lobby { get; } = new();
        public List<LobbySummary> Lobbies { get; } = [];
        public int LobbiesStarted { get; private set; }

        private void Feed(QualityTick tick)
        {
            if (Policy.Feed(tick) is { } decision) Decisions.Add(decision);
            var (ended, started) = Lobby.Feed(tick);
            if (ended is not null) Lobbies.Add(ended);
            if (started) LobbiesStarted++;
        }
        public string Current { get; set; } = "sg-2";
        public int Lane { get; set; }
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
                    CurrentLane = Lane,
                    DoorIds = Others,
                    DoorSent = Enumerable.Repeat(true, Others.Length).ToArray(),
                    DoorMs = others.Select(o => o(i) is { } v ? v + Noise() : (double?)null).ToArray(),
                };
                _index++;
                Feed(tick);
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
                Feed(tick);
            }
        }

        /// <summary>Measured ticks as they were: the current way's pong, then each of <see cref="Others"/>, as (value, sent).</summary>
        public void Replay(IEnumerable<(double? Ms, bool Sent)[]> ticks)
        {
            foreach (var t in ticks)
            {
                var tick = new QualityTick(_index, new DateTimeOffset(2026, 10, 7, 10, 40, 0, TimeSpan.Zero)
                    .AddMilliseconds(_index * SpikeDetector.TickMs))
                {
                    Active = Mode == TickMode.Match,
                    Lobby = Mode == TickMode.Lobby,
                    RelayProcessSent = t[0].Sent,
                    RelayProcessMs = t[0].Ms,
                    CurrentDoor = Current,
                    CurrentLane = Lane,
                    DoorIds = Others,
                    DoorSent = t.Skip(1).Select(o => o.Sent).ToArray(),
                    DoorMs = t.Skip(1).Select(o => o.Ms).ToArray(),
                };
                _index++;
                Feed(tick);
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

        /// <summary>
        /// Feeds a recorded series as is (null = lost): a calm minute at the recording's own normal first, the series, then
        /// calm again. The relay pong and the datacentre echo follow the wire when not given.
        /// </summary>
        public void Replay(double calmGateway, double calmWire, double?[] gateway, double?[] wire,
            double?[]? process = null, double?[]? datacentre = null)
        {
            for (var i = 0; i < 240; i++) Feed(Raw(calmGateway + Math.Abs(Noise()) / 3, calmWire + Noise() / 3));
            var count = Math.Min(gateway.Length, wire.Length);
            for (var i = 0; i < count; i++)
            {
                Feed(Raw(gateway[i], wire[i], process?[i] ?? wire[i] + 1, datacentre?[i] ?? wire[i] + 2));
            }
            for (var i = 0; i < 12; i++) Feed(Raw(calmGateway, calmWire));
            Events.AddRange(Detector.Flush());
        }

        private QualityTick Raw(double? gateway, double? wire, double? process = null, double? datacentre = null)
        {
            var tick = Tick();
            tick.GatewayMs = gateway;
            tick.RelayWireMs = wire;
            tick.RelayProcessMs = process ?? wire + 1;
            tick.DatacentreMs = datacentre ?? wire + 2;
            return tick;
        }

        public QualityTick Tick(
            double gateway = 0, double wire = 0, double process = 0, double datacentre = 0,
            bool loseWire = false, bool loseProcess = false, bool loseDatacentre = false, bool loseGateway = false,
            double downGap = -1, int downPackets = 13,
            double upGap = -1, int upPackets = 15,
            double? lineDown = null, double localLag = 0, double? other = null)
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
            tick.OtherDownMbps = other;
            tick.OtherUpMbps = other is null ? null : 0.5;
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
