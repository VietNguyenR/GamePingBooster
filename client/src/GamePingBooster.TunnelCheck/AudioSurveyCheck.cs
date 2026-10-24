using System.Text;
using GamePingBooster.Service.Discovery;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// The PUBG audio survey against the real registry, without sending it:
///
///     audioread    the body AudioSurvey would POST to /audio-survey from this machine
/// </summary>
internal static partial class Program
{
    private static int AudioReadMain(string[] args)
    {
        var endpoints = AudioSurvey.ReadEndpoints();
        Console.WriteLine(Encoding.UTF8.GetString(AudioSurvey.Body(endpoints, DateTimeOffset.UtcNow)));
        return endpoints.Count > 0 ? 0 : 1;
    }
}
