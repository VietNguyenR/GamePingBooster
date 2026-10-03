using System.Diagnostics;
using System.Net;
using System.Text.Json;
using GamePingBooster.Core.Net;
using GamePingBooster.Core.Profiles;
using GamePingBooster.Service.Dns;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// The profile's refuse list, offline: from the JSON the licence server sends to the answer the resolver gives.
///
/// Made for prod-live-front.playbattlegrounds.com.cn, the lobby's mainland China mirror, which PUBG asks and which
/// only hangs from Vietnam - a black screen on VNPT and "Initializing..." on FPT, 2026-10-03, gone the moment a
/// hosts line made it fail at once. Every check here is one of the ways that fix could quietly not happen.
/// </summary>
internal static partial class Program
{
    private const string CnLobby = "prod-live-front.playbattlegrounds.com.cn";

    private static async Task RefusedNamesAreAnsweredAsNotExisting()
    {
        // As the server writes it, with the untidy spellings a person types into the dashboard.
        const string json = """
            {"games":[],"relays":[],"unblock":[
              {"id":"pubg","name":"PUBG","scope":["playbattlegrounds.com","pubg.com"],"excluded":[],
               "canary":"prod-live-front.playbattlegrounds.com","tunnel":[],
               "refuse":[".PLAYBATTLEGROUNDS.com.cn.","playbattlegrounds.com"]}
            ]}
            """;
        var bundle = JsonSerializer.Deserialize(json, ProfileJsonContext.Default.ProfileBundle)!;
        Check("the refuse list is read from the profile JSON", bundle.Unblock[0].Refuse.Count == 2,
            $"read {bundle.Unblock[0].Refuse.Count} entries");

        var said = new List<string>();
        var policy = UnblockPolicy.FromProfile(bundle, said.Add, allowBuiltin: false);
        var pubg = policy.Apps.Single();

        Check("a suffix that covers the canary is not refused - the fix could never prove itself",
            !pubg.Refuses("prod-live-front.playbattlegrounds.com") && said.Any(l => l.Contains("covers its canary")),
            string.Join(" / ", said));
        Check("the China mirror is refused, spelled as typed", pubg.Refuses(CnLobby) && pubg.Refuses("playbattlegrounds.com.cn"),
            "not refused");
        Check("the global lobby, and a name that only ends the same way, are not",
            !pubg.Refuses("prod-live-front.playbattlegrounds.com") && !pubg.Refuses("xplaybattlegrounds.com.cn"),
            "refused");
        Check("Windows is told to ask the resolver about the refused suffix",
            pubg.Namespaces.Contains(".playbattlegrounds.com.cn") && pubg.Namespaces.Contains(".playbattlegrounds.com"),
            string.Join(", ", pubg.Namespaces));

        // An older server sends no refuse field at all: nothing is refused and nothing breaks.
        var old = JsonSerializer.Deserialize("""{"games":[],"relays":[],"unblock":[{"id":"pubg","scope":["pubg.com"],"canary":"accounts.pubg.com"}]}""",
            ProfileJsonContext.Default.ProfileBundle)!;
        var oldApp = UnblockPolicy.FromProfile(old, _ => { }, allowBuiltin: false).Apps.Single();
        Check("a profile without the field refuses nothing", !oldApp.Refuses(CnLobby) && oldApp.Namespaces.SequenceEqual([".pubg.com"]),
            string.Join(", ", oldApp.Namespaces));

        // The answer itself, without a socket: every record type, the client's id kept, and no upstream asked - the
        // upstream here is a black hole, so any attempt to ask it would show as seconds, not milliseconds.
        var lines = new List<string>();
        var resolver = new LocalResolver([IPAddress.Parse("192.0.2.1")], policy, lines.Add);
        try
        {
            foreach (var (type, label) in new (ushort, string)[] { (DnsWire.TypeA, "A"), (DnsWire.TypeAaaa, "AAAA"), (65, "HTTPS") })
            {
                var query = DnsWire.BuildQuery(0x4242, CnLobby, type);
                var clock = Stopwatch.StartNew();
                var reply = await resolver.AnswerAsync(query, CancellationToken.None);
                var parsed = DnsWire.Parse(reply, reply.Length);
                Check($"{label} for the China mirror: NXDOMAIN, same id, no answer, in {clock.ElapsedMilliseconds} ms",
                    parsed.RCode == 3 && parsed.Id == 0x4242 && parsed.Addresses.Count == 0 && clock.ElapsedMilliseconds < 200,
                    $"rcode {parsed.RCode}, id {parsed.Id:x}, {parsed.Addresses.Count} address(es)");
            }

            var sub = await resolver.AnswerAsync(DnsWire.BuildQuery(7, "cdn.playbattlegrounds.com.cn"), CancellationToken.None);
            Check("a name under the refused suffix is refused too", DnsWire.Parse(sub, sub.Length).RCode == 3, "it was not");
            Check("the log says it once, not once per question",
                lines.Count(l => l.Contains($"{CnLobby} is refused")) == 1 && resolver.RefusedQueries == 4,
                $"{lines.Count(l => l.Contains("is refused"))} line(s), {resolver.RefusedQueries} counted");
        }
        finally
        {
            await resolver.DisposeAsync();
        }
    }
}
