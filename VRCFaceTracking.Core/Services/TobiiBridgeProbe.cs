using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace VRCFaceTracking.Core.Services;

/// <summary>Which app is serving the Tobii tracker's TCP API on 127.0.0.1:5555.</summary>
public enum TobiiBridgeKind
{
    None,
    BrokenEye,
    Sranibro,
}

/// <summary>
/// Looks at the BrokenEye-framed TCP API on 127.0.0.1:5555 and tells the two bridges apart.
/// </summary>
/// <remarks>
/// <para>Both BrokenEye and SRanibro serve the same framing (<c>[1B id][u32 LE length][JSON]</c>
/// after a 0x00 subscribe byte), but they behave differently, and the difference matters:</para>
/// <list type="bullet">
/// <item>BrokenEye only streams once its Tobii device is found, so any frame means the
/// headset's tracker is there.</item>
/// <item>SRanibro writes a frame at 120 Hz <i>always</i>, all-invalid when it has no tracker.
/// Its frames alone prove nothing; a frame with a <c>*_is_valid: true</c> field does.</item>
/// </list>
/// <para>SRanibro is recognised by its top-level <c>noise_filter_samples</c> /
/// <c>sranipal_brow_link</c> fields or a two-value <c>gaze_direction</c> (BrokenEye sends three).</para>
/// <para>Everything here is a short, read-only loopback query. Never call it on the tracking
/// thread: it blocks for up to <c>windowMs</c>.</para>
/// </remarks>
public static class TobiiBridgeProbe
{
    public const int ApiPort = 5555;
    public const int CameraPort = 5556;

    public readonly record struct Sample(TobiiBridgeKind Kind, bool Connected, int Frames, bool AnyValid);

    /// <summary>What SRanibro's eye-camera snapshot endpoint answered.</summary>
    public enum CameraState
    {
        /// <summary>Nothing listening (SRanibro closed, or its eye image output is off).</summary>
        Unreachable,
        /// <summary>A real eye image came back: the camera delivers frames.</summary>
        Frames,
        /// <summary>503 "No eye frame available yet": SRanibro runs but has no camera.</summary>
        NoFrame,
        /// <summary>503 "Too many eye-image clients": the camera apps hold every slot, so it is streaming.</summary>
        Busy,
        /// <summary>Anything else.</summary>
        Unknown,
    }

    /// <summary>
    /// Connects to the API, subscribes, and reads frames for up to <paramref name="windowMs"/>.
    /// Returns early as soon as the answer is clear (a BrokenEye frame, or a valid SRanibro frame).
    /// </summary>
    public static Sample Probe(int windowMs = 400)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            // Cancel and wait for the connect, never just walk away from it. Windows takes
            // about 2 s to refuse a loopback connect, so with no bridge running this always
            // timed out - and BeginConnect + WaitOne then disposed the socket with the
            // connect still pending. That connect faulted with nobody observing it: the
            // "[App] Ignored an abandoned socket task from connection teardown" line once
            // per sweep, ~6 a minute all session.
            using (var timeout = new CancellationTokenSource(250))
            {
                try
                {
                    socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, ApiPort), timeout.Token)
                        .AsTask().GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    return new Sample(TobiiBridgeKind.None, false, 0, false);
                }
            }

            socket.NoDelay = true;
            socket.Send(new byte[] { 0x00 });

            var deadline = Environment.TickCount64 + windowMs;
            var buffer = new byte[16384];
            var pending = new List<byte>(16384);
            var kind = TobiiBridgeKind.None;
            var frames = 0;
            var anyValid = false;
            var anyBytes = false;

            while (true)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    break;
                }

                socket.ReceiveTimeout = (int)Math.Max(20, remaining);
                int read;
                try
                {
                    read = socket.Receive(buffer);
                }
                catch (SocketException)
                {
                    break; // window over without (more) data
                }

                if (read <= 0)
                {
                    break; // closed by the bridge
                }

                anyBytes = true;
                for (var i = 0; i < read; i++)
                {
                    pending.Add(buffer[i]);
                }

                // Peel off every complete frame in the buffer.
                while (pending.Count >= 5)
                {
                    var length = BitConverter.ToInt32(new[] { pending[1], pending[2], pending[3], pending[4] }, 0);
                    if (length < 0 || length > 1_000_000)
                    {
                        // Not a framing we understand. Bytes still mean "something streams".
                        return new Sample(kind == TobiiBridgeKind.None ? TobiiBridgeKind.BrokenEye : kind, true,
                            frames, anyValid);
                    }

                    if (pending.Count < 5 + length)
                    {
                        break;
                    }

                    var payload = pending.GetRange(5, length).ToArray();
                    pending.RemoveRange(0, 5 + length);
                    frames++;

                    var (isSranibro, valid) = Inspect(payload);
                    kind = isSranibro ? TobiiBridgeKind.Sranibro : TobiiBridgeKind.BrokenEye;
                    anyValid |= valid;

                    // BrokenEye: any frame is the answer. SRanibro: a valid one is.
                    if (kind == TobiiBridgeKind.BrokenEye || anyValid)
                    {
                        return new Sample(kind, true, frames, anyValid);
                    }
                }
            }

            if (frames == 0 && anyBytes)
            {
                // A partial frame: the old probe's rule ("any bytes = streaming") still holds
                // for BrokenEye; SRanibro frames are small and would have completed.
                kind = TobiiBridgeKind.BrokenEye;
            }

            return new Sample(kind, true, frames, anyValid);
        }
        catch (Exception)
        {
            return new Sample(TobiiBridgeKind.None, false, 0, false);
        }
    }

    /// <summary>Is this payload SRanibro's, and does any eye carry a valid value?</summary>
    internal static (bool IsSranibro, bool AnyValid) Inspect(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (false, false);
            }

            var isSranibro = root.TryGetProperty("noise_filter_samples", out _)
                             || root.TryGetProperty("sranipal_brow_link", out _);
            var anyValid = false;

            foreach (var side in new[] { "left", "right" })
            {
                if (!root.TryGetProperty(side, out var eye) || eye.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (eye.TryGetProperty("gaze_direction", out var gaze)
                    && gaze.ValueKind == JsonValueKind.Array
                    && gaze.GetArrayLength() == 2)
                {
                    isSranibro = true;
                }

                foreach (var property in eye.EnumerateObject())
                {
                    if (property.Name.EndsWith("_is_valid", StringComparison.Ordinal)
                        && property.Value.ValueKind == JsonValueKind.True)
                    {
                        anyValid = true;
                    }
                }
            }

            return (isSranibro, anyValid);
        }
        catch (Exception)
        {
            return (false, false);
        }
    }

    private static readonly HttpClient CameraClient = new() { Timeout = TimeSpan.FromMilliseconds(1500) };

    /// <summary>
    /// Asks SRanibro's eye-image server for one left-eye snapshot. Separates "SRanibro runs but
    /// has no tracker" (503 no frame) from "the tracker is there, the headset just isn't worn"
    /// (an image comes back) - the 5555 frames are all-invalid in both cases.
    /// </summary>
    public static CameraState ProbeSranibroCamera()
    {
        try
        {
            using var response = CameraClient
                .GetAsync($"http://127.0.0.1:{CameraPort}/left.jpg", HttpCompletionOption.ResponseContentRead)
                .GetAwaiter().GetResult();
            var body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();

            if (response.StatusCode == HttpStatusCode.OK)
            {
                return body.Length > 0 ? CameraState.Frames : CameraState.Unknown;
            }

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                var text = System.Text.Encoding.UTF8.GetString(body);
                if (text.Contains("Too many", StringComparison.OrdinalIgnoreCase))
                {
                    return CameraState.Busy;
                }
                if (text.Contains("No eye frame", StringComparison.OrdinalIgnoreCase))
                {
                    return CameraState.NoFrame;
                }
            }

            return CameraState.Unknown;
        }
        catch (Exception)
        {
            return CameraState.Unreachable;
        }
    }
}
