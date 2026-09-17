namespace GamePingBooster.EtwWatch;

/// <summary>
/// Command line. `./gpb etw [game]` fills every one of these from games.json, the same way it
/// fills `capture`'s, so nobody types them - they exist so the two cannot drift apart.
/// </summary>
internal sealed class Options
{
    public List<string> Processes { get; } = [];
    public string GameId { get; private set; } = "";
    public string GameName { get; private set; } = "";

    /// <summary>0 when the game has no datacentre probes.</summary>
    public int ProbePort { get; private set; }

    /// <summary>Outbound packets above which an address counts as a server - capture's -MinPackets,
    /// and the same default.</summary>
    public int MinPackets { get; private set; } = GamePingBooster.Service.Discovery.Destinations.ServerMinPackets;

    public string? ObservedPath { get; private set; }
    public string? LandmarkPath { get; private set; }
    public string? ProfilePath { get; private set; }
    public string? ReportPath { get; private set; }
    public TimeSpan StatusInterval { get; private set; } = TimeSpan.FromSeconds(30);

    public const string Usage =
        "gpb-etwwatch --process <name>[,<name>] [--game-id id] [--game-name name] [--probe-port n]\n" +
        "             [--min-packets n] [--observed file] [--landmarks file] [--profile file]\n" +
        "             [--report file] [--status-seconds n]\n" +
        "Normally run as `./gpb etw [game]`, which fills all of these from games.json.";

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{name} needs a value.");
            switch (name)
            {
                case "--process":
                    options.Processes.AddRange(Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p));
                    break;
                case "--game-id": options.GameId = Value(); break;
                case "--game-name": options.GameName = Value(); break;
                case "--probe-port": options.ProbePort = int.Parse(Value()); break;
                case "--min-packets": options.MinPackets = int.Parse(Value()); break;
                case "--observed": options.ObservedPath = Value(); break;
                case "--landmarks": options.LandmarkPath = Value(); break;
                case "--profile": options.ProfilePath = Value(); break;
                case "--report": options.ReportPath = Value(); break;
                case "--status-seconds": options.StatusInterval = TimeSpan.FromSeconds(Math.Max(5, int.Parse(Value()))); break;
                default: throw new ArgumentException($"Unknown argument '{name}'.");
            }
        }

        if (options.Processes.Count == 0) throw new ArgumentException("--process is required.");
        if (options.GameName.Length == 0) options.GameName = options.Processes[0];
        if (options.GameId.Length == 0) options.GameId = options.Processes[0].ToLowerInvariant();
        return options;
    }
}
