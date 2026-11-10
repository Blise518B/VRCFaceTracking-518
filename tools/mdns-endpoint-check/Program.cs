// Offline check: a second VRChat mDNS record only replaces the OSCQuery endpoint in use once
// that endpoint stops answering (FORK-NOTES.md, "Two VRChat records").
//
//   dotnet run --project tools/mdns-endpoint-check
//
// Runs the real MulticastDnsService. The VRChat records are handed straight to
// ResolveVrChatClient (serialized and parsed again, like a received packet) and never go out
// on the network, so no other program on this machine sees them. Fake OSCQuery servers on
// loopback stand in for VRChat: one that answers, one that accepts and never replies (hung),
// and closed ports (crashed). Exit code 0 when every check passes.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.mDNS;
using VRCFaceTracking.Core.OSC.Query.mDNS;

foreach (var game in new[] { "VRChat", "ChilloutVR" })
{
    if (Process.GetProcessesByName(game).Length > 0)
    {
        Console.WriteLine($"{game} is running. Its own mDNS records would reach the service under test; close it and run again.");
        return 2;
    }
}

var checks = new Checks();
var log = new ListLogger<MulticastDnsService>();
var mdns = new MulticastDnsService(log);
var discovered = 0;
mdns.OnVrcClientDiscovered += () => Interlocked.Increment(ref discovered);
int? EndpointPort() => mdns.VrchatClientEndpoint?.Port;

Console.WriteLine("1. The first record is taken at once, as before");
using var a = FakeOscQuery.Start();
var took = Offer(mdns, "VRChat-Client-AAAAAA", a.Port);
checks.That(EndpointPort() == a.Port, "the endpoint is set before ResolveVrChatClient returns");
checks.That(discovered == 1, "OnVrcClientDiscovered fired once");
Thread.Sleep(300);
checks.That(a.Requests == 0, "no HTTP request for the first record");

Console.WriteLine("2. The same record again");
Offer(mdns, "VRChat-Client-AAAAAA", a.Port);
checks.That(EndpointPort() == a.Port && discovered == 2, "same endpoint, OnVrcClientDiscovered fired again (as before)");
Thread.Sleep(300);
checks.That(a.Requests == 0, "still no HTTP request");

Console.WriteLine("3. A dead record next to the live one (OscGoesPurrr's log, 2026-11-08)");
var deadPort = FreePort();
took = Offer(mdns, "VRChat-Client-305E19", deadPort);
checks.That(took < 50, $"ResolveVrChatClient returned in {took} ms");
Thread.Sleep(2500);
checks.That(EndpointPort() == a.Port, "the endpoint stays on the VRChat that answers");
checks.That(discovered == 2, "OnVrcClientDiscovered not fired");
checks.That(a.Requests == 1 && a.LastRequestLine.Contains("?HOST_INFO"),
    $"the current VRChat was asked once ({a.Requests}x, \"{a.LastRequestLine}\")");
checks.That(log.Count("ignoring VRChat-Client-305E19") == 1, "the log says it ignored the record");

Console.WriteLine("4. The dead record announced 50 more times within 5 s");
for (var i = 0; i < 50; i++)
{
    Offer(mdns, "VRChat-Client-305E19", deadPort);
}

Thread.Sleep(500);
checks.That(EndpointPort() == a.Port, "the endpoint stays");
checks.That(a.Requests == 1, $"no new HTTP request ({a.Requests} in all)");
checks.That(log.Count("ignoring VRChat-Client-305E19") == 1, "logged once, not per announcement");

Console.WriteLine("5. A second VRChat that answers too (a second client)");
using var b = FakeOscQuery.Start();
Offer(mdns, "VRChat-Client-BBBBBB", b.Port);
Thread.Sleep(2500);
checks.That(EndpointPort() == a.Port && discovered == 2, "the first one stays while it answers");

Console.WriteLine("6. VRChat restarts: the old one's port is closed, the new one answers");
a.Stop();
using var c = FakeOscQuery.Start();
var sw = Stopwatch.StartNew();
took = Offer(mdns, "VRChat-Client-CCCCCC", c.Port);
checks.That(took < 50, $"ResolveVrChatClient returned in {took} ms");
var moved = WaitFor(() => EndpointPort() == c.Port, 4000);
checks.That(moved, $"moved to the new VRChat after {sw.ElapsedMilliseconds} ms");
checks.That(discovered == 3, "OnVrcClientDiscovered fired for the move");
checks.That(log.Count($"OSCQuery moved to 127.0.0.1:{c.Port}") == 1, "the log says it moved");

Console.WriteLine("7. The VRChat in use hangs (accepts, never replies), a new one answers");
c.Hung = true;
using var d = FakeOscQuery.Start();
sw.Restart();
took = Offer(mdns, "VRChat-Client-DDDDDD", d.Port);
checks.That(took < 50, $"ResolveVrChatClient returned in {took} ms while the probe waits for the hung one");
Thread.Sleep(1000);
checks.That(EndpointPort() == c.Port, "still on the hung one 1 s in (its probe hasn't timed out)");
moved = WaitFor(() => EndpointPort() == d.Port, 4000);
checks.That(moved, $"moved to the new VRChat after {sw.ElapsedMilliseconds} ms (probe timeout 2 s)");
checks.That(discovered == 4, "OnVrcClientDiscovered fired for the move");

Console.WriteLine("8. The VRChat in use is gone and the other record doesn't answer either");
d.Stop();
var latePort = FreePort();
var step8 = Stopwatch.StartNew();
Offer(mdns, "VRChat-Client-EEEEEE", latePort);
Thread.Sleep(3000);
checks.That(EndpointPort() == d.Port && discovered == 4, "nothing to move to: the endpoint stays");

Console.WriteLine("9. That VRChat's server comes up after its first announcement");
using var e = FakeOscQuery.Start(latePort);
Offer(mdns, "VRChat-Client-EEEEEE", latePort);
Thread.Sleep(300);
checks.That(EndpointPort() == d.Port, $"not re-checked {step8.ElapsedMilliseconds} ms after the last check");
Thread.Sleep((int)Math.Max(0, 5300 - step8.ElapsedMilliseconds));
sw.Restart();
Offer(mdns, "VRChat-Client-EEEEEE", latePort);
moved = WaitFor(() => EndpointPort() == latePort, 4000);
checks.That(moved, $"the next announcement after 5 s moves to it ({sw.ElapsedMilliseconds} ms)");
checks.That(discovered == 5, "OnVrcClientDiscovered fired for the move");

Console.WriteLine();
Console.WriteLine($"{checks.Passed} passed, {checks.Failed} failed");
return checks.Failed == 0 ? 0 : 1;

// One VRChat OSCQuery announcement, the way VRChat answers a _oscjson._tcp query
static long Offer(MulticastDnsService mdns, string instance, int port)
{
    var service = new List<string> { "_oscjson", "_tcp", "local" };
    var qualified = new List<string> { instance, "_oscjson", "_tcp", "local" };
    var host = new List<string> { instance, "oscjson", "tcp" };
    var packet = new DnsPacket
    {
        QUERYRESPONSE = true,
        answers = new[] { new DnsResource(new PTRRecord { DomainLabels = qualified }, service) },
        additionals = new[]
        {
            new DnsResource(new SRVRecord { Port = (ushort)port, Target = host }, qualified),
            new DnsResource(new ARecord { Address = IPAddress.Loopback }, host),
        },
    };

    // Through the wire format and back, like a packet off the socket
    var received = new DnsPacket(new BigReader(packet.Serialize()));
    var sw = Stopwatch.StartNew();
    mdns.ResolveVrChatClient(received, new IPEndPoint(IPAddress.Loopback, 5353));
    return sw.ElapsedMilliseconds;
}

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static bool WaitFor(Func<bool> condition, int timeoutMs)
{
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < timeoutMs)
    {
        if (condition())
        {
            return true;
        }

        Thread.Sleep(10);
    }

    return condition();
}

sealed class Checks
{
    public int Passed, Failed;

    public void That(bool ok, string what)
    {
        if (ok)
        {
            Passed++;
        }
        else
        {
            Failed++;
        }

        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
    }
}

/// <summary>
/// A stand-in for VRChat's OSCQuery HTTP server: answers every request with 200 and a small
/// HOST_INFO, or, while <see cref="Hung"/>, accepts connections and never replies.
/// </summary>
sealed class FakeOscQuery : IDisposable
{
    private readonly TcpListener _listener;
    private readonly List<TcpClient> _held = new();
    private int _requests;

    public volatile bool Hung;
    public volatile string LastRequestLine = "";
    public int Port { get; }
    public int Requests => Volatile.Read(ref _requests);

    private FakeOscQuery(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    public static FakeOscQuery Start(int port = 0) => new(port);

    private async Task AcceptLoop()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch
            {
                return;
            }

            Interlocked.Increment(ref _requests);
            if (Hung)
            {
                lock (_held)
                {
                    _held.Add(client);
                }

                continue;
            }

            _ = Serve(client);
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var request = new StringBuilder();
                while (!request.ToString().Contains("\r\n\r\n"))
                {
                    var n = await stream.ReadAsync(buffer);
                    if (n == 0)
                    {
                        return;
                    }

                    request.Append(Encoding.ASCII.GetString(buffer, 0, n));
                }

                LastRequestLine = request.ToString().Split("\r\n")[0];
                const string body = "{\"NAME\":\"VRChat-Client-FAKE\",\"OSC_IP\":\"127.0.0.1\",\"OSC_PORT\":9000}";
                var head = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                           $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head + body));
            }
            catch
            {
                // The probe gave up; nothing to do
            }
        }
    }

    /// <summary>Closes the port, like VRChat exiting: new connections are refused.</summary>
    public void Stop()
    {
        _listener.Stop();
        lock (_held)
        {
            foreach (var client in _held)
            {
                client.Dispose();
            }

            _held.Clear();
        }
    }

    public void Dispose() => Stop();
}

sealed class ListLogger<T> : ILogger<T>
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _lines = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var line = $"[{_clock.ElapsedMilliseconds,6} ms] {logLevel}: {formatter(state, exception)}";
        lock (_lines)
        {
            _lines.Add(line);
        }

        Console.WriteLine("  log " + line);
    }

    public int Count(string text)
    {
        lock (_lines)
        {
            return _lines.Count(l => l.Contains(text));
        }
    }
}
