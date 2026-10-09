using GamePingBooster.Core.Quality;

namespace GamePingBooster.QualityCheck;

/// <summary>
/// DoorChoice: which way into a relay the list shows and a connect to it starts on (2026-10-09). The numbers are the
/// owner's at peak on 2026-10-08: sg-1 at 49 ms direct, 42-43 through an entry.
/// </summary>
internal static partial class Program
{
    private static DoorReading Door(string id, double? ms, int lost = 0) =>
        new(id, id == "sg-1", ms, new PingLoss(40, ms is null ? 0 : 40 - lost));

    private static void DoorChoiceChecks()
    {
        var owner = DoorChoice.Pick([Door("sg-1", 49), Door("vn-2-sg-1", 42.5), Door("vn-5-sg-1", 47)]);
        Check("sg-1 49 direct, 42.5 through vn-2 - the entry, 6.5 ms over the 5 ms margin", owner.Door?.DoorId == "vn-2-sg-1", owner.Reason);

        var close = DoorChoice.Pick([Door("sg-1", 46), Door("vn-2-sg-1", 42.5)]);
        Check("46 direct against 42.5 through an entry - inside the margin, the relay's own address", close.Door?.DoorId == "sg-1", close.Reason);

        var direct = DoorChoice.Pick([Door("sg-1", 41), Door("vn-2-sg-1", 44)]);
        Check("Direct is the fastest - direct", direct.Door?.DoorId == "sg-1", direct.Reason);

        var lossy = DoorChoice.Pick([Door("sg-1", 40, lost: 8), Door("vn-2-sg-1", 44)]);
        Check("Direct 4 ms faster but losing a fifth of its probes - the clean entry", lossy.Door?.DoorId == "vn-2-sg-1", lossy.Reason);

        var lossyEntry = DoorChoice.Pick([Door("sg-1", 49), Door("vn-2-sg-1", 30, lost: 8)]);
        Check("An entry 19 ms faster but losing probes - direct", lossyEntry.Door?.DoorId == "sg-1", lossyEntry.Reason);

        var silent = DoorChoice.Pick([Door("sg-1", null), Door("vn-2-sg-1", 60), Door("vn-5-sg-1", 55)]);
        Check("The relay's own address silent - the fastest entry, whatever the margin", silent.Door?.DoorId == "vn-5-sg-1", silent.Reason);

        var none = DoorChoice.Pick([Door("sg-1", null), Door("vn-2-sg-1", null)]);
        Check("Nothing answered - no way", none.Door is null, none.Reason);

        var large = DoorChoice.Pick([Door("sg-1", 120), Door("vn-2-sg-1", 110)]);
        Check("120 against 110 - 10 ms is under 10% of 110 (11), the relay's own address", large.Door?.DoorId == "sg-1", large.Reason);
    }
}
