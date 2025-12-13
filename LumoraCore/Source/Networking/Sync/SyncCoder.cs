using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core;
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
        [typeof(RefID)] = (w, v) => w.Write((ulong)(RefID)v),
        [typeof(WorldDelegate)] = (w, v) => EncodeWorldDelegate(w, (WorldDelegate)v),
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
        [typeof(RefID)] = r => new RefID(r.ReadUInt64()),
        [typeof(WorldDelegate)] = r => DecodeWorldDelegate(r),
    };

    private static readonly Dictionary<Type, object> Defaults = new()
    {
        [typeof(bool)] = false,
        [typeof(byte)] = (byte)0,
        [typeof(sbyte)] = (sbyte)0,
        [typeof(short)] = (short)0,
        [typeof(ushort)] = (ushort)0,
        [typeof(int)] = 0,
        [typeof(uint)] = 0u,
        [typeof(long)] = 0L,
        [typeof(ulong)] = 0UL,
        [typeof(float)] = 0f,
        [typeof(double)] = 0d,
        [typeof(string)] = string.Empty,
        [typeof(float2)] = new float2(0, 0),
        [typeof(float3)] = new float3(0, 0, 0),
        [typeof(float4)] = new float4(0, 0, 0, 0),
        [typeof(floatQ)] = floatQ.Identity,
        [typeof(RefID)] = RefID.Null,
        [typeof(WorldDelegate)] = default(WorldDelegate),
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

    /// <summary>
    /// Get the default value for a type.
    /// </summary>
    public static T GetDefault<T>()
    {
        var type = typeof(T);

        if (Defaults.TryGetValue(type, out var defaultValue))
            return (T)defaultValue;

        if (type.IsEnum)
            return default!;

        if (type.IsValueType)
            return default!;

        return default!;
    }

    /// <summary>
    /// Check equality between two values.
    /// </summary>
    public static bool Equals<T>(T a, T b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        return EqualityComparer<T>.Default.Equals(a, b);
    }

    /// <summary>
    /// Encode a WorldDelegate to binary.
    /// </summary>
    private static void EncodeWorldDelegate(BinaryWriter writer, WorldDelegate value)
    {
        writer.Write((ulong)value.Target);
        writer.Write(value.Method ?? string.Empty);
        bool hasType = value.Type != null;
        writer.Write(hasType);
        if (hasType)
        {
            writer.Write(value.Type!.AssemblyQualifiedName ?? string.Empty);
        }
    }

    /// <summary>
    /// Decode a WorldDelegate from binary.
    /// </summary>
    private static WorldDelegate DecodeWorldDelegate(BinaryReader reader)
    {
        RefID target = new RefID(reader.ReadUInt64());
        string method = reader.ReadString();
        Type? type = null;
        if (reader.ReadBoolean())
        {
            string typeName = reader.ReadString();
            if (!string.IsNullOrEmpty(typeName))
            {
                type = Type.GetType(typeName);
            }
        }
        return new WorldDelegate(target, method, type);
    }
}
