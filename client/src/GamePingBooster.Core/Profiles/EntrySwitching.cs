namespace GamePingBooster.Core.Profiles;

/// <summary>What entry switching does on one connection. See DoorSwitchPolicy.</summary>
public enum EntrySwitchingMode
{
    /// <summary>The other ways into the relay are not probed at all - the client as it was before switching existed.</summary>
    Off,

    /// <summary>They are probed, and every move the policy would make is written down, not made.</summary>
    Record,

    /// <summary>Moves are made.</summary>
    On,
}

/// <summary>
/// Where a connection's entry-switching mode comes from.
///
/// Two places, in this order. A value somebody typed into config.json wins - that is how one machine is
/// tried before everybody. Otherwise the relay's own setting from the profile, which an operator sets per
/// relay in /admin/relays and which is how moves are turned on for everybody, one relay at a time, and off
/// again without a release. Neither: "record".
///
/// A value in config.json that means nothing - a typo - is "record" rather than a reason to fall through to
/// the relay: whoever typed something meant to decide this machine, and a typo must neither switch probing
/// off nor switch moving on.
/// </summary>
public static class EntrySwitching
{
    public static EntrySwitchingMode? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "off" => EntrySwitchingMode.Off,
        "record" => EntrySwitchingMode.Record,
        "on" => EntrySwitchingMode.On,
        _ => null,
    };

    /// <summary>The mode for a connection, and where it came from - for the log line that says so.</summary>
    public static (EntrySwitchingMode Mode, string Source) Resolve(string? local, string? relay)
    {
        if (!string.IsNullOrWhiteSpace(local)) return (Parse(local) ?? EntrySwitchingMode.Record, "config.json");
        if (Parse(relay) is { } fromRelay) return (fromRelay, "the relay's setting");
        return (EntrySwitchingMode.Record, "the default");
    }
}
