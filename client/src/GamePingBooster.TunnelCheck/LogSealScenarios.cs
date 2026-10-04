using GamePingBooster.Service;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// The service log seals names and addresses (LogSeal, 2026-10-04). Offline, and without the operator's private key:
/// what is sealed, what is left readable, and that the file carries the key line ahead of the first sealed line.
/// GPB_WRITE_SEAL_SAMPLE=path writes the sealed lines there, for the operator to open with the private key.
/// </summary>
internal static partial class Program
{
    private static Task NamesAndAddressesAreSealed()
    {
        string[] plain =
        [
            "Unblock: prod-live-front.playbattlegrounds.com.cn -> 116.131.226.149, 123.6.42.122 (0 address(es) dropped, 1162 ms).",
            "Handshake succeeded in 64 ms. Tunnel IP: 10.77.0.161, MTU 1400",
            "Installed 50 lobby route(s) into the virtual adapter (101.72.248.60/32, 103.10.124.4/32) - now, not when the game starts.",
            "Logging to C:\\ProgramData\\GamePingBooster\\logs\\gpb-service.log",
            "Connect took 3.0 s (profile 60 ms, clock 635 ms, relays 2.3 s, adapter 1 ms, routing 50 ms, start 0 ms).",
            "Configuration loaded. Default game: pubg, adapter: Game Ping Booster",
        ];
        var seal = new LogSeal();
        var sealedLines = plain.Select(seal.Seal).ToArray();

        Check("A domain name and everything after it is sealed, the subject stays readable",
            sealedLines[0].StartsWith("Unblock: [enc:" + seal.Id + ":") && !sealedLines[0].Contains("playbattlegrounds"), sealedLines[0]);
        Check("An address is sealed from where it starts",
            sealedLines[1].StartsWith("Handshake succeeded in 64 ms. Tunnel IP: [enc:") && !sealedLines[1].Contains("10.77"), sealedLines[1]);
        Check("A list of routes is sealed", !sealedLines[2].Contains("101.72") && sealedLines[2].StartsWith("Installed 50 lobby route(s) into the virtual adapter ([enc:"), sealedLines[2]);
        Check("A file name is not a domain name", sealedLines[3] == plain[3], sealedLines[3]);
        Check("Times and versions are left alone", sealedLines[4] == plain[4] && sealedLines[5] == plain[5], sealedLines[4]);
        Check("Two seals of the same text differ (fresh IV)", seal.Seal(plain[0]) != sealedLines[0]);
        Check("The key line names the key and carries no name or address",
            seal.KeyLine.StartsWith($"--- log key k={seal.Id} [key:") && LogSeal.FirstSensitive(seal.KeyLine) < 0, seal.KeyLine);

        var dir = Path.Combine(Path.GetTempPath(), "gpb-seal-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var log = new FileLog(dir))
            {
                foreach (var line in plain) log.Write(line);
            }
            var file = File.ReadAllLines(Path.Combine(dir, "gpb-service.log"));
            var keyAt = Array.FindIndex(file, l => l.Contains("[key:"));
            var firstSealed = Array.FindIndex(file, l => l.Contains("[enc:"));
            Check("The log file carries its key line ahead of the first sealed line", keyAt >= 0 && keyAt < firstSealed, $"key at {keyAt}, sealed at {firstSealed}");
            Check("  and no name or address is left in it", !file.Any(l => l.Contains("playbattlegrounds") || l.Contains("116.131") || l.Contains("10.77.0")),
                string.Join(" | ", file));

            var sample = Environment.GetEnvironmentVariable("GPB_WRITE_SEAL_SAMPLE");
            if (!string.IsNullOrEmpty(sample)) File.WriteAllLines(sample, file);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }
}
