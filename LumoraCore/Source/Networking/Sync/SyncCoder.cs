using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core.Math;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Binary encoding/decoding for sync types.
/// </summary>
public static class SyncCoder
{
    private static readonly Dictionary<Type, Action<BinaryWriter, object>> Encoders = new()
    {
        [typeof(bool)] = (w, v) => w.Write((bool)v),
        [typeof(byte)] = (w, v) => w.Write((byte)v),
        [typeof(sbyte)] = (w, v) => w.Write((sbyte)v),
        [typeof(short)] = (w, v) => w.Write((short)v),
        [typeof(ushort)] = (w, v) => w.Write((ushort)v),
        [typeof(int)] = (w, v) => w.Write((int)v),
        [typeof(uint)] = (w, v) => w.Write((uint)v),
        [typeof(long)] = (w, v) => w.Write((long)v),
        [typeof(ulong)] = (w, v) => w.Write((ulong)v),
        [typeof(float)] = (w, v) => w.Write((float)v),
        [typeof(double)] = (w, v) => w.Write((double)v),
        [typeof(string)] = (w, v) => w.Write((string)v ?? string.Empty),
        [typeof(float2)] = (w, v) => { var f = (float2)v; w.Write(f.x); w.Write(f.y); },
        [typeof(float3)] = (w, v) => { var f = (float3)v; w.Write(f.x); w.Write(f.y); w.Write(f.z); },
        [typeof(float4)] = (w, v) => { var f = (float4)v; w.Write(f.x); w.Write(f.y); w.Write(f.z); w.Write(f.w); },
        [typeof(floatQ)] = (w, v) => { var q = (floatQ)v; w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); },
    };

    private static readonly Dictionary<Type, Func<BinaryReader, object>> Decoders = new()
    {
        [typeof(bool)] = r => r.ReadBoolean(),
        [typeof(byte)] = r => r.ReadByte(),
        [typeof(sbyte)] = r => r.ReadSByte(),
        [typeof(short)] = r => r.ReadInt16(),
        [typeof(ushort)] = r => r.ReadUInt16(),
        [typeof(int)] = r => r.ReadInt32(),
        [typeof(uint)] = r => r.ReadUInt32(),
        [typeof(long)] = r => r.ReadInt64(),
        [typeof(ulong)] = r => r.ReadUInt64(),
        [typeof(float)] = r => r.ReadSingle(),
        [typeof(double)] = r => r.ReadDouble(),
        [typeof(string)] = r => r.ReadString(),
        [typeof(float2)] = r => new float2(r.ReadSingle(), r.ReadSingle()),
        [typeof(float3)] = r => new float3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        [typeof(float4)] = r => new float4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        [typeof(floatQ)] = r => new floatQ(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
    };

    public static void Encode<T>(BinaryWriter writer, T value)
    {
        var type = typeof(T);

        if (Encoders.TryGetValue(type, out var encoder))
        {
            encoder(writer, value!);
            return;
        }

        if (type.IsEnum)
        {
            writer.Write(Convert.ToInt32(value));
            return;
        }

        throw new NotSupportedException($"SyncCoder: Type {type} not supported");
    }

    public static T Decode<T>(BinaryReader reader)
    {
        var type = typeof(T);

        if (Decoders.TryGetValue(type, out var decoder))
            return (T)decoder(reader);

        if (type.IsEnum)
            return (T)Enum.ToObject(type, reader.ReadInt32());

        throw new NotSupportedException($"SyncCoder: Type {type} not supported");
    }
}
