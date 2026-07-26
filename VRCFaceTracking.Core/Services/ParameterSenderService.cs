using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.OSC;
using VRCFaceTracking.Core.Params.Data;

namespace VRCFaceTracking.Core.Services;

public class ParameterSenderService : BackgroundService
{
    // We probably don't need a queue since we use osc message bundles, but for now, we're keeping it as
    // we might want to allow a way for the user to specify bundle or single message sends in the future
    private static readonly Queue<OscMessage> SendQueue = new();
 
    private readonly OscSendService _sendService;
    private readonly UnifiedTrackingMutator _mutator; // We don't use this but we do want DI to run its constructor

    public static bool AllParametersRelevantStatic
    {
        get; set;
    }
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

                // Send all messages in OSCParams.SendQueue
                if (SendQueue.Count <= 0)
                {
                    continue;
                }

                await _sendService.Send(SendQueue.ToArray(), cancellationToken);

                SendQueue.Clear();
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