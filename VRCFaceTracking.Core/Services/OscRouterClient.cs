using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.OSC;

namespace VRCFaceTracking.Core.Services;

/// <summary>
/// Tier 1 client for the 518 OSC Fallback Standard (see 518Hub/docs/STANDARD.md).
///
/// VRChat's mDNS listener dies after a few hours, after which it never discovers a
/// freshly started VRCFT - so no incoming avatar parameters, only a VRChat restart
/// helps. The router starts with SteamVR, is discovered while VRChat is healthy, and
/// re-broadcasts VRChat's stream. This client binds an ephemeral port, subscribes at
/// the fixed well-known router address, and feeds received datagrams into the exact
/// same parse path native reception uses.
///
/// Zero configuration by design: the only constant is 127.0.0.1:51518 from the
/// standard. If no router is running, this stays silent and costs one tiny keep-alive
/// packet every 20 seconds; native OSCQuery keeps working unaffected either way.
/// Duplicates (native + router both delivering) are harmless: avatar parameters are
/// absolute values, so applying one twice is a no-op.
/// </summary>
public partial class OscRouterClient : ObservableObject
{
    private const int RouterPort = 51518;
    private static readonly IPEndPoint RouterEndpoint = new(IPAddress.Loopback, RouterPort);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(20);
    private static readonly byte[] ControlPrefix = "/router518/"u8.ToArray();

    /// <summary>How we identify ourselves to the router's control panel.</summary>
    private const string ProgramName = "VRCFaceTracking 518";

    [ObservableProperty]
    [property: SavedSetting("UseOscRouter518", true)]
    private bool _enabled = true;

    /// <summary>
    /// Plain-language link state, shown in Settings and the lite monitor. The router
    /// normally runs headless (no window at all), so "is it running?" is otherwise
    /// only answerable via Task Manager.
    /// </summary>
    [ObservableProperty] private string _linkStatus = "checking...";

    private readonly ILogger<OscRouterClient> _logger;
    private readonly ILocalSettingsService _settingsService;
    private readonly OscRecvService _recvService;
    private readonly IDispatcherService _dispatcherService;
    private DateTime _lastReplyUtc = DateTime.MinValue;

    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private bool _initialized;

    public int MessagesReceived { get; private set; }

    public OscRouterClient(
        ILogger<OscRouterClient> logger,
        ILocalSettingsService settingsService,
        OscRecvService recvService,
        IDispatcherService dispatcherService)
    {
        _logger = logger;
        _settingsService = settingsService;
        _recvService = recvService;
        _dispatcherService = dispatcherService;

        PropertyChanged += (_, args) =>
        {
            // Only the toggle is persisted. LinkStatus changes on every keep-alive and its
            // lost-contact text counts seconds, so saving on any property rewrote the whole
            // settings file every 20s forever - and the lite build writes it synchronously.
            if (_initialized && args.PropertyName == nameof(Enabled))
            {
                _settingsService.Save(this);
            }
        };
    }

    partial void OnEnabledChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }

        if (value)
        {
            StartListening();
        }
        else
        {
            StopListening();
            SetStatus("off - using VRChat's own discovery only");
        }
    }

    /// <summary>Property changes must reach the UI thread in the full app.</summary>
    private void SetStatus(string text)
    {
        if (LinkStatus == text)
        {
            return;
        }

        try
        {
            _dispatcherService.Run(() => LinkStatus = text);
        }
        catch (Exception)
        {
            LinkStatus = text; // No dispatcher (headless): set it directly
        }
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        await _settingsService.Load(this);
        _initialized = true;

        if (Enabled)
        {
            StartListening();
        }
    }

    private void StartListening()
    {
        try
        {
            StopListening();

            // Port 0: the OS picks a free port, so collisions are impossible and no
            // configuration exists to get wrong - the core idea of the standard.
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, 1 << 20);
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var localPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;

            _cts = new CancellationTokenSource();
            _ = KeepAliveLoop(localPort, _cts.Token);
            _ = ReceiveLoop(_cts.Token);

            _logger.LogInformation(
                "OSC Router 518 client listening on port {port}, subscribing at 127.0.0.1:{router}.",
                localPort, RouterPort);
        }
        catch (Exception e)
        {
            _logger.LogWarning("Could not start OSC Router 518 client: {message}", e.Message);
        }
    }

    private void StopListening()
    {
        try
        {
            _cts?.Cancel();
            if (_socket is { IsBound: true })
            {
                // Polite unsubscribe; the router's TTL would clean us up anyway
                var port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
                _socket.SendTo(BuildControl("/router518/unsubscribe", port), RouterEndpoint);
            }
        }
        catch (Exception)
        {
            // Shutdown must never fail loudly
        }
        finally
        {
            _socket?.Close();
            _socket = null;
        }
    }

    private async Task KeepAliveLoop(int localPort, CancellationToken token)
    {
        var subscribe = BuildControl("/router518/subscribe", localPort, ProgramName);
        while (!token.IsCancellationRequested)
        {
            try
            {
                _socket?.SendTo(subscribe, RouterEndpoint);
            }
            catch (Exception)
            {
                // Router not running - that's fine, we're the fallback, not the main path
            }

            // The router acks every subscribe, so a recent reply is proof it's alive.
            // Three missed keep-alives (60s) before calling it gone, so a momentarily
            // busy router doesn't flap the display.
            var since = DateTime.UtcNow - _lastReplyUtc;
            SetStatus(since < TimeSpan.FromSeconds(60)
                ? $"connected on port {localPort} (router is running)"
                : _lastReplyUtc == DateTime.MinValue
                    ? "no hub found - start the 518 Hub to enable the fallback"
                    : $"lost contact {(int)since.TotalSeconds}s ago - is the router still running?");

            try
            {
                await Task.Delay(KeepAliveInterval, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReceiveLoop(CancellationToken token)
    {
        var buffer = new byte[4096];
        while (!token.IsCancellationRequested && _socket != null)
        {
            try
            {
                var received = await _socket.ReceiveAsync(buffer, SocketFlags.None, token);
                if (received <= 0)
                {
                    continue;
                }

                // Router control replies (/router518/ok etc.) are not tracking data, but
                // they ARE the liveness signal: only a running router sends them.
                if (received > ControlPrefix.Length && buffer.AsSpan(0, ControlPrefix.Length)
                        .SequenceEqual(ControlPrefix))
                {
                    _lastReplyUtc = DateTime.UtcNow;
                    SetStatus($"connected on port {((IPEndPoint)_socket!.LocalEndPoint!).Port} (router is running)");
                    continue;
                }

                // Same parse + dispatch as native reception, so downstream code
                // cannot tell the difference between router and direct delivery
                var offset = 0;
                var message = OscMessage.TryParseOsc(buffer, received, ref offset);
                if (message == null)
                {
                    continue;
                }

                MessagesReceived++;
                _recvService.OnMessageReceived(message);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception)
            {
                // A malformed datagram or transient socket error must not stop the loop
            }
        }
    }

    /// <summary>
    /// Builds /addr &lt;port:int32&gt; with an optional trailing name string, so the router's
    /// control panel can list us by name instead of inferring it from whichever process
    /// happens to own the port.
    /// </summary>
    private static byte[] BuildControl(string address, int port, string? name = null)
    {
        static byte[] Pad(byte[] raw)
        {
            var length = (raw.Length + 1 + 3) & ~3;
            var padded = new byte[length];
            raw.CopyTo(padded, 0);
            return padded;
        }

        var head = Pad(System.Text.Encoding.ASCII.GetBytes(address));
        var withName = !string.IsNullOrEmpty(name);
        var tags = Pad(withName ? ",is"u8.ToArray() : ",i"u8.ToArray());
        var value = BitConverter.GetBytes(port);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(value);
        }

        var nameBytes = withName
            ? Pad(System.Text.Encoding.ASCII.GetBytes(name!))
            : Array.Empty<byte>();

        var packet = new byte[head.Length + tags.Length + 4 + nameBytes.Length];
        head.CopyTo(packet, 0);
        tags.CopyTo(packet, head.Length);
        value.CopyTo(packet, head.Length + tags.Length);
        nameBytes.CopyTo(packet, head.Length + tags.Length + 4);
        return packet;
    }
}
