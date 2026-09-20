using GamePingBooster.Core.Net;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GamePingBooster.BlockCheck;

/// <summary>
/// Decides HOW Steam is blocked on the line this runs from, so the fix can be chosen from evidence
/// rather than from one developer's machine.
///
/// The question it settles: a player reports that turning on encrypted DNS alone - no VPN, no
/// tunnel - makes Steam work again while downloads stay at full ISP speed. If that is true across
/// ISPs, the whole feature is a scoped resolver and nothing else: no relay to pay for, no traffic
/// to carry, no per-process interception, and downloads that were never at risk. If it is not true,
/// the same run says which part needs a tunnel and which part must stay off it.
///
/// Three things can be happening, and they need three different products:
///
///   DNS poisoning   the ISP answers with an address that is not Steam's. Encrypted DNS fixes it.
///   SNI filtering   the address is right and the connection is reset once the name is sent in the
///                   TLS hello. DNS cannot fix it; the flow has to leave the country.
///   IP blocked      the real address never answers. Same conclusion, worse.
///
/// Telling them apart takes two things no ordinary resolver call gives you: the raw DNS exchange,
/// including a second reply arriving behind the first, and a TLS handshake judged by whether a
/// trusted CA signed the certificate for that name. Both are here.
///
///     ./gpb blockcheck [label]
///
/// Steam does not need to be running and must not be restarted for this - the tool asks the
/// questions itself. What DOES matter is that no VPN or DNS proxy is up, which it refuses to run
/// without checking.
///
/// It is a plain console program with no packages, on the same footing as ProtocolCheck: this
/// repository carries no test framework and a network measurement is a list of observations.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Addressed by IP, never by name. See <see cref="DnsProbes.QueryDohAsync"/> - resolving the
    /// resolver would hand the censor the first move.
    /// </summary>
    private static readonly string[] PublicResolvers = ["1.1.1.1", "8.8.8.8"];

    /// <summary>Enough addresses to be representative without turning a CDN name into a hundred handshakes.</summary>
    private const int MaxAddressesPerName = 10;

    private static async Task<int> Main(string[] args)
    {
        string? label = null;
        string? jsonPath = null;
        var anyway = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json" when i + 1 < args.Length: jsonPath = args[++i]; break;
                case "--anyway": anyway = true; break;
                case "--help" or "-h" or "/?": Usage(); return 0;
                default:
                    if (args[i].StartsWith('-')) { Console.Error.WriteLine($"unknown option '{args[i]}'"); Usage(); return 2; }
                    label ??= args[i];
                    break;
            }
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

        var startedAt = DateTimeOffset.Now;
        Console.WriteLine();
        Console.WriteLine("Game Ping Booster - block check");
        Console.WriteLine($"  {startedAt:yyyy-MM-dd HH:mm:ss zzz}" + (label is null ? "" : $"   label: {label}"));
        Console.WriteLine();

        var line = Line.Read();
        Console.WriteLine("Line");
        Console.WriteLine("  Windows resolvers : " + (line.Resolvers.Length == 0
            ? "(none)"
            : string.Join(", ", line.Resolvers.Select(r => r.ToString()))));
        foreach (var adapter in line.Adapters) Console.WriteLine("  adapter up        : " + adapter);
        Console.WriteLine();

        if (line.Interference.Length > 0 && !anyway)
        {
            Console.Error.WriteLine("Refusing to run: this machine is not on its ISP's own path.");
            foreach (var found in line.Interference) Console.Error.WriteLine("  - " + found);
            Console.Error.WriteLine();
            Console.Error.WriteLine("Everything would come back clean and the report would say the ISP blocks");
            Console.Error.WriteLine("nothing, which is the one wrong answer this tool can give. Turn the tunnel");
            Console.Error.WriteLine("off - Cloudflare WARP included - and run it again.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("If one of those is a false alarm, re-run with --anyway and say so in the label.");
            return 3;
        }

        if (line.Interference.Length > 0)
        {
            foreach (var found in line.Interference) Warn("--anyway: ignoring " + found);
            Console.WriteLine();
        }

        // The system resolve below reads Windows' cache, which will still be holding whatever the
        // last VPN session put there. Flushed rather than asked for, because "run ipconfig /flushdns
        // first" is one more instruction to forget and the whole run is void when it is forgotten.
        Console.WriteLine(FlushDnsCache()
            ? "  resolver cache    : flushed"
            : "  resolver cache    : NOT flushed (needs an elevated terminal) - system answers may be stale");
        Console.WriteLine();

        using var http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = System.Net.DecompressionMethods.None,
        })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        Console.WriteLine($"Probing {Targets.All.Length} names: plaintext DNS to every resolver Windows declares and");
        Console.WriteLine("to 1.1.1.1 / 8.8.8.8, the same questions over DoH, then TLS to every address that comes back.");
        Console.WriteLine();

        var reports = new List<TargetReport>();

        foreach (var target in Targets.All)
        {
            if (cancellation.IsCancellationRequested) break;
            reports.Add(await ProbeAsync(http, line, target, cancellation.Token));
        }

        var summary = Judge.Summarise(reports);

        Console.WriteLine(new string('-', 78));
        Console.WriteLine();
        Console.WriteLine(summary);
        Console.WriteLine();

        var path = jsonPath ?? Path.Combine(
            Directory.GetCurrentDirectory(), $"blockcheck-{startedAt:yyyyMMdd-HHmmss}.json");

        var run = new RunReport(
            "gpb-blockcheck",
            startedAt.ToString("o"),
            label,
            line.Adapters,
            [.. line.Resolvers.Select(r => r.ToString())],
            line.Interference,
            [.. reports],
            summary);

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(run, JsonOptions), cancellation.Token);
        Console.WriteLine($"Full measurements -> {path}");
        Console.WriteLine("Run this on one line per ISP and compare the files; one machine decides nothing.");
        Console.WriteLine();

        return 0;
    }

    private static async Task<TargetReport> ProbeAsync(
        HttpClient http, LineState line, Target target, CancellationToken ct)
    {
        Console.WriteLine($"{target.Host}  [{Targets.Label(target.Kind)}]");

        var (systemAddresses, systemError) = await DnsProbes.SystemResolveAsync(target.Host, ct);
        Console.WriteLine("  system            : " + (systemError ?? Join(systemAddresses)));

        var ispResults = new List<PlainResult>();
        foreach (var resolver in line.Resolvers)
        {
            var result = await DnsProbes.QueryAsync(resolver, target.Host, ct);
            ispResults.Add(result);
            Console.WriteLine($"  {resolver,-17} : " + DescribePlain(result));

            if (result.Contradicted)
            {
                // Worth its own line and its own words: this is the only observation in the run that
                // proves tampering on its own, with no reasoning in between.
                Warn("    two different answers to one query - an on-path device answered first");
            }
        }

        var publicPlain = new List<PlainResult>();
        var doh = new List<DohResult>();

        foreach (var resolver in PublicResolvers)
        {
            var plain = await DnsProbes.QueryAsync(IPAddress.Parse(resolver), target.Host, ct);
            publicPlain.Add(plain);
            Console.WriteLine($"  {resolver + " plain",-17} : " + DescribePlain(plain));

            var encrypted = await DnsProbes.QueryDohAsync(http, resolver, target.Host, ct);
            doh.Add(encrypted);
            Console.WriteLine($"  {resolver + " DoH",-17} : " + (encrypted.Error is not null
                ? encrypted.Error
                : $"{Join(encrypted.Addresses)}  ({encrypted.ElapsedMs} ms)"));
        }

        // Same operator, same question, two transports. Disjoint answers mean the plaintext one did
        // not come from them. Evidence only - it never decides the verdict on its own, because a CDN
        // is allowed to answer two queries differently and this is the place that would misread it.
        var disagrees = false;
        for (var i = 0; i < PublicResolvers.Length; i++)
        {
            var plain = publicPlain[i].Addresses;
            var encrypted = doh[i].Addresses;
            if (plain.Length > 0 && encrypted.Length > 0 && !plain.Intersect(encrypted).Any()) disagrees = true;
        }

        var dohAddresses = doh.Where(d => d.Error == null).SelectMany(d => d.Addresses).ToHashSet();

        var candidates = PickCandidates(
            (doh.Where(d => d.Error == null).SelectMany(d => d.Addresses).ToArray(), 3),
            (ispResults.SelectMany(r => r.Addresses).ToArray(), 3),
            (systemAddresses, 2),
            (publicPlain.SelectMany(r => r.Addresses).ToArray(), 2));

        var tls = new List<TlsResult>();
        var withoutSni = new List<TlsResult>();

        foreach (var address in candidates)
        {
            var result = await TlsProbes.ProbeAsync(IPAddress.Parse(address), target.Host, ct);
            tls.Add(result);
            Console.WriteLine($"  TLS {address,-15} : " + DescribeTls(result));

            // The control that separates "they filter the name" from "they filter the address".
            // Only worth running where the address is one DoH vouched for and the handshake failed.
            if (!result.Authentic && dohAddresses.Contains(address) &&
                result.Outcome is TlsOutcome.ResetDuringHandshake or TlsOutcome.HandshakeTimedOut)
            {
                var control = await TlsProbes.ProbeAsync(IPAddress.Parse(address), string.Empty, ct);
                withoutSni.Add(control);
                Console.WriteLine($"  TLS {address,-15} : no SNI -> " + DescribeTls(control));
            }
        }

        // Printed here rather than next to the queries, because on its own it is almost always a
        // CDN rotating its answers between two queries seconds apart - which it is entitled to do,
        // and which made this warning fire on every Akamai name in the first run. It is only worth
        // a player's attention when an address that ONLY the plaintext answer named also fails to
        // serve a valid certificate. The raw disagreement still goes to the JSON either way.
        if (disagrees)
        {
            var plaintextOnly = publicPlain
                .SelectMany(r => r.Addresses)
                .Where(a => !dohAddresses.Contains(a))
                .ToHashSet();

            if (tls.Any(t => plaintextOnly.Contains(t.Address) && !t.Authentic))
            {
                Warn("    an address that only the plaintext answer named does not serve a valid certificate");
            }
        }

        var (verdict, reason) = Judge.Decide(doh, ispResults, tls, withoutSni);

        var colour = verdict switch
        {
            Verdict.Ok or Verdict.OkDifferentAddresses => ConsoleColor.Green,
            Verdict.DnsPoisoning => ConsoleColor.Yellow,
            Verdict.SniFiltering or Verdict.IpBlocked => ConsoleColor.Red,
            _ => ConsoleColor.DarkGray,
        };

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = colour;
        Console.WriteLine($"  => {Judge.Describe(verdict)}");
        Console.ForegroundColor = previous;
        Console.WriteLine($"     {reason}");
        Console.WriteLine();

        return new TargetReport(
            target.Host, Targets.Label(target.Kind), target.Why, verdict, reason,
            systemAddresses, systemError,
            [.. ispResults], [.. publicPlain], [.. doh], [.. tls], [.. withoutSni],
            ispResults.Any(r => r.Contradicted), disagrees);
    }

    /// <summary>
    /// Which addresses get a handshake, taken in order from each source rather than from one flat list.
    ///
    /// The cap is needed: an Akamai name answers with eight addresses and a run asks six resolvers.
    /// But a cap applied to a concatenation drops whichever source comes last, and in the first run
    /// that was DoH - leaving api.steampowered.com with six successful handshakes and a verdict of
    /// "inconclusive", because not one of them was an address the judge defines its answer against.
    /// Taking a few from each source in priority order cannot do that.
    /// </summary>
    private static string[] PickCandidates(params (string[] Addresses, int Take)[] sources)
    {
        var picked = new List<string>();

        foreach (var (addresses, take) in sources)
        {
            var taken = 0;
            foreach (var address in addresses)
            {
                if (taken >= take) break;
                if (picked.Contains(address)) continue;
                if (!IPAddress.TryParse(address, out var parsed)) continue;
                if (parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;

                picked.Add(address);
                taken++;
            }
        }

        return [.. picked.Take(MaxAddressesPerName)];
    }

    private static string DescribePlain(PlainResult result)
    {
        if (result.Error is not null && result.Replies.Length == 0) return result.Error;

        var parts = result.Replies.Select(r =>
        {
            var what = r.RCode != 0 ? DnsWire.RCodeName(r.RCode) : Join(r.Addresses);
            return $"{what}  ({r.ElapsedMs} ms)";
        });

        return string.Join("  then  ", parts);
    }

    private static string DescribeTls(TlsResult result)
    {
        var head = TlsProbes.Describe(result.Outcome);
        if (result.Outcome == TlsOutcome.Ok && result.PolicyErrors == null)
        {
            return $"{head}  {ShortName(result.CertificateSubject)}  ({result.ConnectMs} ms connect)";
        }

        var detail = result.PolicyErrors ?? result.Detail;
        return detail is null ? head : $"{head}  {detail}";
    }

    /// <summary>The CN alone. A full subject line pushes the rest of the row off the terminal.</summary>
    private static string ShortName(string? subject)
    {
        if (string.IsNullOrEmpty(subject)) return "";

        foreach (var part in subject.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return trimmed;
        }

        return subject;
    }

    private static string Join(IReadOnlyList<string> addresses) => addresses.Count switch
    {
        0 => "(no addresses)",
        <= 3 => string.Join(", ", addresses),
        _ => string.Join(", ", addresses.Take(3)) + $", +{addresses.Count - 3} more",
    };

    private static void Warn(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }

    private static bool FlushDnsCache()
    {
        try { return DnsFlushResolverCache() != 0; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache", SetLastError = true)]
    private static extern int DnsFlushResolverCache();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static void Usage()
    {
        Console.WriteLine("gpb-blockcheck [label] [--json <path>] [--anyway]");
        Console.WriteLine();
        Console.WriteLine("  label     goes in the report, e.g. viettel-hcm. Name the ISP and the city.");
        Console.WriteLine("  --json    where to write the measurements (default: blockcheck-<timestamp>.json here)");
        Console.WriteLine("  --anyway  run even though a VPN or DNS proxy looks active. Rarely right.");
    }
}

internal sealed record RunReport(
    string Tool,
    string StartedAt,
    string? Label,
    string[] Adapters,
    string[] Resolvers,
    string[] Interference,
    TargetReport[] Targets,
    string Summary);
