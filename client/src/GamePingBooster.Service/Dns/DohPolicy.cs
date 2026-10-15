using Microsoft.Win32;

namespace GamePingBooster.Service.Dns;

/// <summary>
/// Whether Windows is set to Require DoH - the one setting under which unblocking cannot work at all.
///
/// With DoHPolicy = 3 the DNS client only sends a question to a server it knows a DoH template for. The NRPT rules
/// point the claimed names at 127.0.0.53, which has none, so Windows asks nobody and every claimed name fails with
/// status 9852 (no DNS servers) for as long as the rules are in. The 2026-10-15 FPT customer: Connect took Steam pages
/// and the PUBG lobby down, Disconnect brought them back, and the service log said "Windows never asked the resolver".
/// Their DNS was 8.8.8.8, which Windows knows as dns.google, so with the app off nothing looked wrong.
///
/// Two places hold it: the Group Policy key ("Configure DNS over HTTPS (DoH) name resolution", also what Intune and
/// "tweak" tools write) and the DNS client's own parameters. The policy key wins when both are set, as it does in
/// Windows. Values: 1 prohibit, 2 allow, 3 require.
/// </summary>
internal readonly record struct DohPolicy(int? Value, string Source)
{
    public const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient";
    public const string ParametersKey = @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters";
    public const string ValueName = "DoHPolicy";

    public const int Prohibit = 1;
    public const int Allow = 2;
    public const int Require = 3;

    public static readonly DohPolicy NotSet = new(null, "none");

    /// <summary>Windows asks only servers with a DoH template - NRPT rules to 127.0.0.53 would be ignored.</summary>
    public bool Requires => Value == Require;

    /// <summary>For the log and the report: "3 (require, policy)", or "not set".</summary>
    public string Describe() => Value is { } v
        ? $"{v} ({v switch { Prohibit => "prohibit", Allow => "allow", Require => "require", _ => "unknown" }}, {Source})"
        : "not set";

    /// <summary>
    /// The value Windows goes by, from what the two keys hold (null for a key without it). Pure, so TunnelCheck can
    /// walk the cases without a registry: the policy key wins, the parameters only count when the policy is silent,
    /// and a value outside 1-3 is kept as read - it is not "require", and the verify after the rules catches whatever
    /// it does mean.
    /// </summary>
    public static DohPolicy Decide(int? policy, int? parameters) =>
        policy is { } p ? new(p, "policy")
        : parameters is { } q ? new(q, "parameters")
        : NotSet;

    /// <summary>Both keys, read now. Never throws: a key this process cannot read is a key that says nothing.</summary>
    public static DohPolicy Read() => Decide(ReadValue(PolicyKey), ReadValue(ParametersKey));

    private static int? ReadValue(string path)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return Parse(key?.GetValue(ValueName));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A DWORD as Windows writes it, or the QWORD or string a hand-made .reg file can leave instead.</summary>
    internal static int? Parse(object? raw) => raw switch
    {
        int i => i,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        string s when int.TryParse(s.Trim(), out var n) => n,
        _ => null,
    };
}
