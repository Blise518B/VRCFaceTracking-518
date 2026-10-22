using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.OSC;

namespace VRCFaceTracking.Core.Services;

/**
 * OscSendService is responsible for encoding osc messages and sending them over OSC
 */
public class OscSendService
{
    private readonly ILogger<OscSendService> _logger;
    private readonly IOscTarget _oscTarget;

    private Socket _sendSocket;
    private readonly byte[] _sendBuffer = new byte[4096];
    private DateTime _lastNotConnectedWarning = DateTime.MinValue;

    private CancellationTokenSource _cts;
    public Action<int> OnMessagesDispatched = _ => { };

    public OscSendService(
        ILogger<OscSendService> logger,
        IOscTarget oscTarget
    )
    {
        _logger = logger;
        _cts = new CancellationTokenSource();

        _oscTarget = oscTarget;

        _oscTarget.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not nameof(IOscTarget.OutPort))
            {
                return;
            }

            if (_oscTarget.OutPort == default)
            {
                return;
            }

            if (string.IsNullOrEmpty(_oscTarget.DestinationAddress))
            {
                _oscTarget.DestinationAddress = "127.0.0.1";
            }

            UpdateTarget(new IPEndPoint(IPAddress.Parse(_oscTarget.DestinationAddress), _oscTarget.OutPort));
        };
    }

    /// <summary>
    /// Establishes the send socket from the current target values. Must be called
    /// once after settings load: the PropertyChanged hook above only fires when a
    /// loaded value DIFFERS from the field default, so a saved 9000 loading onto a
    /// 9000 default raises no event and - without this - no socket ever connects.
    /// </summary>
    public void EnsureConnected()
    {
        if (_sendSocket is { Connected: true })
        {
            return;
        }

        if (_oscTarget.OutPort <= 0)
        {
            return; // Nothing valid to connect to; the change handler covers later repair
        }

        if (string.IsNullOrEmpty(_oscTarget.DestinationAddress))
        {
            _oscTarget.DestinationAddress = "127.0.0.1";
        }

        UpdateTarget(new IPEndPoint(IPAddress.Parse(_oscTarget.DestinationAddress), _oscTarget.OutPort));
    }

    private void UpdateTarget(IPEndPoint endpoint)
    {
        _cts.Cancel();
        _sendSocket?.Close();
        _oscTarget.IsConnected = false;

        _sendSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            _sendSocket.Connect(endpoint);
            _oscTarget.IsConnected = true;
        }
        catch (SocketException ex)
        {
            _logger.LogWarning($"Failed to bind to sender endpoint: {endpoint}. {ex.Message}");
        }
        finally
        {
            _cts = new CancellationTokenSource();
        }
    }

    // Dropping sends while the socket is down is fine per tick, but staying silent
    // about it hides a dead outgoing path behind healthy-looking logs. Warn once a minute.
    private void WarnNotConnected()
    {
        if (DateTime.UtcNow - _lastNotConnectedWarning < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastNotConnectedWarning = DateTime.UtcNow;
        _logger.LogWarning(
            "OSC send socket is not connected (target {address}:{port}) - dropping outgoing messages. VRChat will not receive tracking data.",
            _oscTarget.DestinationAddress, _oscTarget.OutPort);
    }

    public Task Send(OscMessage message, CancellationToken ct)
    {
        if (_sendSocket is not { Connected: true })
        {
            WarnNotConnected();
            return Task.CompletedTask; // Nothing to send to yet - not an error worth throwing per tick
        }

        if (OutputTap.Enabled)
        {
            OutputTap.Record(new[] { message });
        }

        // Encode and send inline: both are microsecond operations (native serialize +
        // UDP datagram to loopback), and the async versions cost a threadpool hop and
        // a buffer-slice allocation per call on what is a 100Hz path.
        var nextByteIndex = fti_osc.create_osc_message(_sendBuffer, ref message._meta);
        if (nextByteIndex <= 0 || nextByteIndex > _sendBuffer.Length)
        {
            _logger.LogError("OSC message too large to send! Skipping this batch of messages.");
            return Task.CompletedTask;
        }

        _sendSocket.Send(_sendBuffer, 0, nextByteIndex, SocketFlags.None);
        OnMessagesDispatched(1);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends a datagram that is already OSC-encoded (<see cref="FaceTrackingBlob"/>), as its
    /// own packet next to the fti_osc bundles. Inline like the other sends. Throws what the
    /// socket throws; the caller runs on the tracking tick and has to catch it.
    /// </summary>
    public void SendRaw(byte[] datagram, int length, string address)
    {
        var socket = _sendSocket;
        if (socket is not { Connected: true })
        {
            WarnNotConnected();
            return;
        }

        if (OutputTap.Enabled)
        {
            OutputTap.RecordRaw(address, length);
        }

        socket.Send(datagram, 0, length, SocketFlags.None);
        OnMessagesDispatched(1);
    }

    public Task Send(OscMessage[] messages, CancellationToken ct) => Send(messages, messages.Length, ct);

    // Reused across ticks so the per-tick LINQ Select().ToArray() allocation is gone.
    private OscMessageMeta[] _metaBuffer = new OscMessageMeta[256];

    public Task Send(OscMessage[] messages, int count, CancellationToken ct)
    {
        // Guarding through a null-conditional used to throw NullReferenceException on every
        // tick whenever the socket wasn't up yet - which the sender's catch then escalated.
        if (_sendSocket is not { Connected: true })
        {
            WarnNotConnected();
            return Task.CompletedTask;
        }

        if (count <= 0)
        {
            return Task.CompletedTask;
        }

        if (OutputTap.Enabled)
        {
            OutputTap.Record(messages, count);
        }

        if (_metaBuffer.Length < count)
        {
            _metaBuffer = new OscMessageMeta[Math.Max(count, _metaBuffer.Length * 2)];
        }
        for (var i = 0; i < count; i++)
        {
            _metaBuffer[i] = messages[i]._meta;
        }

        var index = 0;
        while (index < count)
        {
            // Inline native call - see the single-message overload for why no Task.Run.
            var length = fti_osc.create_osc_bundle(_sendBuffer, _metaBuffer, count, ref index);

            // The native bundler assumes a 4096-byte buffer (SizeConst) and reports how much
            // it wrote. Trust but verify: a length outside the buffer means something went
            // wrong natively, and sending with it would throw (or worse, send garbage).
            if (length <= 0 || length > _sendBuffer.Length)
            {
                _logger.LogWarning("Native OSC bundler returned invalid length {length}; dropping the rest of this batch.", length);
                break;
            }

            _sendSocket.Send(_sendBuffer, 0, length, SocketFlags.None);
        }
        OnMessagesDispatched(index);
        return Task.CompletedTask;
    }
}
