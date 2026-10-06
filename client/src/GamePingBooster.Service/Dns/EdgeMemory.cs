using System.Collections.Concurrent;

namespace GamePingBooster.Service.Dns;

/// <summary>
/// What the unblock resolver has learnt about ONE network - which edges complete a handshake, which names the line
/// cuts - kept across resolvers, so a Disconnect and a Connect on the same line do not start from nothing.
///
/// Why it exists, measured on FPT on 2026-10-02: PUBG's "Initializing..." took seconds longer than it should, and
/// the time was the resolver's. A name it had no fresh verdict for was probed - handshakes to every candidate edge -
/// before it was answered, and the launcher API and anti-cheat live in us-east, 600-900 ms a handshake from Vietnam;
/// the lobby's Global Accelerator, which answers nothing on 443, cost 2.5 s. Verdicts lasted two minutes and the
/// resolver was rebuilt on every Connect (unblocking runs while connected), so every game start paid it again.
///
/// Keyed by the network - the resolvers Windows uses - by UnblockDns: a verdict is about a line, and the next line
/// filters differently. See WorkingEdges.ForAsync for how long a verdict is used, and UnblockDns.PrewarmAsync for the
/// names asked as soon as the player connects.
/// </summary>
internal sealed class EdgeMemory
{
    /// <summary>The line's own edges.</summary>
    public WorkingEdges.Memory Direct { get; } = new();

    /// <summary>Edges as measured through the tunnel.</summary>
    public WorkingEdges.Memory Tunnel { get; } = new();

    /// <summary>Names sent through the tunnel because the line cuts them - LocalResolver's own list.</summary>
    public ConcurrentDictionary<string, DateTimeOffset> Cut { get; } = new();

    /// <summary>Whether each of the profile's tunnel names goes over this line split - see SplitProxy.</summary>
    public ConcurrentDictionary<string, SplitProxy.Verdict> Split { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every claimed name asked on this network, for warming the next connection.</summary>
    public ConcurrentDictionary<string, byte> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);
}
