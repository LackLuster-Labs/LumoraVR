using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core.Math;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Generic serialization system for network sync.
/// </summary>
public static class Coder<T>
{
    private static readonly Dictionary<Type, Action<BinaryWriter, object>> Encoders = new()
    {
        [typeof(int)] = (w, v) => w.Write((int)v),
        [typeof(float)] = (w, v) => w.Write((float)v),
        [typeof(double)] = (w, v) => w.Write((double)v),
        [typeof(bool)] = (w, v) => w.Write((bool)v),
        [typeof(string)] = (w, v) => w.Write((string)v ?? string.Empty),
        [typeof(byte)] = (w, v) => w.Write((byte)v),
        [typeof(short)] = (w, v) => w.Write((short)v),
        [typeof(ushort)] = (w, v) => w.Write((ushort)v),
        [typeof(long)] = (w, v) => w.Write((long)v),
        [typeof(ulong)] = (w, v) => w.Write((ulong)v),
        [typeof(float3)] = (w, v) => { var f = (float3)v; w.Write(f.x); w.Write(f.y); w.Write(f.z); },
        [typeof(floatQ)] = (w, v) => { var q = (floatQ)v; w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); },
        [typeof(color)] = (w, v) => { var c = (color)v; w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); },
    };

    private static readonly Dictionary<Type, Func<BinaryReader, object>> Decoders = new()
    {
        [typeof(int)] = r => r.ReadInt32(),
        [typeof(float)] = r => r.ReadSingle(),
        [typeof(double)] = r => r.ReadDouble(),
        [typeof(bool)] = r => r.ReadBoolean(),
        [typeof(string)] = r => r.ReadString(),
        [typeof(byte)] = r => r.ReadByte(),
        [typeof(short)] = r => r.ReadInt16(),
        [typeof(ushort)] = r => r.ReadUInt16(),
        [typeof(long)] = r => r.ReadInt64(),
        [typeof(ulong)] = r => r.ReadUInt64(),
        [typeof(float3)] = r => new float3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        [typeof(floatQ)] = r => new floatQ(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        [typeof(color)] = r => new color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
    };

    private static readonly Dictionary<Type, Func<object, object, bool>> Comparers = new()
    {
        [typeof(float)] = (a, b) => System.Math.Abs((float)a - (float)b) < 0.0001f,
        [typeof(double)] = (a, b) => System.Math.Abs((double)a - (double)b) < 0.0001,
        [typeof(float3)] = (a, b) => ((float3)a).Equals((float3)b),
        [typeof(floatQ)] = (a, b) => ((floatQ)a).Equals((floatQ)b),
        [typeof(color)] = (a, b) => ((color)a).Equals((color)b),
    };

    public static void Encode(BinaryWriter writer, T value)
    {
        var type = typeof(T);
        if (Encoders.TryGetValue(type, out var encoder))
        {
            encoder(writer, value);
            return;
        }
        throw new NotSupportedException($"Type {type} is not supported for encoding");
    }

    public static T Decode(BinaryReader reader)
    {
        var type = typeof(T);
        if (Decoders.TryGetValue(type, out var decoder))
            return (T)decoder(reader);
        throw new NotSupportedException($"Type {type} is not supported for decoding");
    }

    public static bool Equals(T a, T b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;

        var type = typeof(T);
        if (Comparers.TryGetValue(type, out var comparer))
            return comparer(a, b);

        return a.Equals(b);
    }
}
