using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GamePingBooster.Service.Discovery;

/// <summary>A game server the profile did not cover, as it is reported.</summary>
/// <param name="Id">32 lowercase hex characters, fixed when the server was found, so a retry is not counted twice.</param>
/// <param name="Game">The profile's game id - the licence server's Game.code.</param>
internal sealed record DiscoveredServer(
    string Id, string Game, string Address, IReadOnlyList<int> Ports, long Packets, double Seconds, DateTimeOffset SeenAt);

/// <summary>
/// The body of POST /discovery and the device's signature over it.
///
/// THE OTHER HALF OF THIS CONTRACT is web-service/app/lib/discovery.ts, and its check script
/// (npm run check:discovery) signs the same way: P-256, SHA-256 over <see cref="Domain"/> followed by
/// the exact body bytes, fixed-width r||s. .NET's ECDsa.SignData writes r||s by default; Node's
/// default is DER, which is why the server names the encoding.
///
/// The body is written by hand with Utf8JsonWriter rather than serialised: the bytes signed are the
/// bytes sent, and nothing here depends on reflection, which Native AOT would strip.
/// </summary>
internal static class DiscoveryReport
{
    public const string Domain = "gpb-discovery-v1\n";

    public static byte[] Body(IEnumerable<DiscoveredServer> servers, string? appVersion)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            if (appVersion is not null) json.WriteString("appVersion", appVersion);
            json.WriteStartArray("servers");
            foreach (var server in servers)
            {
                json.WriteStartObject();
                json.WriteString("id", server.Id);
                json.WriteString("game", server.Game);
                json.WriteString("address", server.Address);
                json.WriteStartArray("ports");
                foreach (var port in server.Ports) json.WriteNumberValue(port);
                json.WriteEndArray();
                json.WriteNumber("packets", server.Packets);
                json.WriteNumber("seconds", Math.Round(server.Seconds, 1));
                json.WriteString("seenAt", server.SeenAt.ToString("O"));
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Lowercase hex of r||s over the domain and the body.</summary>
    public static string Sign(ECDsa deviceKey, byte[] body)
    {
        var domain = Encoding.UTF8.GetBytes(Domain);
        var signed = new byte[domain.Length + body.Length];
        domain.CopyTo(signed, 0);
        body.CopyTo(signed, domain.Length);
        return Convert.ToHexStringLower(
            deviceKey.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }
}
