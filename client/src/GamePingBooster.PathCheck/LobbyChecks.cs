using GamePingBooster.Core.Paths;

namespace GamePingBooster.PathCheck;

internal static partial class Program
{
    /// <summary>
    /// <see cref="LobbyGate"/> - when the region planner may measure after a connect (docs/MULTI-TUNNEL.md 5.5). Driven
    /// as the supervisor drives it: a pass every 5 s with the game's UDP count over every tunnel.
    /// </summary>
    private static void LobbyChecks()
    {
        var home = new FakeTunnel("home");

        {
            var gate = new LobbyGate();
            gate.Arm(home, 0, 0);
            Check("Nothing sent since the routes went in: the first pass is the lobby", gate.Observe(home, 5_000, 0));
        }
        {
            var gate = new LobbyGate();
            gate.Arm(home, 0, 7_400);
            Check("The same after a game change on one connection - the count is taken from the routes going in",
                gate.Observe(home, 5_000, 7_400));
        }
        {
            var gate = new LobbyGate();
            gate.Arm(home, 0, 0);
            var first = gate.Observe(home, 5_000, 150);
            var second = gate.Observe(home, 10_000, 450);
            var third = gate.Observe(home, 15_000, 750);
            Check("A connect in the middle of a match waits - however long it lasts", !first && !second && !third);
            var quiet1 = gate.Observe(home, 20_000, 752);
            var quiet2 = gate.Observe(home, 25_000, 755);
            Check("and the plan comes after two quiet passes once it ends", !quiet1 && quiet2);
        }
        {
            var gate = new LobbyGate();
            gate.Arm(home, 0, 0);
            var first = gate.Observe(home, 5_000, 5);
            var second = gate.Observe(home, 10_000, 10);
            Check("A lobby trickle (Naraka, 1 a second) is the lobby after two passes, counted from the routes going in",
                !first && second);
        }
        {
            var gate = new LobbyGate();
            var first = gate.Observe(home, 5_000, 0);
            var second = gate.Observe(home, 10_000, 0);
            var third = gate.Observe(home, 15_000, 0);
            Check("Never armed (routes already in before a reset): the old rule - a first look, then two quiet passes",
                !first && !second && third);
        }
        {
            var gate = new LobbyGate();
            gate.Arm(home, 0, 0);
            gate.Observe(home, 5_000, 0);
            gate.Reset();
            var again = gate.Observe(home, 10_000, 0);
            Check("After the planner ran, a retry does not take the shortcut", !again);
        }
        {
            var gate = new LobbyGate();
            gate.Arm(home, 0, 0);
            var replaced = new FakeTunnel("home after a reconnect");
            Check("Another tunnel starts again", !gate.Observe(replaced, 5_000, 0));
        }
        {
            var gate = new LobbyGate();
            gate.Arm(home, 0, 0);
            var first = gate.Observe(home, 5_000, 5);
            var early = gate.Observe(home, 5_200, 6);
            var second = gate.Observe(home, 10_000, 10);
            Check("A pass under 2 s after the last neither counts nor breaks the count (one packet in 200 ms is not 5 a second)",
                !first && !early && second);
        }
        {
            var gate = new LobbyGate();
            gate.Arm(home, 0, 0);
            gate.Observe(home, 5_000, 100);
            var quiet = gate.Observe(home, 10_000, 105);
            var busy = gate.Observe(home, 15_000, 400);
            var quietAgain = gate.Observe(home, 20_000, 402);
            var due = gate.Observe(home, 25_000, 404);
            Check("A busy pass between quiet ones starts the count again", !quiet && !busy && !quietAgain && due);
        }
    }
}
