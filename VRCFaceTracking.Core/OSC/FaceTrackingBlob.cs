using System.Buffers.Binary;
using System.Text;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Expressions;

namespace VRCFaceTracking.Core.OSC;

/// <summary>
/// The input of VRChat's native face tracking (open beta, Avatars SDK 3.10.6+): one OSC
/// message to /tracking/face/v1 whose only argument is a blob of exactly 102 bytes, one
/// unsigned byte per Unified Expressions key, read by VRChat as byte / 255. VRChat ignores
/// a message with any other blob size, and the key order is fixed by VRChat
/// (https://vrc-beta-docs.netlify.app/avatars/face-tracking/osc/): bytes 0-13 are the eye
/// keys below, bytes 14-101 are <see cref="UnifiedExpressions"/> in enum order.
///
/// The datagram is built here rather than by fti_osc. Our fti_osc has no blob type, and
/// upstream's rebuilt one changes the OscValue layout under every message we send and
/// receive. The header is written once; each frame only rewrites the payload in place.
/// </summary>
public sealed class FaceTrackingBlob
{
    public const string Address = "/tracking/face/v1";
    public const int PayloadLength = 102;

    private const int EyeKeyCount = 14;
    private const int ShapeCount = PayloadLength - EyeKeyCount;

    // OSC pads every field to 4 bytes: the address (17 chars + NUL) takes 20, the type tag
    // ",b" 4, then the blob's big-endian int32 size, then the payload padded to 104.
    private const int TypeTagOffset = 20;
    private const int SizeOffset = TypeTagOffset + 4;
    public const int PayloadOffset = SizeOffset + 4;
    public const int DatagramLength = PayloadOffset + (PayloadLength + 3) / 4 * 4;

    /// <summary>The complete OSC message, ready for a UDP send.</summary>
    public byte[] Datagram { get; } = new byte[DatagramLength];

    public FaceTrackingBlob()
    {
        Encoding.ASCII.GetBytes(Address, Datagram);
        Datagram[TypeTagOffset] = (byte)',';
        Datagram[TypeTagOffset + 1] = (byte)'b';
        BinaryPrimitives.WriteInt32BigEndian(Datagram.AsSpan(SizeOffset), PayloadLength);
    }

    public Span<byte> Payload => Datagram.AsSpan(PayloadOffset, PayloadLength);

    /// <summary>Rewrites the payload from <paramref name="data"/>. Allocation-free.</summary>
    public void Write(UnifiedTrackingData data) => Encode(data, Payload);

    public static void Encode(UnifiedTrackingData data, Span<byte> payload)
    {
        var right = data.Eye.Right;
        var left = data.Eye.Left;

        // Gaze is tan(angle) per axis, +x looking right, +y looking up, so "out" is +x for
        // the right eye and -x for the left one. Same mapping as upstream.
        payload[0] = ToByte(right.Gaze.x);    // EyeLookOutRight
        payload[1] = ToByte(-right.Gaze.x);   // EyeLookInRight
        payload[2] = ToByte(right.Gaze.y);    // EyeLookUpRight
        payload[3] = ToByte(-right.Gaze.y);   // EyeLookDownRight
        payload[4] = ToByte(-left.Gaze.x);    // EyeLookOutLeft
        payload[5] = ToByte(left.Gaze.x);     // EyeLookInLeft
        payload[6] = ToByte(left.Gaze.y);     // EyeLookUpLeft
        payload[7] = ToByte(-left.Gaze.y);    // EyeLookDownLeft
        payload[8] = ToByte(1f - right.Openness);   // EyeClosedRight
        payload[9] = ToByte(1f - left.Openness);    // EyeClosedLeft

        // Pupils: dilation and constriction are opposing weights around 5 mm, the middle of
        // the 0-10 mm scale modules report on (upstream PR #395)
        var rightPupil = PupilWeight(right.PupilDiameter_MM);
        var leftPupil = PupilWeight(left.PupilDiameter_MM);
        payload[10] = ToByte(rightPupil);     // EyeDilationRight
        payload[11] = ToByte(leftPupil);      // EyeDilationLeft
        payload[12] = ToByte(-rightPupil);    // EyeConstrictRight
        payload[13] = ToByte(-leftPupil);     // EyeConstrictLeft

        var shapes = data.Shapes;
        var count = Math.Min(ShapeCount, Math.Min((int)UnifiedExpressions.Max, shapes.Length));
        for (var i = 0; i < count; i++)
        {
            payload[EyeKeyCount + i] = ToByte(shapes[i].Weight);
        }

        payload[(EyeKeyCount + count)..].Clear();
    }

    // 0 means no pupil tracking, and an invalid sample must go back to neutral too
    private static float PupilWeight(float diameterMm) =>
        float.IsFinite(diameterMm) && diameterMm > 0f ? Math.Clamp(diameterMm / 5f - 1f, -1f, 1f) : 0f;

    // Rounded rather than truncated, so 1.0 really is 255; NaN fails the first test and is 0
    private static byte ToByte(float value) =>
        value > 0f ? value < 1f ? (byte)(value * 255f + 0.5f) : (byte)255 : (byte)0;
}
