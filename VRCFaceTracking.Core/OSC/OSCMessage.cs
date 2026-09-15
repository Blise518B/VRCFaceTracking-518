using System.Reflection;
using System.Runtime.InteropServices;
using VRCFaceTracking.OSC;

namespace VRCFaceTracking.Core.OSC;

public class OscMessage
{
    public OscMessageMeta _meta;
    private IntPtr _metaPtr;

    public string Address
    {
        get => _meta.Address;
        set => _meta.Address = value;
    }
    
    private readonly Action<object> _valueSetter;

    public object Value
    {
        get
        {
            if (_meta.ValueLength == 0)
            {
                return null;
            }
            
            var values = new OscValue[_meta.ValueLength];
            var ptr = _meta.Value;
            for (var i = 0; i < _meta.ValueLength; i++)
            {
                values[i] = Marshal.PtrToStructure<OscValue>(ptr);
                ptr += Marshal.SizeOf<OscValue>();
            }

            return values[0].Value;
        }
        set => _valueSetter(value);
    }

    private static readonly int TypeOffset = Marshal.OffsetOf<OscValue>(nameof(OscValue.Type)).ToInt32();
    private static readonly int IntOffset = Marshal.OffsetOf<OscValue>(nameof(OscValue.IntValue)).ToInt32();
    private static readonly int FloatOffset = Marshal.OffsetOf<OscValue>(nameof(OscValue.FloatValue)).ToInt32();
    private static readonly int BoolOffset = Marshal.OffsetOf<OscValue>(nameof(OscValue.BoolValue)).ToInt32();

    /// <summary>
    /// The first value as an int, float or bool, read straight out of native memory.
    /// Unlike <see cref="Value"/> this never marshals the string field - a pointer it would
    /// have to follow - and allocates nothing beyond the box. For diagnostics that sample
    /// every outgoing message, such as the VRChat Output page. Null for anything else.
    /// </summary>
    public object? PeekScalar()
    {
        var ptr = _meta.Value;
        if (_meta.ValueLength == 0 || ptr == IntPtr.Zero)
        {
            return null;
        }

        return (OscValueType)Marshal.ReadByte(ptr, TypeOffset) switch
        {
            OscValueType.Int => Marshal.ReadInt32(ptr, IntOffset),
            OscValueType.Float => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(ptr, FloatOffset)),
            OscValueType.Bool => Marshal.ReadByte(ptr, BoolOffset) != 0,
            _ => null,
        };
    }

    public OscMessage(string address, Type type)
    {
        Address = address;
        var oscType = OscUtils.TypeConversions.FirstOrDefault(conv => conv.Key.Item1 == type).Value;
        
        if (oscType != default)
        {
            _meta.ValueLength = 1;
            _meta.Value = Marshal.AllocHGlobal(Marshal.SizeOf<OscValue>() * _meta.ValueLength);
            var oscValue = new OscValue
            {
                Type = oscType.oscType,
            };
            // AllocHGlobal returns uninitialized memory, and a message can be queued before
            // its first value is written: send-on-load parameters enqueue the moment an
            // avatar makes them relevant. Whatever read it then - the VRChat Output tap,
            // the native encoder - followed a garbage string pointer. The tap did exactly
            // that on 2026-09-14 (AccessViolation in Marshal.PtrToStringUTF8, app killed).
            // Start from a real value of the right type instead.
            Marshal.StructureToPtr(oscValue, _meta.Value, false);
            _valueSetter = value =>
            {
                oscValue.Value = value;
                Marshal.StructureToPtr(oscValue, _meta.Value, false);
            };
        }
        else    // If we don't have the type, we assume it's a struct and serialize it using reflection
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            _meta.ValueLength = fields.Length;
            _meta.Value = Marshal.AllocHGlobal(Marshal.SizeOf<OscValue>() * _meta.ValueLength);
            var values = new OscValue[_meta.ValueLength];
            for (var i = 0; i < _meta.ValueLength; i++)
            {
                values[i] = new OscValue
                {
                    Type = OscUtils.TypeConversions.First(conv => conv.Key.Item1 == fields[i].FieldType).Value.oscType,
                };
                // Same as above: never leave a slot as uninitialized native memory.
                Marshal.StructureToPtr(values[i], _meta.Value + Marshal.SizeOf<OscValue>() * i, false);
            }
            _valueSetter = value =>
            {
                for (var j = 0; j < _meta.ValueLength; j++)
                {
                    values[j].Value = fields[j].GetValue(value);
                    Marshal.StructureToPtr(values[j], _meta.Value + Marshal.SizeOf<OscValue>() * j, false);
                }
            };
        }
    }

    public static OscMessage TryParseOsc(byte[] bytes, int len, ref int messageIndex)
    {
        var msg = new OscMessage(bytes, len, ref messageIndex);
        if (msg._metaPtr == IntPtr.Zero)
        {
            return null;
        }

        return msg;
    }
    
    public OscMessage(byte[] bytes, int len, ref int messageIndex)
    {
        _metaPtr = fti_osc.parse_osc(bytes, len, ref messageIndex);
        if (_metaPtr != IntPtr.Zero)
        {
            _meta = Marshal.PtrToStructure<OscMessageMeta>(_metaPtr);
        }
    }

    /// <summary>
    /// Encodes stored osc meta into raw bytes using fti_osc lib
    /// </summary>
    /// <param name="buffer">Target byte buffer to serialize to, starting from index 0</param>
    /// <param name="ct">Cancellation Token</param>
    /// <returns>Length of serialized data</returns>
    public async Task<int> Encode(byte[] buffer, CancellationToken ct) => await Task.Run(() => fti_osc.create_osc_message(buffer, ref _meta), ct);

    public OscMessage(OscMessageMeta meta) => _meta = meta;
    
    ~OscMessage()
    {   
        // If we don't own this memory, then we need to sent it back to rust to free it
        if (_metaPtr != IntPtr.Zero)
        {
            fti_osc.free_osc_message(_metaPtr);
        }
    }
}