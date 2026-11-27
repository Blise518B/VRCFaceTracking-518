// Offline check: native face tracking hands the face back to VRChat when tracking stalls.
//
//   dotnet run --project tools/native-ft-stall-check [-- --port 39555]
//
// Part 1 drives NativeFrameGate with a synthetic clock. Part 2 checks
// ReplyUpdatePacket.TrackingChanged on decoded IPC frames. Part 3 runs the real pipeline for
// 8 s: module frames -> VrcftPacketDecoder -> UnifiedLibManager.ApplyTrackingFrame ->
// ParameterSenderService's 100 Hz loop -> NativeFaceTrackingSender -> OscSendService, into a
// UDP socket on loopback that records every datagram. Exit code 0 when every check passes.
// Nothing goes to VRChat: the sender targets this program's own port (random unless --port).

using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using VRCFaceTracking;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.OSC;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Expressions;
using VRCFaceTracking.Core.Sandboxing;
using VRCFaceTracking.Core.Sandboxing.IPC;
using VRCFaceTracking.Core.Services;
using Frame = VRCFaceTracking.Core.Services.NativeFrameGate.Frame;

var port = 0;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port")
    {
        port = int.Parse(args[i + 1]);
    }
}

VRCFaceTracking.Core.Utils.TimeBeginPeriod(1);
var checks = new Checks();

GateChecks.Run(checks);
FrameChecks.Run(checks);
await PipelineRun.Run(checks, port);

VRCFaceTracking.Core.Utils.TimeEndPeriod(1);
Console.WriteLine();
Console.WriteLine(checks.Failed == 0
    ? $"ALL {checks.Passed} CHECKS PASSED"
    : $"{checks.Failed} OF {checks.Passed + checks.Failed} CHECKS FAILED");
return checks.Failed == 0 ? 0 : 1;

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

/// <summary>Part 1: the gate's decisions on a synthetic clock, 10 ms ticks.</summary>
static class GateChecks
{
    private static long Ms(double ms) => (long)(ms * Stopwatch.Frequency / 1000.0);

    // Any non-zero origin: 0 means "no fresh data" to the gate
    private static readonly long Origin = Ms(3_600_000);

    /// <summary>
    /// Ticks every 10 ms from 0 to <paramref name="endMs"/>. <paramref name="lastFreshAt"/> says
    /// what TrackingFreshness reads on that tick (ms of the last changed frame, or null after
    /// MarkLost); <paramref name="runs"/> leaves out ticks the loop never got (a pause).
    /// </summary>
    private static List<(double Ms, Frame Frame)> Drive(double endMs, Func<double, double?> lastFreshAt,
        Func<double, bool>? runs = null)
    {
        var gate = new NativeFrameGate();
        var result = new List<(double, Frame)>();
        for (var ms = 0.0; ms < endMs; ms += 10)
        {
            if (runs != null && !runs(ms))
            {
                continue;
            }

            var fresh = lastFreshAt(ms);
            result.Add((ms, gate.Next(Origin + Ms(ms), fresh == null ? 0 : Origin + Ms(fresh.Value))));
        }

        return result;
    }

    private static string Show(IEnumerable<(double Ms, Frame Frame)> ticks) =>
        string.Join(", ", ticks.Select(t => $"{t.Frame} at {t.Ms:F0} ms"));

    public static void Run(Checks c)
    {
        Console.WriteLine("Part 1: NativeFrameGate on a synthetic clock (10 ms ticks, module frame 3 ms before each tick)");

        // Live until 1000 ms, stalled (last change at 987 ms) until 2500 ms, then live again
        var stall = Drive(3000, ms => ms < 1000 || ms >= 2500 ? ms - 3 : 987);
        var neutral = stall.Where(t => t.Frame == Frame.Neutral).ToList();
        c.That(stall.Where(t => t.Ms < 1530).All(t => t.Frame == Frame.Live),
            "stall: live blobs until the timeout, frozen values included");
        c.That(neutral.Count == 1 && neutral[0].Ms == 1530,
            $"stall: one neutral frame 543 ms after the last change (500 ms + 5 ticks): {Show(neutral)}");
        c.That(stall.Where(t => t.Ms > 1530 && t.Ms < 2500).All(t => t.Frame == Frame.None),
            "stall: nothing sent after the neutral frame");
        c.That(stall.Single(t => t.Ms == 2500).Frame == Frame.Resumed &&
               stall.Where(t => t.Ms > 2500).All(t => t.Frame == Frame.Live),
            "stall: the first fresh tick resumes (2500 ms), live after");

        // Our own 700 ms GC pause: no ticks in between, and the receive thread needs three
        // ticks after it to work through the frames that queued up meanwhile
        var pause = Drive(2500, ms => ms is >= 1700 and < 1730 ? 987 : ms - 3, ms => ms <= 990 || ms >= 1700);
        c.That(pause.All(t => t.Frame == Frame.Live),
            $"own 700 ms pause: no neutral frame (ticks after it: {Show(pause.Where(t => t.Ms is >= 1700 and < 1740))})");

        // The last module dies: MarkLost at 1000 ms, a respawned module changes data at 1500 ms
        var lost = Drive(2000, ms => ms < 1000 ? ms - 3 : ms < 1500 ? null : ms - 3);
        c.That(lost.Single(t => t.Ms == 1000).Frame == Frame.Neutral && lost.Count(t => t.Frame == Frame.Neutral) == 1,
            "module lost: neutral frame on the same tick, no timeout wait");
        c.That(lost.Where(t => t.Ms > 1000 && t.Ms < 1500).All(t => t.Frame == Frame.None) &&
               lost.Single(t => t.Ms == 1500).Frame == Frame.Resumed,
            "module lost: silent until a module delivers changed data, then resumes");

        // Avatar loaded while tracking is stale (last change 10 s ago); data returns at 500 ms
        var idle = Drive(1000, ms => ms < 500 ? -10_000 : ms - 3);
        c.That(idle.Where(t => t.Ms < 500).All(t => t.Frame == Frame.None) &&
               idle.Single(t => t.Ms == 500).Frame == Frame.Live &&
               idle.All(t => t.Frame is Frame.None or Frame.Live),
            "stale at subscription: nothing sent and no neutral frame until data, then live");

        // A 450 ms hiccup stays under the timeout
        var hiccup = Drive(2000, ms => ms < 1000 || ms >= 1450 ? ms - 3 : 987);
        c.That(hiccup.All(t => t.Frame == Frame.Live), "450 ms hiccup: no neutral frame");
    }
}

/// <summary>IPC frames as a module process sends them, decoded the way the app decodes them.</summary>
static class Ipc
{
    public const int HeaderFloats = 16;
    public const int LeftGazeX = 2, LeftGazeY = 3, LeftPupil = 4, LeftOpenness = 5;
    public const int RightGazeX = 6, RightGazeY = 7, RightPupil = 8, RightOpenness = 9;
    public const int HeadYaw = 10;
    public static readonly int ExpressionCount = (int)UnifiedExpressions.Max + 1;
    private static readonly byte[] Magic = { 0xAF, 0xEC, 0x00, 0x8D };

    public static int Shape(UnifiedExpressions e) => HeaderFloats + (int)e;

    /// <summary>A module process's start-up state, UnifiedTrackingData's defaults: eyes open, the rest 0.</summary>
    public static float[] StartupFrame()
    {
        var f = new float[HeaderFloats + ExpressionCount];
        f[LeftOpenness] = 1f;
        f[RightOpenness] = 1f;
        return f;
    }

    public static ReplyUpdatePacket Decode(float[] frame)
    {
        var bytes = new byte[12 + frame.Length * sizeof(float)];
        Magic.CopyTo(bytes, 0);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), (uint)IpcPacket.PacketType.ReplyUpdate);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), frame.Length * sizeof(float));
        Buffer.BlockCopy(frame, 0, bytes, 12, frame.Length * sizeof(float));
        return VrcftPacketDecoder.TryDecodePacket(bytes, out var packet) && packet is ReplyUpdatePacket reply
            ? reply
            : throw new InvalidOperationException("The frame did not decode as a ReplyUpdatePacket");
    }

    public static ModuleRuntimeInfo Module(string name) => new()
    {
        ModuleClassName = name,
        Status = ModuleState.Active,
        ModuleInformation = new ModuleMetadataInternal { Name = name, UsingEye = true, UsingExpression = true },
    };
}

/// <summary>Part 2: which frames count as new tracking data.</summary>
static class FrameChecks
{
    public static void Run(Checks c)
    {
        Console.WriteLine();
        Console.WriteLine("Part 2: ReplyUpdatePacket.TrackingChanged on decoded IPC frames");

        var f = Ipc.StartupFrame();
        float[]? both = null;
        c.That(!Ipc.Decode(f).TrackingChanged(ref both, true, true) && both != null,
            "a module's first frame only primes the comparison (its start-up defaults never count)");
        c.That(!Ipc.Decode(f).TrackingChanged(ref both, true, true), "an identical frame is no new data");

        f[Ipc.HeadYaw] = 0.3f;
        c.That(!Ipc.Decode(f).TrackingChanged(ref both, true, true), "a head pose change alone is no new data");

        f[Ipc.LeftGazeX] = 0.1f;
        float[]? faceOnly = (float[])both!.Clone();
        float[]? eyeOnly = (float[])both.Clone();
        c.That(Ipc.Decode(f).TrackingChanged(ref eyeOnly, true, false), "a gaze change is new data for the eye slot owner");
        c.That(!Ipc.Decode(f).TrackingChanged(ref faceOnly, false, true), "... but not for a module that only owns the face slot");
        Ipc.Decode(f).TrackingChanged(ref both, true, true);

        f[Ipc.Shape(UnifiedExpressions.EyeSquintRight)] = 0.4f;
        faceOnly = (float[])both!.Clone();
        c.That(!Ipc.Decode(f).TrackingChanged(ref faceOnly, false, true), "eye-region shapes belong to the eye slot");
        Ipc.Decode(f).TrackingChanged(ref both, true, true);

        f[Ipc.Shape(UnifiedExpressions.JawOpen)] = 0.31f;
        c.That(Ipc.Decode(f).TrackingChanged(ref both, false, true), "a jaw change is new data for the face slot owner");

        f[Ipc.Shape(UnifiedExpressions.JawOpen)] = MathF.BitIncrement(0.31f);
        c.That(Ipc.Decode(f).TrackingChanged(ref both, false, true),
            "a one-ulp change counts (a still but live face never looks stalled)");

        f[Ipc.Shape(UnifiedExpressions.JawOpen)] = float.NaN;
        Ipc.Decode(f).TrackingChanged(ref both, true, true);
        c.That(!Ipc.Decode(f).TrackingChanged(ref both, true, true), "a NaN that stays NaN is no change");

        f[Ipc.Shape(UnifiedExpressions.BrowOuterUpLeft)] = 0.2f;
        float[]? eyeCopy = (float[])both!.Clone();
        c.That(Ipc.Decode(f).TrackingChanged(ref eyeCopy, true, false) && Ipc.Decode(f).TrackingChanged(ref both, false, true),
            "BrowOuterUpLeft, written by both slots, counts for either owner");
    }
}

/// <summary>Part 3: the real pipeline over loopback UDP.</summary>
static class PipelineRun
{
    // Timeline, ms from the start of the run
    private const double LiveUntil = 1500;      // module A tracks
    private const double StallUntil = 3500;     // A's tracker stalls: A repeats its last values, head still moving
    private const double LiveAgainUntil = 4500; // A tracks again
    private const double StillUntil = 5500;     // A's face holds still: floats move by one ulp, bytes don't
    private const double LostAt = 5700;         // A's process died at 5500; the supervisor notices now
    private const double RespawnAt = 6500;      // module B (respawned) answers with its start-up values
    private const double RespawnLiveAt = 7000;  // B tracks
    private const double End = 8000;

    private static readonly List<(long Ts, byte[] Data)> Received = new();

    public static async Task Run(Checks c, int port)
    {
        Console.WriteLine();
        Console.WriteLine("Part 3: the real pipeline over loopback UDP (module frames 100/s from a 72 Hz tracker)");

        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        var target = new LoopbackTarget(((IPEndPoint)receiver.Client.LocalEndPoint!).Port);
        Console.WriteLine($"  recording on 127.0.0.1:{target.OutPort}");
        var rx = new Thread(() =>
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                while (true)
                {
                    var data = receiver.Receive(ref from);
                    lock (Received)
                    {
                        Received.Add((Stopwatch.GetTimestamp(), data));
                    }
                }
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }) { IsBackground = true };
        rx.Start();

        // The app's own services, minus the ones this run never reaches
        var sendService = new OscSendService(new TimeLogger<OscSendService>(), target);
        sendService.EnsureConnected();
        var mutator = new UnifiedTrackingMutator(new TimeLogger<UnifiedTrackingMutator>(), null!, null!);
        var loop = new ParameterSenderService(sendService, mutator, new TimeLogger<ParameterSenderService>());
        var oscQuery = new OscQueryService(new TimeLogger<OscQueryService>(), null!, null!, loop, target, null!, null!,
            sendService, null!, null!, null!);
        var avatar = new NativeAvatar();
        oscQuery.AvatarInfo = avatar;

        // What an avatar load does to the legacy path: a native avatar switches every parameter
        // off (BaseParam.ResetParam), so the blob is all that goes out
        foreach (var parameter in UnifiedTracking.AllParameters)
        {
            parameter.ResetParam(Array.Empty<IParameterDefinition>(), avatar);
        }

        var sender = new NativeFaceTrackingSender(new TimeLogger<NativeFaceTrackingSender>(), sendService, oscQuery);

        using var cts = new CancellationTokenSource();
        await sender.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);
        await Task.Delay(300); // the loop runs with no module data yet: it must stay silent

        Clock.Start();
        var module = new Thread(SimulateModules) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        module.Start();
        module.Join();

        cts.Cancel();
        await loop.StopAsync(CancellationToken.None);
        await sender.StopAsync(CancellationToken.None);
        await Task.Delay(100);
        receiver.Close();
        rx.Join(1000);

        Analyse(c);
    }

    /// <summary>
    /// Stands in for the module processes and the IPC receive thread: every 10 ms (the host's
    /// update poll) a module answers with its current values, decoded and applied exactly as
    /// UnifiedLibManager.HandleReplyUpdate does.
    /// </summary>
    private static void SimulateModules()
    {
        var a = Ipc.Module("ModuleA");
        var b = Ipc.Module("ModuleB");
        var frameA = Ipc.StartupFrame();
        var frameB = Ipc.StartupFrame();
        long lastStep = -1;
        var lostMarked = false;
        var nextPoll = 0.0;
        string? phase = null;

        while (true)
        {
            var ms = Clock.Ms();
            if (ms >= End)
            {
                Clock.Note($"{ms,8:F1} ms  end");
                return;
            }

            var name = ms < LiveUntil ? "module A tracking"
                : ms < StallUntil ? "A's tracker stalls: A repeats its last values, its head pose keeps moving"
                : ms < LiveAgainUntil ? "A tracking again"
                : ms < StillUntil ? "A's face holds still: floats change by one ulp, bytes don't"
                : ms < RespawnAt ? "A's process died: no answers"
                : ms < RespawnLiveAt ? "module B respawned: answers with its start-up values"
                : "B tracking";
            if (name != phase)
            {
                phase = name;
                Clock.Note($"{ms,8:F1} ms  {name}");
            }

            if (!lostMarked && ms >= LostAt)
            {
                // What HandleDeadModule does once the dead process held the last slot
                TrackingFreshness.MarkLost();
                lostMarked = true;
                Clock.Note($"{ms,8:F1} ms  supervisor finds A dead: TrackingFreshness.MarkLost()");
            }

            if (ms < nextPoll)
            {
                Thread.Sleep(1);
                continue;
            }

            nextPoll += 10;

            // The tracker behind a module delivers 72 frames a second; polls in between repeat
            var step = (long)(ms * 72 / 1000);
            var newStep = step != lastStep;
            lastStep = step;

            if (ms < LiveUntil || ms is >= StallUntil and < LiveAgainUntil)
            {
                if (newStep)
                {
                    Track(frameA, step);
                }

                UnifiedLibManager.ApplyTrackingFrame(a, Ipc.Decode(frameA));
            }
            else if (ms < StallUntil)
            {
                frameA[Ipc.HeadYaw] = (float)Math.Sin(ms / 300.0);
                UnifiedLibManager.ApplyTrackingFrame(a, Ipc.Decode(frameA));
            }
            else if (ms < StillUntil)
            {
                if (newStep)
                {
                    frameA[Ipc.Shape(UnifiedExpressions.JawOpen)] = step % 2 == 0 ? 0.31f : MathF.BitIncrement(0.31f);
                }

                UnifiedLibManager.ApplyTrackingFrame(a, Ipc.Decode(frameA));
            }
            else if (ms < RespawnAt)
            {
                // Module A's process is gone: no answers at all
            }
            else if (ms < RespawnLiveAt)
            {
                UnifiedLibManager.ApplyTrackingFrame(b, Ipc.Decode(frameB));
            }
            else
            {
                if (newStep)
                {
                    Track(frameB, step);
                }

                UnifiedLibManager.ApplyTrackingFrame(b, Ipc.Decode(frameB));
            }
        }
    }

    /// <summary>A moving face: gaze sweeps, the eyes blink, the jaw and smile move.</summary>
    private static void Track(float[] f, long step)
    {
        var t = step / 72.0;
        var gazeX = (float)(0.35 * Math.Sin(2 * Math.PI * 0.4 * t));
        var gazeY = (float)(0.2 * Math.Cos(2 * Math.PI * 0.3 * t));
        var open = (float)(0.5 + 0.5 * Math.Cos(2 * Math.PI * 0.7 * t));
        f[Ipc.LeftGazeX] = f[Ipc.RightGazeX] = gazeX;
        f[Ipc.LeftGazeY] = f[Ipc.RightGazeY] = gazeY;
        f[Ipc.LeftOpenness] = f[Ipc.RightOpenness] = open;
        f[Ipc.LeftPupil] = f[Ipc.RightPupil] = (float)(4 + 1.5 * Math.Sin(2 * Math.PI * 0.2 * t));
        f[Ipc.Shape(UnifiedExpressions.JawOpen)] = (float)(0.35 + 0.3 * Math.Sin(2 * Math.PI * 0.5 * t));
        f[Ipc.Shape(UnifiedExpressions.MouthCornerPullLeft)] = (float)(0.5 + 0.4 * Math.Sin(2 * Math.PI * 0.25 * t));
        f[Ipc.Shape(UnifiedExpressions.MouthCornerPullRight)] = (float)(0.5 + 0.4 * Math.Sin(2 * Math.PI * 0.25 * t));
    }

    private static void Analyse(Checks c)
    {
        var all = Received.Select(r => (Ms: Clock.Ms(r.Ts), r.Data)).OrderBy(r => r.Ms).ToList();
        var blobs = all.Where(r => IsBlob(r.Data)).ToList();
        var neutrals = blobs.Where(r => IsNeutral(r.Data)).ToList();
        double FirstAfter(double ms) => blobs.Where(r => r.Ms >= ms).Select(r => r.Ms).DefaultIfEmpty(double.NaN).First();
        int CountIn(double from, double to) => blobs.Count(r => r.Ms >= from && r.Ms < to);

        Console.WriteLine();
        Console.WriteLine("  Timeline of what reached the socket (runs split at gaps over 40 ms):");
        PrintRuns(blobs);
        Console.WriteLine("  Events:");
        foreach (var note in Clock.Notes)
        {
            Console.WriteLine("    " + note);
        }

        Console.WriteLine();
        foreach (var other in all.Where(r => !IsBlob(r.Data)).GroupBy(r => Describe(r.Data)))
        {
            Console.WriteLine($"  not a blob: {other.Key} x{other.Count()}, first at {other.First().Ms:F1} ms");
        }

        c.That(all.Count == blobs.Count && blobs.Count > 0,
            $"every datagram is a {FaceTrackingBlob.DatagramLength}-byte {FaceTrackingBlob.Address} blob with a {FaceTrackingBlob.PayloadLength}-byte payload ({blobs.Count} of {all.Count})");
        c.That(blobs.All(r => r.Ms >= 0), "nothing was sent before any module delivered data");

        var live = CountIn(0, LiveUntil);
        c.That(live >= 120 && !neutrals.Any(r => r.Ms < LiveUntil), $"live: {live} blobs in the first {LiveUntil:F0} ms, none neutral");

        var stallNeutrals = neutrals.Where(r => r.Ms >= LiveUntil && r.Ms < StallUntil).ToList();
        var stallNeutralMs = stallNeutrals.Count > 0 ? stallNeutrals[0].Ms : StallUntil;
        var lastBeforeNeutral = blobs.Where(r => r.Ms < stallNeutralMs && !IsNeutral(r.Data))
            .Select(r => r.Ms).DefaultIfEmpty(double.NaN).Last();
        c.That(stallNeutrals.Count == 1 && stallNeutrals[0].Ms >= LiveUntil + 450 && stallNeutrals[0].Ms <= LiveUntil + 650,
            $"stall: exactly one neutral blob, {(stallNeutrals.Count > 0 ? stallNeutrals[0].Ms - LiveUntil : double.NaN):F0} ms after the tracker stalled at {LiveUntil:F0} ms");
        c.That(lastBeforeNeutral >= LiveUntil + 400,
            $"stall: the frozen face kept going out until the timeout (last live blob at {lastBeforeNeutral:F0} ms)");
        var silentStall = stallNeutrals.Count == 1 ? CountIn(stallNeutrals[0].Ms + 0.001, StallUntil) : -1;
        c.That(silentStall == 0,
            $"stall: silent from the neutral blob until {StallUntil:F0} ms ({silentStall} blobs), though the module kept answering and its head pose kept moving");

        var resume = FirstAfter(StallUntil);
        c.That(resume - StallUntil is >= 0 and <= 60 && !IsNeutral(blobs.First(r => r.Ms >= StallUntil).Data),
            $"resume: first live blob {resume - StallUntil:F0} ms after tracking returned at {StallUntil:F0} ms");

        var still = blobs.Where(r => r.Ms >= StallUntil + 100 && r.Ms < StillUntil).ToList();
        var maxGap = still.Zip(still.Skip(1), (x, y) => y.Ms - x.Ms).DefaultIfEmpty(0).Max();
        var stillPayloads = blobs.Where(r => r.Ms >= LiveAgainUntil + 50 && r.Ms < StillUntil - 50)
            .Select(r => Convert.ToHexString(r.Data, FaceTrackingBlob.PayloadOffset, FaceTrackingBlob.PayloadLength)).Distinct().Count();
        c.That(!neutrals.Any(r => r.Ms >= StallUntil && r.Ms < LostAt) && maxGap < 40,
            $"still face: no neutral blob and no gap over 40 ms from {StallUntil + 100:F0} to {StillUntil:F0} ms (longest {maxGap:F1} ms)");
        c.That(stillPayloads == 1,
            $"still face: every payload from {LiveAgainUntil + 50:F0} to {StillUntil - 50:F0} ms is byte-identical ({stillPayloads} distinct), yet it counts as live");

        var lostNeutrals = neutrals.Where(r => r.Ms >= StillUntil && r.Ms < RespawnLiveAt).ToList();
        c.That(CountIn(StillUntil, LostAt) >= 10,
            $"module died at {StillUntil:F0} ms: its last values kept going out until the supervisor noticed ({CountIn(StillUntil, LostAt)} blobs)");
        c.That(lostNeutrals.Count == 1 && lostNeutrals[0].Ms >= LostAt && lostNeutrals[0].Ms <= LostAt + 40,
            $"module lost: one neutral blob {(lostNeutrals.Count > 0 ? lostNeutrals[0].Ms - LostAt : double.NaN):F0} ms after MarkLost, sooner than the {StillUntil + 500:F0} ms timeout");
        var silentLost = lostNeutrals.Count == 1 ? CountIn(lostNeutrals[0].Ms + 0.001, RespawnLiveAt) : -1;
        c.That(silentLost == 0,
            $"module lost: silent until {RespawnLiveAt:F0} ms ({silentLost} blobs), through the respawned module's start-up values from {RespawnAt:F0} ms");

        var resume2 = FirstAfter(RespawnLiveAt);
        c.That(resume2 - RespawnLiveAt is >= 0 and <= 60 && !IsNeutral(blobs.First(r => r.Ms >= RespawnLiveAt).Data),
            $"respawned module: first live blob {resume2 - RespawnLiveAt:F0} ms after it started tracking");
        c.That(neutrals.Count == 2, $"two neutral blobs in the whole run ({neutrals.Count})");

        var paused = Clock.LogLines.Count(l => l.Contains("Native face tracking paused"));
        var resumed = Clock.LogLines.Count(l => l.Contains("Native face tracking resumed"));
        c.That(paused == 2 && resumed == 2, $"the log says paused twice and resumed twice ({paused}, {resumed})");
    }

    private static void PrintRuns(List<(double Ms, byte[] Data)> blobs)
    {
        var i = 0;
        double? previousEnd = null;
        while (i < blobs.Count)
        {
            var neutral = IsNeutral(blobs[i].Data);
            var j = i;
            while (j + 1 < blobs.Count && blobs[j + 1].Ms - blobs[j].Ms <= 40 && IsNeutral(blobs[j + 1].Data) == neutral)
            {
                j++;
            }

            if (previousEnd is { } end && blobs[i].Ms - end > 40)
            {
                Console.WriteLine($"    {end,8:F1} - {blobs[i].Ms,8:F1} ms  silent  ({blobs[i].Ms - end:F0} ms)");
            }

            var count = j - i + 1;
            var span = blobs[j].Ms - blobs[i].Ms;
            Console.WriteLine(neutral
                ? $"    {blobs[i].Ms,8:F1}             neutral blob x{count} (payload all zero)"
                : $"    {blobs[i].Ms,8:F1} - {blobs[j].Ms,8:F1} ms  live    {count} blobs ({(span > 0 ? (count - 1) * 1000 / span : 0):F0}/s)");
            previousEnd = blobs[j].Ms;
            i = j + 1;
        }
    }

    private static bool IsBlob(byte[] d) =>
        d.Length == FaceTrackingBlob.DatagramLength &&
        Encoding.ASCII.GetString(d, 0, FaceTrackingBlob.Address.Length) == FaceTrackingBlob.Address &&
        d[20] == ',' && d[21] == 'b' &&
        BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(24)) == FaceTrackingBlob.PayloadLength;

    private static string Describe(byte[] d)
    {
        var end = Array.IndexOf(d, (byte)0);
        var address = Encoding.ASCII.GetString(d, 0, end < 0 ? Math.Min(d.Length, 40) : end);
        return $"{d.Length} bytes, '{address}'";
    }

    private static bool IsNeutral(byte[] d) =>
        d.AsSpan(FaceTrackingBlob.PayloadOffset, FaceTrackingBlob.PayloadLength).IndexOfAnyExcept((byte)0) < 0;
}

static class Clock
{
    private static long _t0 = Stopwatch.GetTimestamp();
    public static readonly List<string> LogLines = new();
    public static readonly List<string> Notes = new();

    public static void Start() => _t0 = Stopwatch.GetTimestamp();
    public static double Ms() => Ms(Stopwatch.GetTimestamp());
    public static double Ms(long ts) => (ts - _t0) * 1000.0 / Stopwatch.Frequency;

    public static void Note(string note)
    {
        lock (Notes)
        {
            Notes.Add(note);
        }
    }
}

sealed class TimeLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var line = $"[{Clock.Ms(),8:F1} ms] {typeof(T).Name}: {formatter(state, exception)}";
        lock (Clock.LogLines)
        {
            Clock.LogLines.Add(line);
        }

        Console.WriteLine("  log " + line);
    }
}

sealed class LoopbackTarget(int port) : IOscTarget
{
    // The port never changes during a run, so nobody needs to hear about it
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public bool IsConnected { get; set; }
    public int InPort { get; set; }
    public int OutPort { get; set; } = port;
    public string DestinationAddress { get; set; } = "127.0.0.1";
}

sealed class NativeAvatar : IAvatarInfo
{
    public string Name => "Stall check";
    public string Id => "avtr_stall_check";
    public bool FullFaceTracking => true;
    public IParameterDefinition[] Parameters => Array.Empty<IParameterDefinition>();
}
