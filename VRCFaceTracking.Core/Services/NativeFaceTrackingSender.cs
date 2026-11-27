using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.OSC;
using VRCFaceTracking.Core.Params.Data;

namespace VRCFaceTracking.Core.Services;

/// <summary>
/// Feeds VRChat's native face tracking. While the loaded avatar has
/// <see cref="IAvatarInfo.FullFaceTracking"/> and tracking data is fresh, every tracking tick
/// sends the whole face as one /tracking/face/v1 blob (<see cref="FaceTrackingBlob"/>). When
/// the modules stop delivering new data it sends one neutral blob and then nothing until they
/// do (<see cref="NativeFrameGate"/>), so VRChat takes the face back. Such an avatar gets no
/// v2 parameters and no /tracking/eye messages (see the ResetParam overloads), so the blob is
/// then all VRCFT sends. Idle for every other avatar.
/// </summary>
public class NativeFaceTrackingSender(
    ILogger<NativeFaceTrackingSender> logger,
    OscSendService sendService,
    OscQueryService oscQueryService) : IHostedService
{
    private readonly FaceTrackingBlob _blob = new();
    private readonly NativeFrameGate _gate = new();
    private long _pausedAt;

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
                // A new avatar carries nothing of ours yet, so a stall needs no neutral frame
                _gate.Reset();
                UnifiedTracking.OnUnifiedDataUpdated += Send;
            }
            else
            {
                UnifiedTracking.OnUnifiedDataUpdated -= Send;
            }
        }

        if (wanted)
        {
            logger.LogInformation("Native face tracking: sending all {keys} tracking keys to {address} every tick while tracking data is fresh, v2 parameters off",
                FaceTrackingBlob.PayloadLength, FaceTrackingBlob.Address);
        }
        else if (avatar != null)
        {
            logger.LogInformation("Avatar has no native face tracking: stopped sending {address}", FaceTrackingBlob.Address);
        }
    }

    // On the send loop, 100 times a second (invariant 1): no allocations, nothing that blocks.
    // Sent every tick while tracking is fresh, changed or not: the blob carries every key, so
    // the next one repairs a lost one 10 ms later and BaseParam's re-send isn't needed. When
    // tracking goes stale, one neutral blob and then silence; the log lines on those two
    // transitions are the only allocations, once per stall. It may not throw either: that
    // would skip the update event's later subscribers and end in the send loop's catch, which
    // drops the tick's queued messages.
    private void Send(UnifiedTrackingData data)
    {
        try
        {
            var now = Stopwatch.GetTimestamp();
            var lastFresh = TrackingFreshness.LastFresh;
            switch (_gate.Next(now, lastFresh))
            {
                case NativeFrameGate.Frame.Live:
                    _blob.Write(data);
                    sendService.SendRaw(_blob.Datagram, FaceTrackingBlob.DatagramLength, FaceTrackingBlob.Address);
                    break;

                case NativeFrameGate.Frame.Resumed:
                    _blob.Write(data);
                    sendService.SendRaw(_blob.Datagram, FaceTrackingBlob.DatagramLength, FaceTrackingBlob.Address);
                    logger.LogInformation("Native face tracking resumed after {seconds:F1} s: tracking data is fresh again, sending {address} every tick",
                        (now - _pausedAt) / (double)Stopwatch.Frequency, FaceTrackingBlob.Address);
                    break;

                case NativeFrameGate.Frame.Neutral:
                    _pausedAt = now;
                    _blob.WriteNeutral();
                    sendService.SendRaw(_blob.Datagram, FaceTrackingBlob.DatagramLength, FaceTrackingBlob.Address);
                    if (lastFresh == 0)
                    {
                        logger.LogInformation("Native face tracking paused: no tracking module is running. Sent one neutral frame and stopped sending {address}, so VRChat takes the face back",
                            FaceTrackingBlob.Address);
                    }
                    else
                    {
                        logger.LogInformation("Native face tracking paused: no new tracking data for {ms} ms. Sent one neutral frame and stopped sending {address}, so VRChat takes the face back",
                            (now - lastFresh) * 1000 / Stopwatch.Frequency, FaceTrackingBlob.Address);
                    }
                    break;
            }
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
