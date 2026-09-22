namespace GamePingBooster.BlockCheck;

internal enum Verdict
{
    /// <summary>Everyone's answer works. Nothing to fix for this name.</summary>
    Ok,

    /// <summary>The ISP hands out different addresses from DoH, but both serve a valid certificate - a CDN
    /// steering by resolver, not a block. Called out separately so it is never mistaken for poisoning.</summary>
    OkDifferentAddresses,

    /// <summary>DoH's answer works, the ISP's does not. Encrypted DNS alone fixes this name.</summary>
    DnsPoisoning,

    /// <summary>The real address is reachable until the name is spoken. DNS cannot fix this one.</summary>
    SniFiltering,

    /// <summary>The real address swallows packets while the control address does not. Needs a tunnel.</summary>
    IpBlocked,

    /// <summary>No encrypted resolver answered, so there is no trustworthy address to test against.</summary>
    DohUnreachable,

    /// <summary>Every resolver, encrypted ones included, says the name does not exist. The target list is wrong.</summary>
    NoSuchName,

    Inconclusive,
}

internal sealed record TargetReport(
    string Host,
    string Kind,
    string Why,
    Verdict Verdict,
    string Reason,
    string[] SystemAddresses,
    string? SystemError,
    PlainResult[] IspResolvers,
    PlainResult[] PublicPlaintext,
    DohResult[] Doh,
    TlsResult[] Tls,
    TlsResult[] TlsWithoutSni,
    bool AnswerContradicted,
    bool PlaintextDisagreesWithDoh,
    string[] FilteredAddresses);

internal static class Judge
{
    /// <summary>
    /// Turns the measurements for one name into one of a small number of answers.
    ///
    /// The order of the tests is the argument. Everything is judged against the addresses that came
    /// back over DoH, because those are the only ones the path could not tamper with, and every
    /// "works" is a valid certificate rather than a reachable port. Read downwards: is there a
    /// trustworthy answer at all; does it work; does the ISP's answer work too; and if the
    /// trustworthy one does not work, what stopped it.
    /// </summary>
    public static (Verdict Verdict, string Reason) Decide(
        IReadOnlyList<DohResult> doh,
        IReadOnlyList<PlainResult> ispResolvers,
        IReadOnlyList<TlsResult> tls,
        IReadOnlyList<TlsResult> tlsWithoutSni)
    {
        var dohAddresses = doh.Where(d => d.Error == null).SelectMany(d => d.Addresses).Distinct().ToArray();
        if (dohAddresses.Length == 0)
        {
            // Separated because the first run confused the two and drew a conclusion about Steam
            // downloads from a hostname that has not existed for years. NXDOMAIN from an encrypted
            // resolver is not censorship - nobody can poison a channel they cannot read - so it can
            // only mean the name is wrong.
            var answered = doh.Where(d => d.RCode >= 0).ToArray();
            if (answered.Length > 0 && answered.All(d => d.RCode == 3))
            {
                return (Verdict.NoSuchName,
                    "every resolver including the encrypted ones answers NXDOMAIN - this name does not " +
                    "exist and should come off the target list");
            }

            var why = doh.Select(d => d.Resolver + ": " + (d.Error ?? "no addresses"));
            return (Verdict.DohUnreachable, "no encrypted resolver returned an address - " + string.Join(", ", why));
        }

        var ispAddresses = ispResolvers.SelectMany(r => r.Addresses).Distinct().ToArray();
        var authentic = tls.Where(t => t.Authentic).Select(t => t.Address).ToHashSet();

        var dohWorks = dohAddresses.Any(authentic.Contains);
        var ispWorks = ispAddresses.Any(authentic.Contains);

        if (dohWorks)
        {
            if (ispWorks)
            {
                return dohAddresses.OrderBy(a => a).SequenceEqual(ispAddresses.OrderBy(a => a))
                    ? (Verdict.Ok, "the ISP's answer and DoH's agree, and it serves a valid certificate")
                    : (Verdict.OkDifferentAddresses,
                       "different addresses but both serve a valid certificate - CDN steering, not a block");
            }

            if (ispAddresses.Length == 0)
            {
                var errors = ispResolvers.Select(r => r.Server + ": " + (r.Error ?? "empty answer"));
                return (Verdict.DnsPoisoning,
                    "DoH's address works; the ISP's resolver returned nothing - " + string.Join(", ", errors));
            }

            var failures = tls
                .Where(t => ispAddresses.Contains(t.Address) && !t.Authentic)
                .Select(t => t.Address + " " + TlsProbes.Describe(t.Outcome));

            return (Verdict.DnsPoisoning,
                "DoH's address serves a valid certificate; the ISP's does not - " + string.Join(", ", failures));
        }

        // Nothing trustworthy worked. What stopped it decides whether DNS could ever have helped.
        var dohResults = tls.Where(t => dohAddresses.Contains(t.Address)).ToArray();

        // A stall counts the same as a reset, and getting this wrong cost two days.
        //
        // The middlebox does not answer the hello: it holds the connection open and sends the reset
        // about nineteen seconds later. This tool gives a handshake eight seconds, so what it
        // usually SEES is a timeout, and a test that looked only for ResetDuringHandshake reported
        // "inconclusive" for a name whose evidence was complete and sitting in the same report -
        // both addresses stalled with the name, both completed a handshake without it.
        var stalled = dohResults.Any(t =>
            t.Outcome is TlsOutcome.ResetDuringHandshake or TlsOutcome.HandshakeTimedOut);
        // A handshake with no SNI usually ends in a certificate for some other name - the server has
        // no idea which site was wanted, so it presents its default. That is a COMPLETED handshake
        // and it is the whole point of the control: the packets crossed, the TLS exchange ran to the
        // end, and the only thing that changed in the failing case was the name in the hello.
        // Requiring a clean certificate here made the control unable to ever fire.
        var noSniCompleted = tlsWithoutSni.Any(t =>
            dohAddresses.Contains(t.Address) &&
            t.Outcome is TlsOutcome.Ok or TlsOutcome.CertificateNotValid);

        if (stalled && noSniCompleted)
        {
            return (Verdict.SniFiltering,
                "the real address completes a handshake when no name is sent, and stalls when the " +
                "name is sent - the name is what is being filtered");
        }

        if (dohResults.Length > 0 && dohResults.All(t =>
                t.Outcome is TlsOutcome.ConnectTimedOut or TlsOutcome.ConnectRefused))
        {
            return (Verdict.IpBlocked, "the real address never answered a SYN");
        }

        if (dohResults.Any(t => t.Outcome == TlsOutcome.CertificateNotValid))
        {
            var seen = dohResults.First(t => t.Outcome == TlsOutcome.CertificateNotValid);
            return (Verdict.Inconclusive,
                "a certificate arrived that is not valid for this name (" + seen.PolicyErrors + ") - something " +
                "is terminating TLS in the middle");
        }

        if (stalled)
        {
            return (Verdict.SniFiltering,
                "the real address never finished a handshake; the no-SNI control did not complete " +
                "either, so this is a filter of some kind but not proven to key on the name");
        }

        var detail = dohResults.Select(t => t.Address + " " + TlsProbes.Describe(t.Outcome));
        return (Verdict.Inconclusive, "the real address did not serve a valid certificate - " + string.Join(", ", detail));
    }

    /// <summary>
    /// Addresses that are filtered while OTHER addresses for the same name work.
    ///
    /// This exists because the first version of this tool hid the thing it was built to find. On
    /// 2026-09-20 it printed "handshake stalled" for one of store.steampowered.com's addresses and
    /// "no SNI -> wrong certificate" for the same one - a completed handshake the moment the name
    /// was left out - and then reported the name as DNS POISONING and moved on, because
    /// <see cref="Decide"/> stops as soon as ANY trustworthy address works. Two days later the
    /// store was still slow for exactly that reason and it had to be found again by hand.
    ///
    /// A name whose addresses are partly filtered is a separate finding from how it is blocked, so
    /// it is computed separately and never short-circuited.
    /// </summary>
    public static string[] PartlyFiltered(IReadOnlyList<TlsResult> tls)
    {
        // Only meaningful when something DID work: if every address fails, that is a plain block
        // and Decide already says so.
        if (!tls.Any(t => t.Authentic)) return [];

        return
        [
            .. tls.Where(t => t.Outcome is TlsOutcome.ResetDuringHandshake or TlsOutcome.HandshakeTimedOut)
                  .Select(t => t.Address)
                  .Distinct()
        ];
    }

    public static string Describe(Verdict verdict) => verdict switch
    {
        Verdict.Ok => "ok",
        Verdict.OkDifferentAddresses => "ok (different addresses)",
        Verdict.DnsPoisoning => "DNS POISONING",
        Verdict.SniFiltering => "SNI FILTERING",
        Verdict.IpBlocked => "IP BLOCKED",
        Verdict.DohUnreachable => "DoH UNREACHABLE",
        Verdict.NoSuchName => "no such name (bad target)",
        _ => "inconclusive",
    };

    /// <summary>
    /// What the whole run means, and what it tells the project to build.
    ///
    /// Deliberately blunt. The point of running this on several ISPs is to come back with one
    /// sentence per line, and the interesting comparison is between those sentences.
    /// </summary>
    public static string Summarise(IReadOnlyList<TargetReport> reports)
    {
        var control = reports.Where(r => r.Kind == Targets.Label(TargetKind.Control)).ToArray();
        if (control.Length > 0 && control.All(r => r.Verdict is not (Verdict.Ok or Verdict.OkDifferentAddresses)))
        {
            return "The control name failed too. Something is wrong with this line or this run - " +
                   "do not read anything into the rest of these results.";
        }

        var appNames = reports.Where(r => r.Kind != Targets.Label(TargetKind.Control)).ToArray();
        // A name that does not resolve anywhere says nothing about the bulk path either way, so it
        // is left out of the judgement rather than counted against it.
        var bulk = appNames
            .Where(r => r.Kind == Targets.Label(TargetKind.Bulk) && r.Verdict != Verdict.NoSuchName)
            .ToArray();
        var bulkClean = bulk.Length > 0 &&
            bulk.All(r => r.Verdict is Verdict.Ok or Verdict.OkDifferentAddresses);

        var poisoned = appNames.Where(r => r.Verdict is Verdict.DnsPoisoning).Select(r => r.Host).ToArray();
        var sni = appNames.Where(r => r.Verdict is Verdict.SniFiltering).Select(r => r.Host).ToArray();
        var dead = appNames.Where(r => r.Verdict is Verdict.IpBlocked).Select(r => r.Host).ToArray();

        if (poisoned.Length == 0 && sni.Length == 0 && dead.Length == 0 &&
            reports.All(r => r.FilteredAddresses.Length == 0))
        {
            return "Nothing on this line is blocked. Either the ISP does not filter this service, or " +
                   "something on this machine is already working around it.";
        }

        var lines = new List<string>();

        if (poisoned.Length > 0 && sni.Length == 0 && dead.Length == 0)
        {
            lines.Add("Blocking on this line is DNS only: " + string.Join(", ", poisoned) + ".");
            lines.Add("Encrypted DNS scoped to those names is the whole fix. No relay, no tunnel, " +
                      "no per-process work.");
        }

        if (sni.Length > 0)
        {
            lines.Add("SNI filtering on: " + string.Join(", ", sni) + ".");

            // Deliberately not "these need the tunnel", which is what this said until the resolver
            // learned to check its own answers. A CDN name has many edges and only some sit behind
            // the filter, so the first thing to try is handing out one that completes a handshake -
            // measured, not assumed. The tunnel is only the answer when NO edge works, and this
            // tool cannot tell which case it is from one address: it probes what the resolvers
            // named, not the whole CDN.
            lines.Add("Encrypted DNS alone does not fix these: the connection dies after the name is sent. " +
                      "But the addresses here are only the ones the resolvers happened to name - if any " +
                      "other edge of the same CDN completes a handshake, answering with that one fixes it " +
                      "without a tunnel. The tunnel is the answer only when none does.");
        }

        if (dead.Length > 0)
        {
            lines.Add("Addresses black-holed for: " + string.Join(", ", dead) + ". These need the tunnel.");
        }

        if (poisoned.Length > 0 && (sni.Length > 0 || dead.Length > 0))
        {
            lines.Add("DNS-poisoned as well: " + string.Join(", ", poisoned) + ".");
        }

        var badTargets = reports.Where(r => r.Verdict == Verdict.NoSuchName).Select(r => r.Host).ToArray();
        if (badTargets.Length > 0)
        {
            lines.Add("Not measured, because the name does not exist: " + string.Join(", ", badTargets) +
                      ". Fix the target list.");
        }

        // Reported for every name, whatever its verdict. A name can be DNS-poisoned AND have half
        // its addresses filtered, and on the line this was written for, one name was exactly that.
        var partly = reports.Where(r => r.FilteredAddresses.Length > 0).ToArray();
        if (partly.Length > 0)
        {
            lines.Add("Partly filtered - some addresses work and some are reset mid-handshake:");
            foreach (var report in partly)
            {
                lines.Add($"  {report.Host}: {string.Join(", ", report.FilteredAddresses)}");
            }

            lines.Add("Whether these names feel fast is then luck of which address the resolver hands out. " +
                      "Encrypted DNS alone does not fix it - the answer has to be checked before it is used.");
        }

        // Never inferred from another app. Steam's bulk names turned out to resolve to caches
        // inside the country, which made the ISP's own answer the FASTER one for downloads - a fact
        // about Steam on that line, not a rule, and the reason every app declares its own bulk names.
        lines.Add(bulkClean
            ? "Bulk names are clean - downloads must stay on the ISP's own path. Do not route them."
            : "Bulk names are NOT clean. Read the per-name rows before deciding anything about downloads, " +
              "because carrying them is what a relay cannot afford.");

        return string.Join(Environment.NewLine, lines);
    }
}
