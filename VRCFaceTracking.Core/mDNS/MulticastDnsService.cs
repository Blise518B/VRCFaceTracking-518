using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.OSC.Query.mDNS;

namespace VRCFaceTracking.Core.mDNS;

public partial class MulticastDnsService : ObservableObject
{
    private static readonly IPAddress MulticastIp = IPAddress.Parse("224.0.0.251");
    private const int MulticastPost = 5353;
    private static readonly IPEndPoint MdnsEndpointIp4 = new(MulticastIp, MulticastPost);
    private readonly ILogger<MulticastDnsService> _logger;

    // This machine's IPv4 addresses. Swapped for a new array when the network changes,
    // never mutated, so the receive loops can read it without taking a lock.
    private volatile IPAddress[] _localIpAddresses;

    private readonly UdpClient _receiver;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _refreshLock = new();

    // Senders and Receivers are touched by the receive loops, query responses and the
    // network-change callback, each on its own thread - only ever under SocketsLock.
    private static readonly object SocketsLock = new();
    private static readonly Dictionary<IPAddress, UdpClient> Senders = new();
    private static readonly Dictionary<UdpClient, CancellationToken> Receivers = new();
    private static readonly Dictionary<string, AdvertisedService> Services = new();

    // Last time a socket error was logged per interface, so a persistent one cannot flood
    // the log the way the dead-adapter sends did (36,812 lines in one session).
    private readonly ConcurrentDictionary<IPAddress, long> _lastErrorLog = new();
    private const long ErrorLogIntervalMs = 60_000;

    public Action OnVrcClientDiscovered = () => { };

    [ObservableProperty] private IPEndPoint _vrchatClientEndpoint;

    private static List<NetworkInterface> GetIpv4NetInterfaces() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(net =>
            net.OperationalStatus == OperationalStatus.Up &&
            net.NetworkInterfaceType != NetworkInterfaceType.Loopback && (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || ( 
            net.SupportsMulticast &&
            net.GetIPProperties().MulticastAddresses.Count != 0)))
        .ToList();

    // Get the first ipv4 address from a specific network interface
    private static IPAddress? GetIpv4Address(NetworkInterface net) => net.GetIPProperties()
        .UnicastAddresses
        .Select(addr => addr.Address)
        .FirstOrDefault(addr => addr.AddressFamily == AddressFamily.InterNetwork);

    private static IPAddress[] CurrentIpv4Addresses() => GetIpv4NetInterfaces()
        .Select(GetIpv4Address)
        .Where(addr => addr != null)
        .Select(addr => addr!)
        .Distinct()
        .ToArray();

    public MulticastDnsService(ILogger<MulticastDnsService> logger)
    {
        _logger = logger;

        _localIpAddresses = CurrentIpv4Addresses();

        // Create listeners for all interfaces
        _receiver = new UdpClient(AddressFamily.InterNetwork);
        _receiver.Client.ExclusiveAddressUse = false;
        _receiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _receiver.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPost));
        lock (SocketsLock)
        {
            Receivers.Add(_receiver, _cts.Token);
        }

        // For every ipv4 address discovered in the network interfaces, create a sender udp client set up grouping
        foreach (var ipAddress in _localIpAddresses)
        {
            AddSender(ipAddress);
        }

        Listen(_receiver, _cts.Token);

        // Adapters come and go mid-session: VPNs, a streaming app's virtual adapter, a
        // cable replugged. The senders used to be fixed at startup, so an address that
        // went away was sent to forever - about eight failures a second, all logged.
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    /// <summary>
    /// Joins the multicast group on one local address and opens a sender bound to it.
    /// A failure skips that address only; it used to abort the whole service.
    /// </summary>
    private void AddSender(IPAddress ipAddress)
    {
        try
        {
            // Add the local ip address to our multicast group
            _receiver.JoinMulticastGroup(MulticastIp, ipAddress);
        }
        catch (SocketException e)
        {
            // Typically "already a member" after the address came back; harmless
            _logger.LogDebug("mDNS: could not join the multicast group on {iface}: {message}", ipAddress, e.Message);
        }

        UdpClient? sender = null;
        try
        {
            sender = new UdpClient(ipAddress.AddressFamily);
            sender.Client.ExclusiveAddressUse = false;
            sender.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sender.Client.Bind(new IPEndPoint(ipAddress, MulticastPost));    // Bind to the local ip address
            sender.JoinMulticastGroup(MulticastIp, ipAddress);                           // Join the multicast group
            sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            sender?.Dispose();
            _logger.LogDebug("mDNS: could not open a sender on {iface}: {message}", ipAddress, e.Message);
            return;
        }

        lock (SocketsLock)
        {
            if (Senders.ContainsKey(ipAddress))
            {
                // Another network-change pass got here first
                sender.Dispose();
                return;
            }

            Senders.Add(ipAddress, sender);
            Receivers.Add(sender, _cts.Token);
        }

        _logger.LogDebug("mDNS: answering on {iface}", ipAddress);
        Listen(sender, _cts.Token);
    }

    /// <summary>
    /// Stops answering on an address that no longer works. It is added back by the next
    /// network-change pass that finds the address on an interface again.
    /// </summary>
    private void RetireSender(IPAddress ipAddress, string why)
    {
        UdpClient? sender;
        lock (SocketsLock)
        {
            if (!Senders.Remove(ipAddress, out sender))
            {
                return;
            }

            Receivers.Remove(sender);
        }

        try
        {
            // UdpClient has no per-interface IPv4 overload for leaving; the socket option does it
            _receiver.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership,
                new MulticastOption(MulticastIp, ipAddress));
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // Usually the interface is already gone and took the membership with it
            _logger.LogDebug("mDNS: could not leave the multicast group on {iface}: {message}", ipAddress, e.Message);
        }

        sender.Dispose();
        _logger.LogInformation("mDNS: stopped answering on {iface} ({why}). It is picked up again if the address comes back.",
            ipAddress, why);
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        // Raised on a threadpool thread; an exception escaping here would kill the app
        try
        {
            lock (_refreshLock)
            {
                var current = CurrentIpv4Addresses();
                _localIpAddresses = current;

                IPAddress[] known;
                lock (SocketsLock)
                {
                    known = Senders.Keys.ToArray();
                }

                foreach (var gone in known.Where(k => !current.Contains(k)))
                {
                    RetireSender(gone, "address removed from this machine");
                }

                foreach (var added in current.Where(c => !known.Contains(c)))
                {
                    AddSender(added);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("mDNS: could not follow a network change: {message}", ex.Message);
        }
    }

    private static KeyValuePair<IPAddress, UdpClient>[] SnapshotSenders()
    {
        lock (SocketsLock)
        {
            return Senders.ToArray();
        }
    }

    private static bool IsRetired(UdpClient client)
    {
        lock (SocketsLock)
        {
            return !Receivers.ContainsKey(client);
        }
    }

    private bool ShouldLogErrorFor(IPAddress address)
    {
        var now = Environment.TickCount64;
        if (_lastErrorLog.TryGetValue(address, out var last) && now - last < ErrorLogIntervalMs)
        {
            return false;
        }

        _lastErrorLog[address] = now;
        return true;
    }

    /// <summary>
    /// Sends one packet from one interface. Never throws: every caller is an async void,
    /// where an escaping exception takes the whole app down.
    /// </summary>
    private async Task SendFromAsync(IPAddress address, UdpClient sender, byte[] bytes)
    {
        try
        {
            await sender.SendAsync(bytes, bytes.Length, MdnsEndpointIp4);
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressNotAvailable)
        {
            // The address this sender is bound to no longer exists (WSAEADDRNOTAVAIL). It
            // fails the same way on every query until the address returns, so stop using
            // it now instead of logging the failure eight times a second.
            RetireSender(address, "its address is no longer available");
        }
        catch (SocketException e)
        {
            if (ShouldLogErrorFor(address))
            {
                _logger.LogDebug("mDNS send on {iface} failed ({code}): {message}",
                    address, e.SocketErrorCode, e.Message);
            }
        }
        catch (ObjectDisposedException)
        {
            // Retired by a network change while this send was in flight
        }
    }

    private async void ResolveDnsQueries(DnsPacket packet, IPEndPoint remoteEndpoint)
    {
        // Me when having to wait for vrchat to resolve itself
        // https://www.youtube.com/watch?v=wLg04uu2j2o
        if (packet.OPCODE != 0 || VrchatClientEndpoint == null)
        {
            return;
        }

        foreach (var question in packet.questions)
        {
            // Ensure the question has three labels. First for service name, second for protocol, third for domain
            // Ensure the question is for local domain
            // Ensure the question is for the _osc._udp service
            if (question.Labels.Count != 3
                || question.Labels[2] != "local"
                || !Services.TryGetValue($"{question.Labels[0]}.{question.Labels[1]}", out var service))
            {
                continue;
            }

            //foreach (var service in storedServices)
            {
                var qualifiedServiceName = new List<string>
                {
                    service.ServiceName,
                    question.Labels[0],
                    question.Labels[1],
                    question.Labels[2]
                };

                var serviceName = new List<string>
                {
                    service.ServiceName,
                    question.Labels[0].Trim('_'),
                    question.Labels[1].Trim('_')
                };

                var txt = new TXTRecord { Text = new List<string> { "txtvers=1" } };
                var srv = new SRVRecord {
                    Port = (ushort)service.Port,
                    Target = serviceName
                };
                var aRecord = new ARecord { Address = service.Address };
                var ptrRecord = new PTRRecord
                {
                    DomainLabels = qualifiedServiceName
                };

                var additionalRecords = new List<DnsResource>
                {
                    new (txt, qualifiedServiceName),
                    new (srv, qualifiedServiceName),
                    new (aRecord, serviceName)
                };

                var answers = new List<DnsResource> { new (ptrRecord, question.Labels) };

                var response = new DnsPacket
                {
                    CONFLICT = true,
                    ID = 0,
                    OPCODE = 0,
                    QUERYRESPONSE = true,
                    RESPONSECODE = 0,
                    TENTATIVE = false,
                    TRUNCATION = false,
                    questions = Array.Empty<DnsQuestion>(),
                    answers = answers.ToArray(),
                    authorities = Array.Empty<DnsResource>(),
                    additionals = additionalRecords.ToArray()
                };

                var bytes = response.Serialize();

                if (remoteEndpoint.Port == MulticastPost)
                {
                    // A sender is bound to one local interface. When that interface goes
                    // away - adapter change, VPN up/down, a streaming app's virtual
                    // adapter - the send fails with WSAEADDRNOTAVAIL (10049). That used to
                    // escape into the dispatcher and kill the app; SendFromAsync retires
                    // the sender instead. Iterate a snapshot: the network-change callback
                    // can change the set while we are in here.
                    foreach (var sender in SnapshotSenders())
                    {
                        await SendFromAsync(sender.Key, sender.Value, bytes);
                    }

                    continue;
                }

                //var unicastClientIp4 = new UdpClient(AddressFamily.InterNetwork);
                //await unicastClientIp4.SendAsync(bytes, bytes.Length, remoteEndpoint);
            }
        }
    }

    public async void SendQuery(string labels)
    {
        var dnsPacket = new DnsPacket();
        var dnsQuestion = new DnsQuestion(labels.Split('.').ToList(), 255, 1);
        dnsPacket.questions = new[] { dnsQuestion };
        dnsPacket.QUERYRESPONSE = false;
        dnsPacket.OPCODE = 0;
        dnsPacket.TRUNCATION = false;

        var bytes = dnsPacket.Serialize();
        foreach (var sender in SnapshotSenders())
        {
            await SendFromAsync(sender.Key, sender.Value, bytes);
        }

        //var unicastClientIp4 = new UdpClient(AddressFamily.InterNetwork);
        //await unicastClientIp4.SendAsync(bytes, bytes.Length, remoteEndpoint);
    }

    public void ResolveVrChatClient(DnsPacket packet, IPEndPoint remoteEndpoint)
    {
        if (!packet.QUERYRESPONSE || packet.answers.Length <= 0 || packet.answers[0].Type != 12)
        {
            return;
        }

        var ptrRecord = packet.answers[0].Data as PTRRecord;
        if (ptrRecord.DomainLabels.Count != 4
            || (!ptrRecord.DomainLabels[0].StartsWith("VRChat-Client") && !ptrRecord.DomainLabels[0].StartsWith("ChilloutVR-GameClient")))
        {
            return;
        }

        if (packet.answers[0].Labels.Count != 3 || packet.answers[0].Labels[0] != "_oscjson" ||
            packet.answers[0].Labels[1] != "_tcp" || packet.answers[0].Labels[2] != "local")
        {
            return;
        }

        // Now we find the first A record in the additional records
        var aRecord = packet.additionals.FirstOrDefault(r => r.Type == 1);
        var srvRecord = packet.additionals.FirstOrDefault(r => r.Type == 33);
        if (aRecord == null || srvRecord == null)
        {
            return;
        }

        var vrChatClientIp = aRecord.Data as ARecord;
        var vrChatClientPort = srvRecord.Data as SRVRecord;

        if (vrChatClientIp?.Address == null || vrChatClientPort?.Port == null)
        {
            return;
        }

        var hostAddress = vrChatClientIp.Address;

        // If the host address is a loopback address (127.0.0.1) but isn't our local machine,
        // manually set the correspondence address to where we heard the mdns response come from
        // This is due to VRC ALWAYS using 127.0.0.1 as the A record address
        if (IPAddress.IsLoopback(hostAddress) && !_localIpAddresses.Contains(remoteEndpoint.Address))
        {
            hostAddress = remoteEndpoint.Address;
        }

        VrchatClientEndpoint = new IPEndPoint(hostAddress, vrChatClientPort.Port);
        OnVrcClientDiscovered();
    }

    private async void Listen(UdpClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                // This socket's sender was retired; its interface is gone
                return;
            }
            catch (SocketException e)
            {
                if (IsRetired(client))
                {
                    return;
                }

                // Windows reports an earlier send's ICMP "port unreachable" on the next
                // receive (WSAECONNRESET) while the socket itself is fine: keep listening.
                // This method is async void, so letting anything escape kills the app.
                if (e.SocketErrorCode != SocketError.ConnectionReset)
                {
                    _logger.LogDebug("mDNS receive failed ({code}): {message}", e.SocketErrorCode, e.Message);
                    try
                    {
                        await Task.Delay(1000, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }

                continue;
            }

            if (!_localIpAddresses.Any(i => i.Equals(result.RemoteEndPoint.Address)))
            {
                // Wouldn't want to steal control of other people's faces on LAN now, would we!
                // (On second thought, that'd be pretty funny)
                continue;
            }

            try
            {
                var reader = new BigReader(result.Buffer);
                var packet = new DnsPacket(reader);

                // I'm aware this is cringe, but we do this first as it's a lot more likely vrchat beats us to the punch responding to the query
                ResolveVrChatClient(packet, result.RemoteEndPoint);
                ResolveDnsQueries(packet, result.RemoteEndPoint);
            }
            catch (Exception e)
            {
                SentrySdk.CaptureException(e, scope => scope.SetExtra("bytes", result.Buffer));
            }
        }
    }

    public void Advertise(string serviceName, AdvertisedService advertisement)
    {
        _logger.LogDebug($"Advertising service {serviceName} with advertisement {advertisement}");

        Services[serviceName] = advertisement;
    }
}
