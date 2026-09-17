using System.Net;
using System.Text.Json;
using GamePingBooster.Core.Profiles;

namespace GamePingBooster.EtwWatch;

/// <summary>
/// What the project already knows about a game, so a report can say which of the addresses it
/// found are new: the capture files (observed and landmarks) and the profile's ranges.
///
/// Read once at start and never written. This tool is a prototype next to `./gpb capture`, and
/// its findings go to its own file until they have been shown to agree with capture's.
/// </summary>
internal sealed class KnownAddresses
{
    private readonly HashSet<IPAddress> _observed = [];
    private readonly HashSet<IPAddress> _landmarkFile = [];
    private readonly HashSet<IPAddress> _profileLandmarks = [];
    private readonly List<IPNetwork> _profileRanges = [];

    public List<string> Notes { get; } = [];

    public static KnownAddresses Load(Options options)
    {
        var known = new KnownAddresses();
        known.ReadList(options.ObservedPath, known._observed, "observed");
        known.ReadList(options.LandmarkPath, known._landmarkFile, "landmarks");
        known.ReadProfile(options);
        return known;
    }

    public string Describe(IPAddress address)
    {
        var parts = new List<string>(3);
        if (_profileRanges.Any(r => r.Contains(address))) parts.Add("profile");
        if (_profileLandmarks.Contains(address)) parts.Add("profile-landmark");
        if (_observed.Contains(address)) parts.Add("observed");
        if (_landmarkFile.Contains(address)) parts.Add("landmarks");
        return parts.Count == 0 ? "NEW" : string.Join("+", parts);
    }

    public bool InProfileRanges(IPAddress address) => _profileRanges.Any(r => r.Contains(address));
    public bool InObserved(IPAddress address) => _observed.Contains(address);
    public bool IsKnownLandmark(IPAddress address) => _landmarkFile.Contains(address) || _profileLandmarks.Contains(address);

    private void ReadList(string? path, HashSet<IPAddress> into, string what)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!File.Exists(path))
        {
            Notes.Add($"no {what} file at {path}");
            return;
        }
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            var first = trimmed.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
            if (IPAddress.TryParse(first, out var address)) into.Add(address);
        }
        Notes.Add($"{into.Count} address(es) in {Path.GetFileName(path)}");
    }

    private void ReadProfile(Options options)
    {
        if (string.IsNullOrWhiteSpace(options.ProfilePath)) return;
        if (!File.Exists(options.ProfilePath))
        {
            Notes.Add($"no profile at {options.ProfilePath}");
            return;
        }

        ProfileBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize(File.ReadAllText(options.ProfilePath), ProfileJsonContext.Default.ProfileBundle);
        }
        catch (JsonException ex)
        {
            Notes.Add($"profile unreadable: {ex.Message}");
            return;
        }

        var exe = options.Processes.Select(p => p + ".exe").ToList();
        var game = bundle?.Games.FirstOrDefault(g => string.Equals(g.Id, options.GameId, StringComparison.OrdinalIgnoreCase))
                   ?? bundle?.Games.FirstOrDefault(g => g.ProcessNames.Any(n => exe.Contains(n, StringComparer.OrdinalIgnoreCase)));
        if (game is null)
        {
            Notes.Add($"no game matching '{options.GameId}' in {Path.GetFileName(options.ProfilePath)}");
            return;
        }

        foreach (var region in game.Regions)
        {
            foreach (var cidr in region.Cidrs)
            {
                if (IPNetwork.TryParse(cidr, out var network)) _profileRanges.Add(network);
            }
            foreach (var landmark in region.Landmarks)
            {
                if (IPAddress.TryParse(landmark, out var address)) _profileLandmarks.Add(address);
            }
        }
        Notes.Add($"{_profileRanges.Count} range(s) and {_profileLandmarks.Count} landmark(s) in {Path.GetFileName(options.ProfilePath)}");
    }
}
