using System.Net;

namespace GamePingBooster.Service.Dns;

/// <summary>
/// The tunnel, as the unblock resolver sees it: somewhere to send a name's addresses when the line resets any
/// handshake that names it.
///
/// Why the resolver needs this at all - measured on FPT, 2026-10-02. The line lied about PUBG's names AND reset,
/// within 25 ms, every TLS handshake naming prod-live-xenuine, acrt-pcprod or api.steampowered.com, on every edge
/// tried (Akamai, Tencent, AWS), while the same addresses completed for any other name. An honest address is no use
/// when the connection to it is cut by name; only taking the handshake out of the line's sight helps, and the tunnel
/// does that. The profile says which names may go that way (UnblockApp.tunnel) - small APIs, never content.
///
/// Implemented by TunnelEngine. The resolver never asks the tunnel for anything else, and the tunnel never asks the
/// resolver anything: the two halves of the product still share only this and a switch.
/// </summary>
internal interface IUnblockRoutes
{
    /// <summary>True while a tunnel is connected that host routes can be added to.</summary>
    bool Ready { get; }

    /// <summary>
    /// Routes <paramref name="addresses"/> through the tunnel before they are handed out, so the connection the program
    /// opens next goes that way. Cheap to call again with addresses already routed. False when nothing was routed - no
    /// tunnel, or every address refused (a relay's own, a landmark, a private one).
    /// </summary>
    bool Route(string name, IReadOnlyList<IPAddress> addresses);
}
