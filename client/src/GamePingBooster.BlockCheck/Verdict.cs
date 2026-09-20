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
    bool PlaintextDisagreesWithDoh);

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

        var reset = dohResults.Any(t => t.Outcome == TlsOutcome.ResetDuringHandshake);
        // A handshake with no SNI usually ends in a certificate for some other name - the server has
        // no idea which site was wanted, so it presents its default. That is a COMPLETED handshake
        // and it is the whole point of the control: the packets crossed, the TLS exchange ran to the
        // end, and the only thing that changed in the failing case was the name in the hello.
        // Requiring a clean certificate here made the control unable to ever fire.
        var noSniCompleted = tlsWithoutSni.Any(t =>
            dohAddresses.Contains(t.Address) &&
            t.Outcome is TlsOutcome.Ok or TlsOutcome.CertificateNotValid);

        if (reset && noSniCompleted)
        {
            return (Verdict.SniFiltering,
                "the real address completes a handshake when no name is sent and is reset when the " +
                "name is sent");
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

        if (reset)
        {
            return (Verdict.SniFiltering,
                "reset mid-handshake on the real address; the no-SNI control did not complete either, so " +
                "this is a filter of some kind but not proven to key on the name");
        }

        var detail = dohResults.Select(t => t.Address + " " + TlsProbes.Describe(t.Outcome));
        return (Verdict.Inconclusive, "the real address did not serve a valid certificate - " + string.Join(", ", detail));
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
                   "do not read anything into the Steam results.";
        }

        var steam = reports.Where(r => r.Kind != Targets.Label(TargetKind.Control)).ToArray();
        // A name that does not resolve anywhere says nothing about the content path either way, so
        // it is left out of the judgement rather than counted against it.
        var content = steam
            .Where(r => r.Kind == Targets.Label(TargetKind.SteamContent) && r.Verdict != Verdict.NoSuchName)
            .ToArray();
        var contentClean = content.Length > 0 &&
            content.All(r => r.Verdict is Verdict.Ok or Verdict.OkDifferentAddresses);

        var poisoned = steam.Where(r => r.Verdict is Verdict.DnsPoisoning).Select(r => r.Host).ToArray();
        var sni = steam.Where(r => r.Verdict is Verdict.SniFiltering).Select(r => r.Host).ToArray();
        var dead = steam.Where(r => r.Verdict is Verdict.IpBlocked).Select(r => r.Host).ToArray();

        if (poisoned.Length == 0 && sni.Length == 0 && dead.Length == 0)
        {
            return "Nothing on this line is blocked. Either the ISP does not filter Steam, or something " +
                   "on this machine is already working around it.";
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
            lines.Add("DNS cannot fix these - the connection dies after the name is sent. They need the tunnel.");
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

        lines.Add(contentClean
            ? "Content names are clean - downloads must stay on the ISP's own path. Do not route them."
            : "Content names are NOT clean. Read the per-name rows before deciding anything about downloads, " +
              "because tunnelling them is what the relay cannot afford.");

        return string.Join(Environment.NewLine, lines);
    }
}
