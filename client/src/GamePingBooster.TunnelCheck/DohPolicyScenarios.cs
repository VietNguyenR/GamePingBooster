using GamePingBooster.Service.Dns;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// Unblocking on a PC where Windows is set to Require DoH (the 2026-10-15 FPT case): the rules point at 127.0.0.53,
/// which has no DoH template, so Windows asks nobody and every claimed name fails while connected. Offline - the
/// decision is pure, read from the two registry values and the verify's counters.
/// </summary>
internal static partial class Program
{
    private static void RequireDohIsNeverWritten()
    {
        Check("no value anywhere writes the rules",
            UnblockDns.Decide(DohPolicy.Decide(null, null)) == UnblockDns.EnableDecision.Write);
        Check("policy 3 (require) skips them",
            UnblockDns.Decide(DohPolicy.Decide(3, null)) == UnblockDns.EnableDecision.SkipDohRequired);
        Check("the DNS client's own parameter 3 skips them when the policy is silent",
            UnblockDns.Decide(DohPolicy.Decide(null, 3)) == UnblockDns.EnableDecision.SkipDohRequired);

        var allowOverRequire = DohPolicy.Decide(2, 3);
        Check("the policy key wins over the parameter, as in Windows",
            !allowOverRequire.Requires && allowOverRequire.Source == "policy",
            allowOverRequire.Describe());
        Check("policy 3 over a parameter 2 still requires", DohPolicy.Decide(3, 2).Requires);
        Check("allow and prohibit write the rules",
            !DohPolicy.Decide(2, null).Requires && !DohPolicy.Decide(1, null).Requires);
        Check("an unknown value is not taken for require", !DohPolicy.Decide(7, null).Requires);

        Check("a DWORD, a QWORD and a string all read",
            DohPolicy.Parse(3) == 3 && DohPolicy.Parse(3L) == 3 && DohPolicy.Parse(" 3 ") == 3);
        Check("junk reads as not set", DohPolicy.Parse("yes") is null && DohPolicy.Parse(null) is null &&
                                       DohPolicy.Parse(new byte[] { 3 }) is null);
        Check("the report text names value, meaning and key",
            DohPolicy.Decide(3, null).Describe() == "3 (require, policy)", DohPolicy.Decide(3, null).Describe());
    }

    private static void TheRulesAreAlwaysVerified()
    {
        // An honest line: no canary was poisoned, so the answer proves nothing - only the resolver being asked does.
        Check("honest line, Windows asked the resolver: verified",
            UnblockDns.Judge(poisonedCount: 0, cleanNow: true, askedSince: 1) == UnblockDns.VerifyOutcome.Verified);
        Check("honest line, a clean answer but the resolver never asked: not verified (the FPT PC)",
            UnblockDns.Judge(0, cleanNow: true, askedSince: 0) == UnblockDns.VerifyOutcome.NotAsked);

        // A poisoned line: a clean canary through Windows is the proof, as before.
        Check("poisoned line, canary now clean: verified",
            UnblockDns.Judge(1, cleanNow: true, askedSince: 2) == UnblockDns.VerifyOutcome.Verified);
        Check("poisoned line, still lying, never asked: Windows ignored the rules",
            UnblockDns.Judge(1, cleanNow: false, askedSince: 0) == UnblockDns.VerifyOutcome.NotAsked);
        Check("poisoned line, still lying though asked: the fault is ours, told apart",
            UnblockDns.Judge(1, cleanNow: false, askedSince: 3) == UnblockDns.VerifyOutcome.AskedStillPoisoned);
    }
}
