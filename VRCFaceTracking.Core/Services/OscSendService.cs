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

    public async Task Send(OscMessage message, CancellationToken ct)
    {
        if (_sendSocket is not { Connected: true })
        {
            return; // Nothing to send to yet - not an error worth throwing per tick
        }

        var nextByteIndex = await message.Encode(_sendBuffer, ct);
        if (nextByteIndex <= 0 || nextByteIndex > _sendBuffer.Length)
        {
            _logger.LogError("OSC message too large to send! Skipping this batch of messages.");
            return;
        }

        await _sendSocket.SendAsync(_sendBuffer[..nextByteIndex]);
        OnMessagesDispatched(1);
    }

    public async Task Send(OscMessage[] messages, CancellationToken ct)
    {
        // Awaiting through a null-conditional used to throw NullReferenceException on every
        // tick whenever the socket wasn't up yet - which the sender's catch then escalated.
        if (_sendSocket is not { Connected: true })
        {
            return;
        }

        var cbt = messages.Select(m => m._meta).ToArray();
        var index = 0;
        while (index < cbt.Length)
        {
            var length = await Task.Run(() => fti_osc.create_osc_bundle(_sendBuffer, cbt, messages.Length, ref index), ct);

            // The native bundler assumes a 4096-byte buffer (SizeConst) and reports how much
            // it wrote. Trust but verify: a length outside the buffer means something went
            // wrong natively, and slicing with it would throw (or worse, send garbage).
            if (length <= 0 || length > _sendBuffer.Length)
            {
                _logger.LogWarning("Native OSC bundler returned invalid length {length}; dropping the rest of this batch.", length);
                break;
            }

            await _sendSocket.SendAsync(_sendBuffer[..length]);
        }
        OnMessagesDispatched(index);
    }
}
