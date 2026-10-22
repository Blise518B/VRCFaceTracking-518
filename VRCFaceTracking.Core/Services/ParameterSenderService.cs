using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.OSC;
using VRCFaceTracking.Core.Params.Data;

namespace VRCFaceTracking.Core.Services;

public class ParameterSenderService : BackgroundService
{
    // Concurrent: most enqueues come from parameter Process() on the send loop itself,
    // but avatar-config loads enqueue from the OSC receive path (sendOnLoad), so this
    // queue really is touched from more than one thread.
    private static readonly ConcurrentQueue<OscMessage> SendQueue = new();
 
    private readonly OscSendService _sendService;
    private readonly UnifiedTrackingMutator _mutator; // We don't use this but we do want DI to run its constructor

    public static bool AllParametersRelevantStatic
    {
        get; set;
    }

    /// <summary>
    /// Re-send parameter values that stopped changing (see BaseParam.ParamValue), so a
    /// datagram VRChat drops can't leave a parameter stuck. Values are only sent on change,
    /// and VRChat does lose messages: a capture on 2026-10-15 showed about a quarter of the
    /// binary "Negative" flags never confirmed, one of them wrong for 8 s until the value
    /// happened to change again. An unchanged value costs a datagram and nothing else:
    /// VRChat ignores a value it already has, so nothing extra goes over the network.
    /// Switched at runtime with /vrcft/settings/resendUnchanged (bool).
    /// </summary>
    public static bool ResendUnchanged { get; set; } = true;
    public bool AllParametersRelevant
    {
        get => AllParametersRelevantStatic;
        set
        {
            if (AllParametersRelevantStatic == value) return;
            AllParametersRelevantStatic = value;
            SendQueue.Clear();
            foreach (var parameter in UnifiedTracking.AllParameters)
            {
                parameter.ResetParam(Array.Empty<IParameterDefinition>());
            }
        }
    }

    // Reused batch buffer so draining the queue doesn't allocate an array per 10ms tick.
    private OscMessage[] _batch = new OscMessage[256];
    
    private readonly ILogger<ParameterSenderService>? _logger;
    private DateTime _lastSendErrorLog = DateTime.MinValue;

    public ParameterSenderService(OscSendService sendService, UnifiedTrackingMutator mutator, ILogger<ParameterSenderService>? logger = null)
    {
        _sendService = sendService;
        _mutator = mutator;
        _logger = logger;
    }

    public static void Enqueue(OscMessage message) => SendQueue.Enqueue(message);
    public static void Clear() => SendQueue.Clear();
    
    protected async override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(10, cancellationToken);

                await UnifiedTracking.UpdateData(cancellationToken);

                // Drain the queue into the reused batch buffer (no per-tick allocations)
                var count = 0;
                while (SendQueue.TryDequeue(out var message))
                {
                    if (count == _batch.Length)
                    {
                        Array.Resize(ref _batch, _batch.Length * 2);
                    }
                    _batch[count++] = message;
                }

                var sendStart = Stopwatch.GetTimestamp();
                if (count > 0)
                {
                    await _sendService.Send(_batch, count, cancellationToken);
                    // Drop the references so a swapped avatar's parameters can be collected
                    Array.Clear(_batch, 0, count);
                }

                PipelineWatch.RecordLoop(
                    (Stopwatch.GetTimestamp() - sendStart) * 1000.0 / Stopwatch.Frequency);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                break;
            }
            catch (Exception e)
            {
                // Do NOT inspect the queued OscMessages here. The stock code enriched a Sentry
                // report with msg.Value for every queued message - but Value marshals from the
                // message's native allocation, which is no longer valid after a failed send.
                // That turned every transient send error into an AccessViolation that killed
                // the whole process (verified from .NET Runtime event-log stacks). The queue
                // is dropped instead: stale tracking frames are worthless anyway.
                var count = SendQueue.Count;
                SendQueue.Clear();

                if (DateTime.UtcNow - _lastSendErrorLog > TimeSpan.FromSeconds(5))
                {
                    _lastSendErrorLog = DateTime.UtcNow;
                    _logger?.LogWarning("OSC send failed ({type}: {message}). Dropped {count} queued messages; will keep trying.",
                        e.GetType().Name, e.Message, count);
                }
            }
        }
    }
}