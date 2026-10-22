using System.Text.Json;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.Models.ParameterDefinition.FileBased;
using VRCFaceTracking.Core.Params;
using VRCFaceTracking.Core.Services;

namespace VRCFaceTracking.Core;

/// <summary>
/// ConfigParser is responsible for parsing the traditional JSON OSC config that VRChat produces
/// </summary>
public class AvatarConfigParser
{
    private readonly ILogger<AvatarConfigParser> _logger;

    public AvatarConfigParser(ILogger<AvatarConfigParser> parserLogger)
    {
        _logger = parserLogger;
    }

    public async Task<(IAvatarInfo avatarInfo, List<Parameter> relevantParameters)?> ParseAvatar(string newId)
    {
        if (string.IsNullOrEmpty(newId))
        {
            return null;
        }

        var paramList = new List<Parameter>();
            
        /*if (newId.StartsWith("local:"))
        {
            foreach (var parameter in UnifiedTracking.AllParameters_v2.Concat(UnifiedTracking.AllParameters_v1).ToArray())
            {
                paramList.AddRange(parameter.ResetParam(Array.Empty<IParameterDefinition>()));
            }

            // This is a local test avatar, there won't be a config file for it so assume we're using no parameters and just return
            _lastAvatarId = newId;
            return (new NullAvatarDef(newId.Substring(10), newId), paramList);
        }*/

        var avatarConfig = await FindConfig(newId);
        if (avatarConfig == null)
        {
            _logger.LogError("Avatar config file for {avatarId} not found", newId);
            return null;
        }

        _logger.LogInformation("Parsing config file for avatar: {avatarName}", avatarConfig.name);
        ParameterSenderService.Clear();
        var parameters = avatarConfig.parameters.Where(param => param.input != null).ToArray<IParameterDefinition>();

        foreach (var parameter in UnifiedTracking.AllParameters)
        {
            paramList.AddRange(parameter.ResetParam(parameters, avatarConfig));
        }

        //_lastAvatarId = newId;
        return (avatarConfig, paramList);
    }

    /// <summary>
    /// The avatar's name from its OSC config JSON, without resetting any parameter. For VRChat
    /// builds that don't serve /avatar/name over OSCQuery yet.
    /// </summary>
    public async Task<string?> FindAvatarName(string avatarId) =>
        string.IsNullOrEmpty(avatarId) ? null : (await FindConfig(avatarId))?.name;

    private async Task<AvatarConfigFile?> FindConfig(string avatarId)
    {
        foreach (var userFolder in Directory.GetDirectories(VRChat.VRCOSCDirectory)
                     .Where(folder => Directory.Exists(Path.Combine(folder, "Avatars"))))
        {
            foreach (var avatarFile in Directory.GetFiles(Path.Combine(userFolder, "Avatars"), "*.json"))
            {
                try
                {
                    var configText = await File.ReadAllTextAsync(avatarFile);
                    var tempConfig = JsonSerializer.Deserialize<AvatarConfigFile>(configText);
                    if (tempConfig == null || tempConfig.id != avatarId)
                    {
                        continue;
                    }
                    return tempConfig;
                }
                catch (JsonException ex)
                {
                    // Malformed JSON file detected, rename it to .bak to prevent future parsing attempts
                    var backupFileName = Path.ChangeExtension(avatarFile, ".bak");
                    _logger.LogWarning("Malformed JSON file detected: {fileName}. Renaming to {backupFileName}. Error: {error}", 
                        avatarFile, backupFileName, ex.Message);
                    
                    try
                    {
                        File.Move(avatarFile, backupFileName, overwrite: true);
                    }
                    catch (Exception moveEx)
                    {
                        _logger.LogError("Failed to rename malformed JSON file {fileName}: {error}", 
                            avatarFile, moveEx.Message);
                    }
                }
            }
        }

        return null;
    }
}