using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.mDNS;
using VRCFaceTracking.Core.OSC.Query;
using VRCFaceTracking.Core.Params;

namespace VRCFaceTracking.Core;

public class OscQueryConfigParser(
    ILogger<OscQueryConfigParser> parserLogger,
    AvatarConfigParser configParser,
    MulticastDnsService multicastDnsService)
{
    private readonly HttpClient _httpClient = new();

    public async Task<(IAvatarInfo avatarInfo, List<Parameter> relevantParameters)?> ParseAvatar(string avatarId = null)
    {
        try
        {
            if (multicastDnsService.VrchatClientEndpoint == null)
            {
                return null;
            }

            // Request on the endpoint + /avatar/parameters
            var httpEndpoint = "http://" + multicastDnsService.VrchatClientEndpoint + "/avatar";

            // Get the response
            var response = await _httpClient.GetAsync(httpEndpoint);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var raw = await response.Content.ReadAsStringAsync();
            var avatarConfig = JsonConvert.DeserializeObject<OscQueryNode>(raw);
            // Never log the tree itself: for a big avatar it's hundreds of KB per resolve
            // (which drowned latest.log), and passing raw JSON as the log message treats
            // every { in it as a format placeholder.
            parserLogger.LogDebug("Fetched avatar OSCQuery tree ({bytes} bytes)", raw.Length);
            var avatarInfo = new OscQueryAvatarInfo(avatarConfig);

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

            return (avatarInfo, paramList);
        }
        catch (Exception e)
        {
            parserLogger.LogError(e.Message);
            SentrySdk.CaptureException(e, scope => scope.SetExtra("endpoint", multicastDnsService.VrchatClientEndpoint));
            return null;
        }
    }
}
