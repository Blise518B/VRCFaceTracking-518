using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.mDNS;
using VRCFaceTracking.Core.OSC.Query;
using VRCFaceTracking.Core.Params;
using VRCFaceTracking.Core.Services;

namespace VRCFaceTracking.Core;

public class OscQueryConfigParser(
    ILogger<OscQueryConfigParser> parserLogger,
    AvatarConfigParser configParser,
    MulticastDnsService multicastDnsService)
{
    // Upstream's default timeout is 100 s. VRChat answers on loopback within milliseconds, and
    // AvatarSync runs one load at a time, so a hung VRChat must not hold it for that long.
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly TimeSpan AvatarIdTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reads VRChat's avatar tree and maps every parameter to it, which changes the global
    /// parameter state. When <paramref name="expectedId"/> is given and the tree still shows
    /// another avatar, nothing is touched and the outcome is Stale: VRChat sends /avatar/change
    /// before it has rebuilt the tree (see AvatarSync).
    /// </summary>
    public async Task<(AvatarLoadOutcome outcome, string? avatarId, IAvatarInfo? avatarInfo, List<Parameter>? relevantParameters)>
        LoadAvatar(string? expectedId = null, CancellationToken ct = default)
    {
        try
        {
            if (multicastDnsService.VrchatClientEndpoint == null)
            {
                return (AvatarLoadOutcome.Failed, null, null, null);
            }

            // Request on the endpoint + /avatar/parameters
            var httpEndpoint = "http://" + multicastDnsService.VrchatClientEndpoint + "/avatar";

            // Get the response
            using var response = await _httpClient.GetAsync(httpEndpoint, ct);
            if (!response.IsSuccessStatusCode)
            {
                return (AvatarLoadOutcome.Failed, null, null, null);
            }

            var raw = await response.Content.ReadAsStringAsync(ct);
            var avatarConfig = JsonConvert.DeserializeObject<OscQueryNode>(raw);
            // Never log the tree itself: for a big avatar it's hundreds of KB per resolve
            // (which drowned latest.log), and passing raw JSON as the log message treats
            // every { in it as a format placeholder.
            parserLogger.LogDebug("Fetched avatar OSCQuery tree ({bytes} bytes)", raw.Length);
            var avatarInfo = new OscQueryAvatarInfo(avatarConfig);

            if (!string.IsNullOrEmpty(expectedId) && !string.Equals(avatarInfo.Id, expectedId, StringComparison.Ordinal))
            {
                return (AvatarLoadOutcome.Stale, avatarInfo.Id, null, null);
            }

            // Reset all parameters
            var paramList = new List<Parameter>();
            foreach (var parameter in UnifiedTracking.AllParameters)
            {
                paramList.AddRange(parameter.ResetParam(avatarInfo.Parameters, avatarInfo));
            }

            if (avatarInfo.FullFaceTracking)
            {
                parserLogger.LogInformation("Avatar {avatarId} uses VRChat's native face tracking", avatarInfo.Id);
            }

            // Builds without /avatar/name (everything before 2026.4.1) only have the name in the
            // legacy OSC config JSON. Only the name is taken from there: going through
            // ParseAvatar reset every parameter a second time, from a file that may not
            // carry the native face tracking flag.
            if (string.IsNullOrEmpty(avatarInfo.Name))
            {
                parserLogger.LogInformation($"Attempting to resolve avatar config file for {avatarInfo.Id}");
                var name = await configParser.FindAvatarName(avatarInfo.Id);
                if (!string.IsNullOrEmpty(name))
                {
                    avatarInfo.Name = name;
                    parserLogger.LogInformation($"Successfully found config containing avatar name {avatarInfo.Name}");
                }
                else
                {
                    avatarInfo.Name = "Unknown";
                    parserLogger.LogWarning("Odd. Our attempt to find the legacy osc config json for this avatar failed.");
                }
            }

            return (AvatarLoadOutcome.Loaded, avatarInfo.Id, avatarInfo, paramList);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            parserLogger.LogError(e.Message);
            SentrySdk.CaptureException(e, scope => scope.SetExtra("endpoint", multicastDnsService.VrchatClientEndpoint));
            return (AvatarLoadOutcome.Failed, null, null, null);
        }
    }

    /// <summary>
    /// The avatar VRChat has on, from its /avatar/change node: a couple of hundred bytes instead
    /// of the whole tree, so AvatarSync can ask every few seconds. Null when there is no OSCQuery
    /// endpoint or VRChat doesn't answer within 2 s. Logs nothing; the caller decides.
    /// </summary>
    public async Task<string?> ReadCurrentAvatarId(CancellationToken ct = default)
    {
        var endpoint = multicastDnsService.VrchatClientEndpoint;
        if (endpoint == null)
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AvatarIdTimeout);
            using var response = await _httpClient.GetAsync($"http://{endpoint}/avatar/change", timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return ParseAvatarIdNode(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// VRChat's answer for /avatar/change, e.g.
    /// <c>{"FULL_PATH":"/avatar/change","ACCESS":3,"TYPE":"s","VALUE":["avtr_..."]}</c>.
    /// </summary>
    public static string? ParseAvatarIdNode(string json)
    {
        try
        {
            var node = JsonConvert.DeserializeObject<OscQueryNode>(json);
            return node?.Value is { Length: > 0 } value ? value[0] as string : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
