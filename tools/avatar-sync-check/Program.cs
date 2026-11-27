// Offline check: avatar changes load one at a time, after VRChat has settled, never from a tree
// that still shows another avatar, and an avatar VRChat switched to without an /avatar/change
// message still gets loaded (FORK-NOTES.md, "Avatar changes").
//
//   dotnet run --project tools/avatar-sync-check
//
// A: AvatarSync against a scripted fake VRChat (short timings, so it runs in seconds).
// B: the real OscQueryConfigParser against a fake VRChat OSCQuery server on loopback.
// C: both together, replaying 2026-11-27: VRChat switched back to a native face tracking avatar
//    and sent no /avatar/change, and the app stayed on the avatar before.
//
// Touches nothing outside this process: its own loopback ports only, no mDNS (the mDNS service
// is created without running its constructor, so it binds no socket), no VRChat files (every
// fake tree carries a name). Safe to run while VRChat and VRCFaceTracking are open.
// Exit code 0 when every check passes.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using VRCFaceTracking;
using VRCFaceTracking.Core;
using VRCFaceTracking.Core.mDNS;
using VRCFaceTracking.Core.Params;
using VRCFaceTracking.Core.Services;

if (args is ["--live", var livePort])
{
    return await LiveCheck.Run(int.Parse(livePort));
}

var checks = new Checks();
var log = new ListLogger<AvatarSync>();

const string missedLine = "without an /avatar/change message";

// ---------------------------------------------------------------------------------------------
Console.WriteLine("A. AvatarSync against a scripted VRChat (settle 200 ms, max 800 ms, check every 250 ms, re-read every 100 ms x4)");
var vrc = new ScriptedVrchat { Node = "avtr_A", Tree = "avtr_A" };
var sync = new AvatarSync(log, vrc.Load, vrc.Read,
    settle: TimeSpan.FromMilliseconds(200), maxSettle: TimeSpan.FromMilliseconds(800),
    checkEvery: TimeSpan.FromMilliseconds(250), staleRetryDelay: TimeSpan.FromMilliseconds(100),
    staleRetries: 4);

Console.WriteLine("A1. Requests before Start and odd arguments never throw");
var threw = false;
try
{
    sync.Request(null);
    sync.Request("");
    sync.Request("avtr_A", immediate: true);
}
catch (Exception)
{
    threw = true;
}

checks.That(!threw, "Request before Start, with null, empty and immediate, didn't throw");

Console.WriteLine("A2. The first load after discovery doesn't wait");
var asked = TestClock.Ms;
sync.Start();
checks.That(WaitFor(() => sync.LoadedAvatarId == "avtr_A", 1000), "avtr_A loaded");
checks.That(vrc.FirstStartAfter(asked) - asked < 120, $"load started {vrc.FirstStartAfter(asked) - asked} ms after the immediate request");

Console.WriteLine("A3. A change waits for the settle time, then loads once");
Thread.Sleep(300);
vrc.Reset();
vrc.Node = vrc.Tree = "avtr_B";
asked = TestClock.Ms;
sync.Request("avtr_B");
Thread.Sleep(100);
checks.That(vrc.Calls == 0, $"nothing loaded 100 ms after the message ({vrc.Calls} loads)");
checks.That(WaitFor(() => sync.LoadedAvatarId == "avtr_B", 1000), "avtr_B loaded");
var waited = vrc.FirstStartAfter(asked) - asked;
checks.That(waited is >= 190 and < 450, $"load started {waited} ms after the message");
checks.That(vrc.Calls == 1 && vrc.Expected[0] == "avtr_B", $"one load, asked for avtr_B ({vrc.Calls} loads)");

Console.WriteLine("A4. Doubled and rapid messages fold into one load of the last avatar");
vrc.Reset();
vrc.Node = vrc.Tree = "avtr_C";
foreach (var message in new[] { "avtr_B", "avtr_C", "avtr_B", "avtr_C", "avtr_C" })
{
    sync.Request(message);
    Thread.Sleep(40);
}

Thread.Sleep(600);
checks.That(vrc.Calls == 1, $"one load for five messages ({vrc.Calls})");
checks.That(sync.LoadedAvatarId == "avtr_C" && vrc.Expected.LastOrDefault() == "avtr_C", "the last avatar named was loaded");

Console.WriteLine("A5. VRChat switches without a message (2026-11-27): the check loads it");
vrc.Reset();
var missedBefore = log.Count(missedLine);
vrc.Node = vrc.Tree = "avtr_D";
var switched = TestClock.Ms;
checks.That(WaitFor(() => sync.LoadedAvatarId == "avtr_D", 2000), "avtr_D loaded with no message at all");
checks.That(TestClock.Ms - switched < 1000, $"{TestClock.Ms - switched} ms after the switch (check 250 + settle 200)");
checks.That(log.Count(missedLine) == missedBefore + 1, "the log says VRChat switched without a message");
checks.That(vrc.Calls == 1, $"one load ({vrc.Calls})");

Console.WriteLine("A6. The check sees the new avatar first and the message follows: one load, not called a miss");
vrc.Reset();
missedBefore = log.Count(missedLine);
vrc.Node = vrc.Tree = "avtr_E";
var readsBefore = vrc.Reads;
WaitFor(() => vrc.Reads > readsBefore, 1000);
sync.Request("avtr_E");
checks.That(WaitFor(() => sync.LoadedAvatarId == "avtr_E", 1000), "avtr_E loaded");
Thread.Sleep(300);
checks.That(vrc.Calls == 1, $"one load ({vrc.Calls})");
checks.That(log.Count(missedLine) == missedBefore, "not logged as a missed message");

Console.WriteLine("A7. The tree still shows the old avatar: read again, never applied");
vrc.Reset();
vrc.Node = "avtr_F"; // VRChat's node already says F, its tree is still being rebuilt
sync.Request("avtr_F");
Thread.Sleep(350);
vrc.Tree = "avtr_F";
checks.That(WaitFor(() => sync.LoadedAvatarId == "avtr_F", 1500), "avtr_F loaded once the tree caught up");
checks.That(vrc.Calls >= 2 && vrc.Stale >= 1, $"{vrc.Calls} reads of the tree, {vrc.Stale} of them stale");
checks.That(vrc.Applied.SequenceEqual(new[] { "avtr_F" }), $"applied only avtr_F ({string.Join(", ", vrc.Applied)})");

Console.WriteLine("A8. A change that never shows up in the tree: after the retries, what VRChat shows is used");
vrc.Reset();
vrc.Node = vrc.Tree = "avtr_E"; // a message for G, but VRChat stayed on (went back to) E
sync.Request("avtr_G");
checks.That(WaitFor(() => sync.LoadedAvatarId == "avtr_E", 2000), "avtr_E (what VRChat shows) loaded");
checks.That(vrc.Stale == 5 && vrc.Expected.Last() == null, $"{vrc.Stale} stale reads, then one taking what is on");
checks.That(log.Count("still shows avatar avtr_E") == 1, "logged once");

Console.WriteLine("A9. Loads never overlap, and the newest change wins");
vrc.Reset();
vrc.DelayMs = 300;
vrc.Node = vrc.Tree = "avtr_H";
sync.Request("avtr_H");
Thread.Sleep(300); // the H load is running now
vrc.Node = vrc.Tree = "avtr_I";
sync.Request("avtr_I");
checks.That(WaitFor(() => sync.LoadedAvatarId == "avtr_I", 3000), "avtr_I loaded");
Thread.Sleep(200);
checks.That(vrc.MaxConcurrent == 1, $"at most one load at a time ({vrc.MaxConcurrent})");
checks.That(!vrc.Applied.Contains("avtr_H") && vrc.Applied.LastOrDefault() == "avtr_I",
    $"the H load saw VRChat on I and stood down; applied: {string.Join(", ", vrc.Applied)}");
vrc.DelayMs = 0;

Console.WriteLine("A10. VRChat doesn't answer: nothing is loaded");
vrc.Reset();
vrc.Node = null;
vrc.Tree = "avtr_J";
Thread.Sleep(1000);
checks.That(vrc.Calls == 0 && vrc.Reads >= 3, $"{vrc.Reads} checks, {vrc.Calls} loads");

Console.WriteLine("A11. Loads that keep failing back off; a message still loads at once");
vrc.Reset();
vrc.Fail = true;
vrc.Node = vrc.Tree = "avtr_K";
Thread.Sleep(4000);
var failedLoads = vrc.Calls;
checks.That(failedLoads is >= 2 and <= 5, $"{failedLoads} loads in 4 s (about 9 without backing off)");
var callsBefore = vrc.Calls;
asked = TestClock.Ms;
sync.Request("avtr_K");
checks.That(WaitFor(() => vrc.Calls > callsBefore, 800), $"the message's load ran {vrc.FirstStartAfter(asked) - asked} ms later, backoff or not");
vrc.Fail = false;
sync.Request("avtr_K");
checks.That(WaitFor(() => sync.LoadedAvatarId == "avtr_K", 1000), "avtr_K loaded once loads work again");

Console.WriteLine("A12. Changes that never stop still load within the max settle time");
vrc.Reset();
vrc.Node = vrc.Tree = "avtr_L";
asked = TestClock.Ms;
var until = asked + 1500;
while (TestClock.Ms < until)
{
    sync.Request("avtr_L");
    Thread.Sleep(100);
}

var firstStart = vrc.FirstStartAfter(asked) - asked;
checks.That(firstStart is >= 750 and < 1100, $"first load started {firstStart} ms into a stream of messages every 100 ms");

sync.Stop();
threw = false;
try
{
    sync.Request("avtr_M");
}
catch (Exception)
{
    threw = true;
}

checks.That(!threw, "Request after Stop doesn't throw");

// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("B. OscQueryConfigParser against a fake VRChat OSCQuery server");
var parserLog = new ListLogger<OscQueryConfigParser>();
var mdns = (MulticastDnsService)RuntimeHelpers.GetUninitializedObject(typeof(MulticastDnsService));
var parser = new OscQueryConfigParser(parserLog, new AvatarConfigParser(new ListLogger<AvatarConfigParser>()), mdns);

const string legacy = "avtr_legacy-0000";
const string native = "avtr_native-1111";
var legacyTree = FakeTree.Build(legacy, "Legacy Test", nativeFaceTracking: false,
    ("/avatar/parameters/v2/EyeOpenLeft", "f"), ("/avatar/parameters/v2/PupilDilation", "f"),
    ("/avatar/parameters/FT/v2/EyeOpenRight", "f"), ("/avatar/parameters/SomethingElse", "i"));
var nativeTree = FakeTree.Build(native, "Native Test", nativeFaceTracking: true,
    ("/avatar/parameters/v2/EyeOpenLeft", "f"), ("/avatar/parameters/SomethingElse", "i"));

Console.WriteLine("B1. VRChat's /avatar/change answer");
checks.That(OscQueryConfigParser.ParseAvatarIdNode(
        "{\"DESCRIPTION\":\"Avatar ID, updated whenever the user switches into a valid avatar.\",\"FULL_PATH\":\"/avatar/change\",\"ACCESS\":3,\"TYPE\":\"s\",\"VALUE\":[\"avtr_x\"]}")
    == "avtr_x", "VRChat's format (as served on 2026-11-27) gives the id");
checks.That(OscQueryConfigParser.ParseAvatarIdNode("{\"FULL_PATH\":\"/avatar/change\",\"VALUE\":[]}") == null, "no value: null");
checks.That(OscQueryConfigParser.ParseAvatarIdNode("not json") == null, "garbage: null, no exception");

Console.WriteLine("B2. Reading the avatar id");
checks.That(await parser.ReadCurrentAvatarId() == null, "no OSCQuery endpoint yet: null");
using var server = new FakeVrchatOscQuery { Tree = legacyTree, AvatarId = legacy };
mdns.VrchatClientEndpoint = new IPEndPoint(IPAddress.Loopback, server.Port);
checks.That(await parser.ReadCurrentAvatarId() == legacy, "from the fake VRChat: the legacy avatar");
checks.That(server.LastPath == "/avatar/change", $"asked /avatar/change only ({server.LastPath})");

server.Hung = true;
var sw = Stopwatch.StartNew();
var hungId = await parser.ReadCurrentAvatarId();
checks.That(hungId == null && sw.ElapsedMilliseconds is >= 1900 and < 3000, $"VRChat hung: null after {sw.ElapsedMilliseconds} ms");
server.Hung = false;

var closed = new IPEndPoint(IPAddress.Loopback, FreePort());
mdns.VrchatClientEndpoint = closed;
sw.Restart();
checks.That(await parser.ReadCurrentAvatarId() == null && sw.ElapsedMilliseconds < 3000, $"VRChat closed: null after {sw.ElapsedMilliseconds} ms");
mdns.VrchatClientEndpoint = new IPEndPoint(IPAddress.Loopback, server.Port);

Console.WriteLine("B3. Loading the legacy avatar");
var (outcome, id, info, list) = await parser.LoadAvatar();
checks.That(outcome == AvatarLoadOutcome.Loaded && id == legacy && info?.FullFaceTracking == false,
    $"Loaded {id}, native face tracking {info?.FullFaceTracking}");
var relevantLegacy = RelevantCount();
checks.That(list?.Count >= 2 && relevantLegacy >= 2, $"{list?.Count} parameters mapped, {relevantLegacy} relevant");

Console.WriteLine("B4. A tree that shows another avatar than asked for changes nothing");
server.Tree = nativeTree;
server.AvatarId = native;
(outcome, id, info, list) = await parser.LoadAvatar("avtr_somethingelse");
checks.That(outcome == AvatarLoadOutcome.Stale && id == native && info == null, $"Stale, VRChat shows {id}");
checks.That(RelevantCount() == relevantLegacy, $"still {RelevantCount()} relevant: no parameter was reset");

Console.WriteLine("B5. Loading the native face tracking avatar it shows");
(outcome, id, info, list) = await parser.LoadAvatar(native);
checks.That(outcome == AvatarLoadOutcome.Loaded && info?.FullFaceTracking == true && list?.Count == 0,
    $"Loaded {id}: native face tracking {info?.FullFaceTracking}, {list?.Count} parameters");
checks.That(RelevantCount() == 0, $"{RelevantCount()} relevant (native face tracking sends no v2 parameters)");

Console.WriteLine("B6. VRChat answers /avatar with an error");
server.FailTree = true;
(outcome, _, _, _) = await parser.LoadAvatar();
checks.That(outcome == AvatarLoadOutcome.Failed, $"{outcome}");
server.FailTree = false;

// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("C. Both together, replaying 2026-11-27 (settle 300 ms, check every 500 ms, re-read every 200 ms x4)");
var syncLog = new ListLogger<AvatarSync>();
var nativeLines = () => parserLog.Count("uses VRChat's native face tracking");
server.Tree = legacyTree;
server.AvatarId = legacy;
var real = new AvatarSync(syncLog,
    async (expected, ct) =>
    {
        var (o, i, _, _) = await parser.LoadAvatar(expected, ct);
        return new AvatarLoadResult(o, i);
    },
    parser.ReadCurrentAvatarId,
    settle: TimeSpan.FromMilliseconds(300), maxSettle: TimeSpan.FromSeconds(2),
    checkEvery: TimeSpan.FromMilliseconds(500), staleRetryDelay: TimeSpan.FromMilliseconds(200));
real.Start();
real.Request(null, immediate: true);
checks.That(WaitFor(() => real.LoadedAvatarId == legacy, 2000) && RelevantCount() == relevantLegacy,
    "C1. started on the parameter avatar");

Console.WriteLine("C2. VRChat switches to the native avatar and sends no /avatar/change");
server.Tree = nativeTree;
server.AvatarId = native;
sw.Restart();
checks.That(WaitFor(() => real.LoadedAvatarId == native, 3000), $"native avatar loaded after {sw.ElapsedMilliseconds} ms without any message");
checks.That(RelevantCount() == 0, "its parameters are mapped (none relevant: native face tracking is on)");
checks.That(syncLog.Count(missedLine) == 1, "the log says VRChat switched without a message");

Console.WriteLine("C3. The message runs ahead of the tree: switch back to legacy, tree still native for 600 ms");
var nativeBefore = nativeLines();
server.AvatarId = legacy;
real.Request(legacy);
Thread.Sleep(600);
server.Tree = legacyTree;
checks.That(WaitFor(() => real.LoadedAvatarId == legacy, 3000), "legacy avatar loaded once the tree caught up");
checks.That(nativeLines() == nativeBefore, "the native tree it read in between was never applied");
checks.That(RelevantCount() == relevantLegacy, $"{RelevantCount()} relevant again");
real.Stop();

Console.WriteLine();
Console.WriteLine($"{checks.Passed} passed, {checks.Failed} failed");
return checks.Failed == 0 ? 0 : 1;

// Parameters currently mapped to the avatar: what the next send would carry
static int RelevantCount() => UnifiedTracking.AllParameters
    .SelectMany(p => p.GetParamNames())
    .Select(n => n.Item2)
    .Distinct()
    .Count(p => p.GetType().GetProperty("Relevant")?.GetValue(p) is true);

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

        Thread.Sleep(5);
    }

    return condition();
}

/// <summary>
/// VRChat as AvatarSync sees it: a node that says which avatar is on (null: not answering) and a
/// tree that may still show the avatar before. Loads follow the real loader's rules.
/// </summary>
sealed class ScriptedVrchat
{

    private readonly object _lock = new();
    private int _concurrent;

    public volatile string? Node;
    public volatile string? Tree;
    public volatile bool Fail;
    public volatile int DelayMs;

    public int Calls, Reads, Stale, MaxConcurrent;
    public readonly List<string?> Expected = new();
    public readonly List<string> Applied = new();
    private readonly List<long> _starts = new();

    public void Reset()
    {
        lock (_lock)
        {
            Calls = Stale = MaxConcurrent = 0;
            Expected.Clear();
            Applied.Clear();
            _starts.Clear();
        }
    }

    public long FirstStartAfter(long ms)
    {
        lock (_lock)
        {
            var later = _starts.Where(s => s >= ms).ToList();
            return later.Count > 0 ? later.Min() : long.MaxValue / 2;
        }
    }

    public async Task<AvatarLoadResult> Load(string? expected, CancellationToken ct)
    {
        lock (_lock)
        {
            Calls++;
            Expected.Add(expected);
            _starts.Add(TestClock.Ms);
            MaxConcurrent = Math.Max(MaxConcurrent, ++_concurrent);
        }

        try
        {
            if (DelayMs > 0)
            {
                await Task.Delay(DelayMs, ct);
            }

            var shows = Tree;
            if (Fail || shows == null)
            {
                return new AvatarLoadResult(AvatarLoadOutcome.Failed, null);
            }

            if (expected != null && expected != shows)
            {
                lock (_lock)
                {
                    Stale++;
                }

                return new AvatarLoadResult(AvatarLoadOutcome.Stale, shows);
            }

            lock (_lock)
            {
                Applied.Add(shows);
            }

            return new AvatarLoadResult(AvatarLoadOutcome.Loaded, shows);
        }
        finally
        {
            lock (_lock)
            {
                _concurrent--;
            }
        }
    }

    public Task<string?> Read(CancellationToken ct)
    {
        Interlocked.Increment(ref Reads);
        return Task.FromResult(Node);
    }
}

static class FakeTree
{
    /// <summary>An /avatar tree shaped like VRChat's: change, name, the native flag, parameters.</summary>
    public static string Build(string id, string name, bool nativeFaceTracking, params (string path, string type)[] parameters)
    {
        static JObject Node(string path, string? type = null, object? value = null)
        {
            var node = new JObject { ["FULL_PATH"] = path, ["ACCESS"] = type == null ? 0 : 3 };
            if (type != null)
            {
                node["TYPE"] = type;
                node["VALUE"] = new JArray(value!);
            }

            return node;
        }

        var parametersNode = Node("/avatar/parameters");
        parametersNode["CONTENTS"] = new JObject();
        foreach (var (path, type) in parameters)
        {
            var parent = parametersNode;
            var parts = path["/avatar/parameters/".Length..].Split('/');
            var at = "/avatar/parameters";
            for (var i = 0; i < parts.Length; i++)
            {
                at += "/" + parts[i];
                var contents = (JObject)(parent["CONTENTS"] ??= new JObject());
                if (i == parts.Length - 1)
                {
                    contents[parts[i]] = Node(at, type, type == "f" ? 0.0f : 0);
                }
                else
                {
                    parent = (JObject)(contents[parts[i]] ??= Node(at));
                }
            }
        }

        var root = Node("/avatar");
        root["CONTENTS"] = new JObject
        {
            ["change"] = Node("/avatar/change", "s", id),
            ["name"] = Node("/avatar/name", "s", name),
            ["avatarfullfacetracking"] = Node("/avatar/avatarfullfacetracking", "T", nativeFaceTracking),
            ["parameters"] = parametersNode,
        };
        return root.ToString(Newtonsoft.Json.Formatting.None);
    }
}

/// <summary>
/// VRChat's OSCQuery HTTP server as far as avatars go: /avatar (the tree) and /avatar/change
/// (the id node), answered separately so a test can make them disagree. While
/// <see cref="Hung"/>, it accepts connections and never replies.
/// </summary>
sealed class FakeVrchatOscQuery : IDisposable
{
    private readonly TcpListener _listener;
    private readonly List<TcpClient> _held = new();

    public volatile string Tree = "{}";
    public volatile string AvatarId = "";
    public volatile bool Hung;
    public volatile bool FailTree;
    public volatile string LastPath = "";
    public int Port { get; }

    public FakeVrchatOscQuery()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

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

                var path = request.ToString().Split("\r\n")[0].Split(' ')[1];
                LastPath = path;
                var (status, body) = path switch
                {
                    "/avatar" when FailTree => ("500 Internal Server Error", "{}"),
                    "/avatar" => ("200 OK", Tree),
                    "/avatar/change" => ("200 OK",
                        "{\"DESCRIPTION\":\"Avatar ID, updated whenever the user switches into a valid avatar.\"," +
                        $"\"FULL_PATH\":\"/avatar/change\",\"ACCESS\":3,\"TYPE\":\"s\",\"VALUE\":[\"{AvatarId}\"]}}"),
                    _ => ("404 Not Found", "{}"),
                };
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\n" +
                           $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(bytes);
            }
            catch
            {
                // The client gave up; nothing to do
            }
        }
    }

    public void Dispose()
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

sealed class ListLogger<T> : ILogger<T>
{

    private readonly List<string> _lines = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var line = $"[{TestClock.Ms,6} ms] {logLevel}: {formatter(state, exception)}";
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

/// <summary>One clock for the checks and the scripted VRChat, so their times compare.</summary>
static class TestClock
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();

    public static long Ms => Watch.ElapsedMilliseconds;
}

/// <summary>
/// --live PORT: the same reads against a running VRChat's OSCQuery port (VRCFaceTracking's log
/// names it: "OSCQuery detected at 127.0.0.1:PORT"). Only GETs /avatar/change and /avatar; the
/// parameter mapping it changes is this process's own.
/// </summary>
static class LiveCheck
{
    public static async Task<int> Run(int port)
    {
        var mdns = (MulticastDnsService)RuntimeHelpers.GetUninitializedObject(typeof(MulticastDnsService));
        mdns.VrchatClientEndpoint = new IPEndPoint(IPAddress.Loopback, port);
        var parser = new OscQueryConfigParser(new ListLogger<OscQueryConfigParser>(),
            new AvatarConfigParser(new ListLogger<AvatarConfigParser>()), mdns);

        var sw = Stopwatch.StartNew();
        var current = await parser.ReadCurrentAvatarId();
        Console.WriteLine($"/avatar/change: {current ?? "(no answer)"} in {sw.ElapsedMilliseconds} ms");
        if (current == null)
        {
            return 1;
        }

        sw.Restart();
        var (outcome, id, info, list) = await parser.LoadAvatar(current);
        Console.WriteLine($"/avatar, expecting {current}: {outcome} {id} \"{info?.Name}\", native face tracking {info?.FullFaceTracking}, " +
                          $"{list?.Count} parameters mapped, in {sw.ElapsedMilliseconds} ms");
        var (wrong, shows, _, _) = await parser.LoadAvatar("avtr_00000000-0000-0000-0000-000000000000");
        Console.WriteLine($"/avatar, expecting another avatar: {wrong} (VRChat shows {shows})");
        return outcome == AvatarLoadOutcome.Loaded && id == current && wrong == AvatarLoadOutcome.Stale ? 0 : 1;
    }
}
