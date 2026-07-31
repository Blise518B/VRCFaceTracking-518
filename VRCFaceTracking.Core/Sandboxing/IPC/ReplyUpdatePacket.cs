using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Expressions;

namespace VRCFaceTracking.Core.Sandboxing.IPC;
public class ReplyUpdatePacket : IpcPacket
{
    const int EXPRESSION_COUNT = (int)UnifiedExpressions.Max + 1;
    const float INVALID_FLOAT = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    internal class UpdateDataContiguous
    {
        internal float Eye_MaxDilation;
        internal float Eye_MinDilation;

        internal float Eye_Left_GazeX;
        internal float Eye_Left_GazeY;
        internal float Eye_Left_PupilDiameter_MM;
        internal float Eye_Left_Openness;

        internal float Eye_Right_GazeX;
        internal float Eye_Right_GazeY;
        internal float Eye_Right_PupilDiameter_MM;
        internal float Eye_Right_Openness;

        internal float Head_Yaw;
        internal float Head_Pitch;
        internal float Head_Roll;

        internal float Head_PosX;
        internal float Head_PosY;
        internal float Head_PosZ;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = EXPRESSION_COUNT)]
        internal float[] Expression_Shapes;
    }

    private UpdateDataContiguous _contiguousUnifiedData = new ()
    {
        Expression_Shapes = new float[EXPRESSION_COUNT]
    };

    public override PacketType GetPacketType() => PacketType.ReplyUpdate;

    // Wire layout (unchanged from the Marshal-based version, all little-endian floats):
    // [4B magic][4B type][4B struct size][16 header floats][EXPRESSION_COUNT shape floats].
    // Header float order matches UpdateDataContiguous's field order exactly.
    private const int HEADER_FLOATS = 16;
    private const int STRUCT_SIZE = (HEADER_FLOATS + EXPRESSION_COUNT) * sizeof(float);

    // Staging buffer reused between calls: this packet is built/decoded 100 times a
    // second per module, and the previous implementation paid AllocHGlobal +
    // Marshal.StructureToPtr/PtrToStructure (plus an intermediate byte[]) per packet
    // on BOTH sides of the IPC link. A float[]->byte[] block copy is the same bytes
    // with none of that.
    private readonly float[] _staging = new float[HEADER_FLOATS + EXPRESSION_COUNT];

    public override byte[] GetBytes()
    {
        var f = _staging;
        f[0]  = UnifiedTracking.Data.Eye._maxDilation;
        f[1]  = UnifiedTracking.Data.Eye._minDilation;

        f[2]  = UnifiedTracking.Data.Eye.Left.Gaze.x;
        f[3]  = UnifiedTracking.Data.Eye.Left.Gaze.y;
        f[4]  = UnifiedTracking.Data.Eye.Left.PupilDiameter_MM;
        f[5]  = UnifiedTracking.Data.Eye.Left.Openness;

        f[6]  = UnifiedTracking.Data.Eye.Right.Gaze.x;
        f[7]  = UnifiedTracking.Data.Eye.Right.Gaze.y;
        f[8]  = UnifiedTracking.Data.Eye.Right.PupilDiameter_MM;
        f[9]  = UnifiedTracking.Data.Eye.Right.Openness;

        f[10] = UnifiedTracking.Data.Head.HeadYaw;
        f[11] = UnifiedTracking.Data.Head.HeadPitch;
        f[12] = UnifiedTracking.Data.Head.HeadRoll;

        f[13] = UnifiedTracking.Data.Head.HeadPosX;
        f[14] = UnifiedTracking.Data.Head.HeadPosY;
        f[15] = UnifiedTracking.Data.Head.HeadPosZ;

        for ( int i = 0; i < EXPRESSION_COUNT; i++ )
        {
            f[HEADER_FLOATS + i] = UnifiedTracking.Data.Shapes[i].Weight;
        }

        byte[] finalDataStream = new byte[SIZE_PACKET_MAGIC + SIZE_PACKET_TYPE + sizeof(int) + STRUCT_SIZE];
        Buffer.BlockCopy(HANDSHAKE_MAGIC, 0, finalDataStream, 0, SIZE_PACKET_MAGIC);
        BitConverter.TryWriteBytes(finalDataStream.AsSpan(4), (uint)GetPacketType());
        BitConverter.TryWriteBytes(finalDataStream.AsSpan(8), STRUCT_SIZE);
        Buffer.BlockCopy(f, 0, finalDataStream, 12, STRUCT_SIZE);

        return finalDataStream;
    }

    public override void Decode(in byte[] data)
    {
        int structSize = BitConverter.ToInt32(data, 8);
        var bytes = Math.Min(structSize, STRUCT_SIZE);
        if ( bytes < HEADER_FLOATS * sizeof(float) || data.Length < 12 + bytes )
        {
            return; // Truncated packet; keep the previous frame rather than reading garbage
        }

        var f = _staging;
        Buffer.BlockCopy(data, 12, f, 0, bytes);

        var d = _contiguousUnifiedData;
        d.Eye_MaxDilation           = f[0];
        d.Eye_MinDilation           = f[1];
        d.Eye_Left_GazeX            = f[2];
        d.Eye_Left_GazeY            = f[3];
        d.Eye_Left_PupilDiameter_MM = f[4];
        d.Eye_Left_Openness         = f[5];
        d.Eye_Right_GazeX           = f[6];
        d.Eye_Right_GazeY           = f[7];
        d.Eye_Right_PupilDiameter_MM = f[8];
        d.Eye_Right_Openness        = f[9];
        d.Head_Yaw                  = f[10];
        d.Head_Pitch                = f[11];
        d.Head_Roll                 = f[12];
        d.Head_PosX                 = f[13];
        d.Head_PosY                 = f[14];
        d.Head_PosZ                 = f[15];

        var shapeFloats = bytes / sizeof(float) - HEADER_FLOATS;
        for ( int i = 0; i < shapeFloats && i < EXPRESSION_COUNT; i++ )
        {
            d.Expression_Shapes[i] = f[HEADER_FLOATS + i];
        }
    }

    public void UpdateGlobalEyeState()
    {
        // If the eye state is valid

        // If dilation parameters are invalid
        if ( _contiguousUnifiedData.Eye_MaxDilation != INVALID_FLOAT &&
            _contiguousUnifiedData.Eye_MinDilation != INVALID_FLOAT &&
            _contiguousUnifiedData.Eye_MaxDilation < _contiguousUnifiedData.Eye_MinDilation )
        {
            return;
        }

        // Update the unified tracking to match our data structure
        if ( _contiguousUnifiedData.Eye_Left_GazeX != INVALID_FLOAT )
            UnifiedTracking.Data.Eye.Left.Gaze.x = _contiguousUnifiedData.Eye_Left_GazeX;
        if ( _contiguousUnifiedData.Eye_Left_GazeY != INVALID_FLOAT )
            UnifiedTracking.Data.Eye.Left.Gaze.y = _contiguousUnifiedData.Eye_Left_GazeY;
        if ( _contiguousUnifiedData.Eye_Left_PupilDiameter_MM != INVALID_FLOAT )
            UnifiedTracking.Data.Eye.Left.PupilDiameter_MM = _contiguousUnifiedData.Eye_Left_PupilDiameter_MM;
        if ( _contiguousUnifiedData.Eye_Left_Openness != INVALID_FLOAT )
            UnifiedTracking.Data.Eye.Left.Openness = _contiguousUnifiedData.Eye_Left_Openness;

        if ( _contiguousUnifiedData.Eye_Right_GazeX != INVALID_FLOAT )
            UnifiedTracking.Data.Eye.Right.Gaze.x = _contiguousUnifiedData.Eye_Right_GazeX;
        if ( _contiguousUnifiedData.Eye_Right_GazeY != INVALID_FLOAT )
            UnifiedTracking.Data.Eye.Right.Gaze.y = _contiguousUnifiedData.Eye_Right_GazeY;
        if ( _contiguousUnifiedData.Eye_Right_PupilDiameter_MM != INVALID_FLOAT )
            UnifiedTracking.Data.Eye.Right.PupilDiameter_MM = _contiguousUnifiedData.Eye_Right_PupilDiameter_MM;
        if ( _contiguousUnifiedData.Eye_Right_Openness != INVALID_FLOAT )
            UnifiedTracking.Data.Eye.Right.Openness = _contiguousUnifiedData.Eye_Right_Openness;

        if ( _contiguousUnifiedData.Eye_MaxDilation != INVALID_FLOAT )
            UnifiedTracking.Data.Eye._maxDilation = _contiguousUnifiedData.Eye_MaxDilation;
        if ( _contiguousUnifiedData.Eye_MinDilation != INVALID_FLOAT )
            UnifiedTracking.Data.Eye._minDilation = _contiguousUnifiedData.Eye_MinDilation;

        // Eye-derived expression shapes
        for ( int i = (int)UnifiedExpressions.EyeSquintRight; i <= (int)UnifiedExpressions.BrowOuterUpLeft; i++ )
        {
            if ( _contiguousUnifiedData.Expression_Shapes[i] != INVALID_FLOAT )
                UnifiedTracking.Data.Shapes[i].Weight = _contiguousUnifiedData.Expression_Shapes[i];
        }
    }

    public void UpdateHeadState()
    {
        if ( _contiguousUnifiedData.Head_Yaw != INVALID_FLOAT )
            UnifiedTracking.Data.Head.HeadYaw = _contiguousUnifiedData.Head_Yaw;
        if ( _contiguousUnifiedData.Head_Pitch != INVALID_FLOAT )
            UnifiedTracking.Data.Head.HeadPitch = _contiguousUnifiedData.Head_Pitch;
        if ( _contiguousUnifiedData.Head_Roll != INVALID_FLOAT )
            UnifiedTracking.Data.Head.HeadRoll = _contiguousUnifiedData.Head_Roll;
            
        if ( _contiguousUnifiedData.Head_PosX != INVALID_FLOAT )
            UnifiedTracking.Data.Head.HeadPosX = _contiguousUnifiedData.Head_PosX;
        if ( _contiguousUnifiedData.Head_PosY != INVALID_FLOAT )
            UnifiedTracking.Data.Head.HeadPosY = _contiguousUnifiedData.Head_PosY;
        if ( _contiguousUnifiedData.Head_PosZ != INVALID_FLOAT )
            UnifiedTracking.Data.Head.HeadPosZ = _contiguousUnifiedData.Head_PosZ;
    }

    public void UpdateGlobalExpressionState()
    {
        // Copy face tracking
        for ( int i = (int)UnifiedExpressions.BrowOuterUpLeft; i < _contiguousUnifiedData.Expression_Shapes.Length; i++ )
        {
            if ( _contiguousUnifiedData.Expression_Shapes[i] != INVALID_FLOAT)
                UnifiedTracking.Data.Shapes[i].Weight = _contiguousUnifiedData.Expression_Shapes[i];
        }
    }
}
