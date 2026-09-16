namespace GamePingBooster.Core.Profiles;

/// <summary>
/// Entries - forwarders in front of a relay - as paths the tunnel measures and connects through
/// exactly like relays, and the rule for when they are worth measuring at all.
///
/// An entry is a machine that forwards UDP, untouched, to its relay (relay/deploy/setup-entry.sh).
/// Nothing on it signs or decrypts anything, so a path through it is the relay itself reached by
/// another road: the same public key, the same session on the relay, a different endpoint. That
/// last part is why a path remembers which relay it ends at - two open paths to one relay are one
/// session, and TunnelEngine.SelectRelayAsync never holds both.
///
/// Written for a VNTT line on 2026-09-13 whose route abroad left Vietnam through Hong Kong: 68 ms to
/// the game direct, 71 through our Singapore relay, while a datacentre in Ho Chi Minh City reached
/// the same relay in 39 ms. The players on lines like that are the ones who never report anything -
/// they see no difference and leave - so the client has to find the entry by itself.
/// </summary>
public static class RelayPaths
{
    /// <summary>
    /// One path per entry of every relay, in profile order. A relay without entries adds nothing,
    /// and neither does an entry missing its id or endpoint.
    /// </summary>
    public static List<RelayEntry> Expand(IEnumerable<RelayEntry> relays)
    {
        var paths = new List<RelayEntry>();
        foreach (var relay in relays)
        {
            foreach (var entry in relay.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Endpoint)) continue;

                paths.Add(new RelayEntry
                {
                    Id = entry.Id,
                    Name = $"{relay.Name} via {(string.IsNullOrWhiteSpace(entry.Location) ? entry.Id : entry.Location)}",
                    // Where the game traffic leaves, which is the relay and not the forwarder.
                    Location = relay.Location,
                    Endpoint = entry.Endpoint,
                    // The forwarder signs nothing; the relay behind it answers the handshake.
                    PublicKey = relay.PublicKey,
                    // A setting of the relay, not of the road to it.
                    EntrySwitching = relay.EntrySwitching,
                    ViaRelayId = relay.Id,
                });
            }
        }
        return paths;
    }

    /// <summary>
    /// The relay a path ends at: a relay's own id, or the id of the relay behind an entry. Two paths
    /// with the same answer are one relayd, and share one session there.
    /// </summary>
    public static string RelayIdOf(RelayEntry path) => path.ViaRelayId ?? path.Id;

    /// <summary>
    /// Every way into one relay - the relay itself, then each entry in front of it - or none when the
    /// profile has no such relay.
    ///
    /// These are the only paths a tunnel may move between while a game runs. The game server sees the
    /// address the relay sends from, and every one of these ends at the same relayd and the same session,
    /// so moving between them changes nothing the server can see. Moving to another RELAY changes that
    /// address, and the match drops.
    /// </summary>
    public static List<RelayEntry> DoorsOf(IEnumerable<RelayEntry> relays, string relayId)
    {
        var relay = relays.FirstOrDefault(r => r.ViaRelayId is null && r.Id.Equals(relayId, StringComparison.OrdinalIgnoreCase));
        if (relay is null) return [];

        var doors = new List<RelayEntry> { relay };
        doors.AddRange(Expand([relay]));
        return doors;
    }

    /// <summary>
    /// How much faster than the player's own connection a path must be to count as helping.
    ///
    /// The same margin tools\Probe-Providers.ps1 uses to say whether a provider is worth a relay: a
    /// few milliseconds is inside what one evening differs from the next, and inside the relay's own
    /// forwarding cost.
    /// </summary>
    public static double HelpMargin(double directMs) => Math.Max(5.0, 0.1 * directMs);

    /// <summary>
    /// True when the best relay does not beat the player's own connection by <see cref="HelpMargin"/>
    /// - the only time entries are worth measuring.
    ///
    /// Scaled to the line rather than a fixed number of milliseconds. 60 ms is excellent to Seoul and
    /// poor to Singapore from Hanoi, and the client already holds the direct figure for this very
    /// line, measured seconds earlier. A line the relays already help never pays for the extra
    /// handshakes.
    /// </summary>
    public static bool WorthTryingEntries(double directMs, double bestEndToEndMs) =>
        bestEndToEndMs > directMs - HelpMargin(directMs);
}
