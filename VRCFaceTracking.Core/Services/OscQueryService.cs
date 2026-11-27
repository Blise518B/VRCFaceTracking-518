using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.mDNS;
using VRCFaceTracking.Core.Models.ParameterDefinition;
using VRCFaceTracking.Core.OSC;
using VRCFaceTracking.Core.OSC.Query.mDNS;
using VRCFaceTracking.Core.Params;

namespace VRCFaceTracking.Core.Services;

public partial class OscQueryService(
    ILogger<OscQueryService> logger,
    OscQueryConfigParser oscQueryConfigParser,
    AvatarConfigParser avatarConfigParser,
    ParameterSenderService parameterSenderService,
    IOscTarget oscTarget,
    MulticastDnsService multicastDnsService,
    OscRecvService recvService,
    OscSendService sendService,
    ILocalSettingsService settingsService,
    HttpHandler httpHandler,
    IDispatcherService dispatcherService)
    : ObservableObject
{
    [ObservableProperty] private IAvatarInfo _avatarInfo = new NullAvatarDef("Loading...", "Loading...");
    [ObservableProperty] private List<Parameter> _avatarParameters;

    /// <summary>Runs every avatar load, one at a time, and notices changes VRChat didn't announce.</summary>
    private AvatarSync? _avatarSync;

    public async Task InitializeAsync()
    {
        logger.LogDebug("OSC Service Initializing");

        // Before anything can deliver an /avatar/change
        _avatarSync ??= new AvatarSync(logger, LoadAvatarAsync, oscQueryConfigParser.ReadCurrentAvatarId);
        _avatarSync.Start();

        recvService.OnMessageReceived = HandleNewMessage;

        await settingsService.Load(oscTarget);

        (bool listenerSuccess, bool senderSuccess) result = (false, false);

        if (string.IsNullOrWhiteSpace(oscTarget.DestinationAddress))
        {
            return;  // Return both false as we cant bind to anything without an address
        }

        // Connect the send socket deterministically now that settings are loaded.
        // Face tracking output must never hinge on whether the loaded port value
        // happened to differ from the field default (see EnsureConnected).
        sendService.EnsureConnected();

        multicastDnsService.OnVrcClientDiscovered += FirstClientDiscovered;

        multicastDnsService.SendQuery("_oscjson._tcp.local");

        logger.LogDebug("OSC Service Initialized with result {0}", result);
        await Task.CompletedTask;
    }

    private void FirstClientDiscovered()
    {
        multicastDnsService.OnVrcClientDiscovered -= FirstClientDiscovered;

        logger.LogInformation($"OSCQuery detected at {multicastDnsService.VrchatClientEndpoint}. Setting port negotiation to autopilot.");

        httpHandler.OnHostInfoQueried += HandleNewAvatarWrapper;

        var recvEndpoint = recvService.UpdateTarget(new IPEndPoint(IPAddress.Parse(oscTarget.DestinationAddress), 0));
        if (recvEndpoint == null)
        {
            logger.LogError("Very strange. We were unable to bind to a random port.");
            recvEndpoint = new IPEndPoint(IPAddress.Parse(oscTarget.DestinationAddress), oscTarget.InPort);
        }

        var randomServiceSuffix = Utils.GetRandomChars(6);
        var httpPort = Utils.GetRandomFreePort();
        httpHandler.SetAppName("VRCFT-" + randomServiceSuffix);
        httpHandler.BindTo($"http://127.0.0.1:{httpPort}/", recvEndpoint.Port);

        // Advertise our OSC JSON and OSC endpoints (OSC JSON to display the silly lil popup in-game)
        multicastDnsService.Advertise("_oscjson._tcp", new AdvertisedService("VRCFT-"+randomServiceSuffix, httpPort, IPAddress.Loopback));
        multicastDnsService.Advertise("_osc._udp", new AdvertisedService("VRCFT-"+randomServiceSuffix, recvEndpoint.Port, IPAddress.Loopback));

        // VRChat is there already, so whatever it has on is ready to read
        _avatarSync?.Request(null, immediate: true);
    }

    /// <summary>
    /// One load, called by AvatarSync only (never two at once). <paramref name="expectedId"/> is
    /// the avatar the /avatar/change message named, if any.
    /// </summary>
    private async Task<AvatarLoadResult> LoadAvatarAsync(string? expectedId, CancellationToken ct)
    {
        IAvatarInfo avatarInfo;
        List<Parameter> parameters;
        if (multicastDnsService.VrchatClientEndpoint != null)
        {
            var (outcome, id, info, list) = await oscQueryConfigParser.LoadAvatar(expectedId, ct);
            if (outcome != AvatarLoadOutcome.Loaded)
            {
                return new AvatarLoadResult(outcome, id);
            }

            avatarInfo = info!;
            parameters = list!;
        }
        else
        {
            // Plain OSC (VRChat never found over mDNS): the config file VRChat writes for the id
            // the message named. Nothing to compare against, so never Stale.
            if (string.IsNullOrEmpty(expectedId))
            {
                return new AvatarLoadResult(AvatarLoadOutcome.Failed, null);
            }

            var parsed = await avatarConfigParser.ParseAvatar(expectedId);
            if (!parsed.HasValue)
            {
                return new AvatarLoadResult(AvatarLoadOutcome.Failed, null);
            }

            (avatarInfo, parameters) = parsed.Value;
        }

        // Parsing success. Deregister callback and update values
        httpHandler.OnHostInfoQueried -= HandleNewAvatarWrapper;
        // The id was only in the log for native face tracking avatars; a stuck avatar needs it
        logger.LogInformation("Avatar loaded: {name} ({id}), {kind}", avatarInfo.Name, avatarInfo.Id,
            avatarInfo.FullFaceTracking ? "native face tracking" : $"{parameters.Count} parameters");
        dispatcherService.Run(() =>
        {
            AvatarInfo = avatarInfo;
            AvatarParameters = parameters;
        });

        return new AvatarLoadResult(AvatarLoadOutcome.Loaded, avatarInfo.Id);
    }

    // VRChat asked for our host info: until the first load worked, a sign it's ready now
    private void HandleNewAvatarWrapper() => _avatarSync?.Request(null);


    private void HandleNewMessage(OscMessage msg)
    {
        switch (msg.Address)
        {
            case "/avatar/change":
                // Copied out now: the message is released once this handler returns
                _avatarSync?.Request(msg.Value as string);
                break;
            case "/vrcft/settings/forceRelevant":   // Endpoint for external tools to force vrcft to send all parameters
                if (msg.Value is bool relevancy)
                {
                    parameterSenderService.AllParametersRelevant = relevancy;
                }

                break;
            case "/vrcft/settings/resendUnchanged":   // A/B switch for the lost-message repair, see BaseParam
                if (msg.Value is bool resend)
                {
                    ParameterSenderService.ResendUnchanged = resend;
                    logger.LogInformation("Re-sending unchanged parameter values: {state}", resend ? "on" : "off");
                }

                break;
            /*
            case "/avatar/parameters/EyeTrackingActive":
                if (UnifiedLibManager.EyeStatus != ModuleState.Uninitialized)
                {
                    if (!msg.Value.BoolValue)
                        UnifiedLibManager.EyeStatus = ModuleState.Idle;
                    else UnifiedLibManager.EyeStatus = ModuleState.Active;
                }
                break;
            case "/avatar/parameters/LipTrackingActive":
            case "/avatar/parameters/ExpressionTrackingActive":
                {
                    if (!msg.Value.BoolValue)
                if (UnifiedLibManager.ExpressionStatus != ModuleState.Uninitialized)
                        UnifiedLibManager.ExpressionStatus = ModuleState.Idle;
                    else UnifiedLibManager.ExpressionStatus = ModuleState.Active;
                }
                break;
            */
        }
    }

    partial void OnAvatarParametersChanged(List<Parameter> value)
    {
        logger.LogDebug("Configuration loaded. Checking for native tracking parameters...");
        var hasLoadedNative = value.Any(p => p.GetParamNames().Any(t => t.paramName.StartsWith("/tracking/")));
        if (hasLoadedNative)
        {
            logger.LogWarning("Native tracking parameters detected.");
        }

        var deprecatedParams = value.Where(p => p.Deprecated).ToList();

        logger.LogInformation(value.Count + " parameters loaded.");
        if (deprecatedParams.Any())
        {
            logger.LogWarning(
                deprecatedParams.Count +
                " Legacy parameters detected. " +
                "Please consider updating the avatar to use the latest documented parameters.");
            logger.LogDebug($"Loaded deprecated parameters: {string.Join(", ", deprecatedParams.SelectMany(x => x.GetParamNames()).Select(y => y.paramName))}");
        }
    }
}
