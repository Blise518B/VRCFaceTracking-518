using System.ComponentModel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.OSC;
using VRCFaceTracking.Core.Params.Data;

namespace VRCFaceTracking.Core.Services;

/// <summary>
/// Feeds VRChat's native face tracking. While the loaded avatar has
/// <see cref="IAvatarInfo.FullFaceTracking"/>, every tracking tick sends the whole face as
/// one /tracking/face/v1 blob (<see cref="FaceTrackingBlob"/>). Such an avatar gets no v2
/// parameters and no /tracking/eye messages (see the ResetParam overloads), so the blob is
/// then all VRCFT sends. Idle for every other avatar.
/// </summary>
public class NativeFaceTrackingSender(
    ILogger<NativeFaceTrackingSender> logger,
    OscSendService sendService,
    OscQueryService oscQueryService) : IHostedService
{
    private readonly FaceTrackingBlob _blob = new();

    // Avatar info changes on the UI dispatcher in the full app, but inline on whichever
    // thread parsed the avatar in the lite build, where two loads can overlap
    private readonly object _subscriptionLock = new();
    private bool _subscribed;

    private DateTime _lastSendErrorLog = DateTime.MinValue;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        oscQueryService.PropertyChanged += OnOscQueryChanged;
        Apply(oscQueryService.AvatarInfo);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        oscQueryService.PropertyChanged -= OnOscQueryChanged;
        Apply(null);
        return Task.CompletedTask;
    }

    private void OnOscQueryChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(OscQueryService.AvatarInfo))
        {
            Apply(oscQueryService.AvatarInfo);
        }
    }

    private void Apply(IAvatarInfo? avatar)
    {
        var wanted = avatar?.FullFaceTracking == true;
        lock (_subscriptionLock)
        {
            if (wanted == _subscribed)
            {
                return;
            }

            _subscribed = wanted;
            if (wanted)
            {
                UnifiedTracking.OnUnifiedDataUpdated += Send;
            }
            else
            {
                UnifiedTracking.OnUnifiedDataUpdated -= Send;
            }
        }

        if (wanted)
        {
            logger.LogInformation("Native face tracking: sending all {keys} tracking keys to {address} every tick, v2 parameters off",
                FaceTrackingBlob.PayloadLength, FaceTrackingBlob.Address);
        }
        else if (avatar != null)
        {
            logger.LogInformation("Avatar has no native face tracking: stopped sending {address}", FaceTrackingBlob.Address);
        }
    }

    // On the send loop, 100 times a second (invariant 1): no allocations, nothing that blocks.
    // Sent every tick, changed or not: the blob carries every key, so the next one repairs a
    // lost one 10 ms later and BaseParam's re-send isn't needed. It may not throw either: that
    // would skip the update event's later subscribers and end in the send loop's catch, which
    // drops the tick's queued messages.
    private void Send(UnifiedTrackingData data)
    {
        try
        {
            _blob.Write(data);
            sendService.SendRaw(_blob.Datagram, FaceTrackingBlob.DatagramLength, FaceTrackingBlob.Address);
        }
        catch (Exception e)
        {
            if (DateTime.UtcNow - _lastSendErrorLog > TimeSpan.FromSeconds(5))
            {
                _lastSendErrorLog = DateTime.UtcNow;
                logger.LogWarning("Sending {address} failed ({type}: {message}); will keep trying",
                    FaceTrackingBlob.Address, e.GetType().Name, e.Message);
            }
        }
    }
}
