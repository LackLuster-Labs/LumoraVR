using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Input;

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
        [typeof(Uri)] = (w, v) => w.Write(((Uri)v)?.ToString() ?? string.Empty),
        [typeof(float2)] = (w, v) => { var f = (float2)v; w.Write(f.x); w.Write(f.y); },
        [typeof(float3)] = (w, v) => { var f = (float3)v; w.Write(f.x); w.Write(f.y); w.Write(f.z); },
        [typeof(float4)] = (w, v) => { var f = (float4)v; w.Write(f.x); w.Write(f.y); w.Write(f.z); w.Write(f.w); },
        [typeof(int4)] = (w, v) => { var i = (int4)v; w.Write(i.x); w.Write(i.y); w.Write(i.z); w.Write(i.w); },
        [typeof(floatQ)] = (w, v) => { var q = (floatQ)v; w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); },
        [typeof(color)] = (w, v) => { var c = (color)v; w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); },
        [typeof(colorHDR)] = (w, v) => { var c = (colorHDR)v; w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); },
        [typeof(RefID)] = (w, v) => w.Write((ulong)(RefID)v),
        [typeof(WorldDelegate)] = (w, v) => EncodeWorldDelegate(w, (WorldDelegate)v),
        [typeof(BoundingBox)] = (w, v) => EncodeBoundingBox(w, (BoundingBox)v),
        [typeof(BodyNode)] = (w, v) => w.Write((int)(BodyNode)v),
        [typeof(BodyNode?)] = (w, v) => EncodeNullableBodyNode(w, (BodyNode?)v),
        [typeof(object)] = (w, v) => EncodeObject(w, v),
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
        [typeof(Uri)] = r => { var s = r.ReadString(); return string.IsNullOrEmpty(s) ? null : new Uri(s); },
        [typeof(float2)] = r => new float2(r.ReadSingle(), r.ReadSingle()),
        [typeof(float3)] = r => new float3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        [typeof(float4)] = r => new float4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        [typeof(int4)] = r => new int4(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()),
        [typeof(floatQ)] = r => new floatQ(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        [typeof(color)] = r => new color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        [typeof(colorHDR)] = r => new colorHDR(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        [typeof(RefID)] = r => new RefID(r.ReadUInt64()),
        [typeof(WorldDelegate)] = r => DecodeWorldDelegate(r),
        [typeof(BoundingBox)] = r => DecodeBoundingBox(r),
        [typeof(BodyNode)] = r => (BodyNode)r.ReadInt32(),
        [typeof(BodyNode?)] = r => DecodeNullableBodyNode(r),
        [typeof(object)] = r => DecodeObject(r),
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
        [typeof(Uri)] = null,
        [typeof(float2)] = new float2(0, 0),
        [typeof(float3)] = new float3(0, 0, 0),
        [typeof(float4)] = new float4(0, 0, 0, 0),
        [typeof(int4)] = new int4(0, 0, 0, 0),
        [typeof(floatQ)] = floatQ.Identity,
        [typeof(color)] = new color(1f, 1f, 1f, 1f),
        [typeof(colorHDR)] = new colorHDR(1f, 1f, 1f, 1f),
        [typeof(RefID)] = RefID.Null,
        [typeof(WorldDelegate)] = default(WorldDelegate),
        [typeof(BoundingBox)] = new BoundingBox(),
        [typeof(BodyNode)] = BodyNode.NONE,
        [typeof(BodyNode?)] = null,
        [typeof(object)] = null,
    };

    public static void Encode<T>(BinaryWriter writer, T value)
    {
        var type = typeof(T);

        if (Encoders.TryGetValue(type, out var encoder))
        {
            try
            {
                encoder(writer, value!);
            }
            catch (Exception ex)
            {
                // Log error but don't crash - write default value instead
                Console.WriteLine($"SyncCoder: Failed to encode {type}: {ex.Message}");
                EncodeObject(writer, null); // Write safe null marker
            }
            return;
        }

        if (type.IsEnum)
        {
            try
            {
                writer.Write(Convert.ToInt32(value));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SyncCoder: Failed to encode enum {type}: {ex.Message}");
                writer.Write(0); // Default enum value
            }
            return;
        }

        // Instead of throwing, log the error and write a null marker
        Console.WriteLine($"SyncCoder: Type {type} not supported - using null encoding");
        EncodeObject(writer, null);
    }

    public static T Decode<T>(BinaryReader reader)
    {
        var type = typeof(T);

        if (Decoders.TryGetValue(type, out var decoder))
        {
            try
            {
                return (T)decoder(reader);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SyncCoder: Failed to decode {type}: {ex.Message}");
                // Try to consume a null marker to keep stream consistent
                try { reader.ReadBoolean(); } catch { }
                return GetDefault<T>();
            }
        }

        if (type.IsEnum)
        {
            try
            {
                return (T)Enum.ToObject(type, reader.ReadInt32());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SyncCoder: Failed to decode enum {type}: {ex.Message}");
                return GetDefault<T>();
            }
        }

        // Instead of throwing, log the error and return default value
        Console.WriteLine($"SyncCoder: Type {type} not supported - using default value");
        // Try to consume a null marker to keep stream consistent
        try { reader.ReadBoolean(); } catch { }
        return GetDefault<T>();
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

    /// <summary>
    /// Encode a BoundingBox to binary.
    /// </summary>
    private static void EncodeBoundingBox(BinaryWriter writer, BoundingBox value)
    {
        writer.Write(value.Min.x);
        writer.Write(value.Min.y);
        writer.Write(value.Min.z);
        writer.Write(value.Max.x);
        writer.Write(value.Max.y);
        writer.Write(value.Max.z);
    }

    /// <summary>
    /// Decode a BoundingBox from binary.
    /// </summary>
    private static BoundingBox DecodeBoundingBox(BinaryReader reader)
    {
        float minX = reader.ReadSingle();
        float minY = reader.ReadSingle();
        float minZ = reader.ReadSingle();
        float maxX = reader.ReadSingle();
        float maxY = reader.ReadSingle();
        float maxZ = reader.ReadSingle();
        return new BoundingBox(new float3(minX, minY, minZ), new float3(maxX, maxY, maxZ));
    }

    /// <summary>
    /// Encode a nullable BodyNode to binary.
    /// </summary>
    private static void EncodeNullableBodyNode(BinaryWriter writer, BodyNode? value)
    {
        bool hasValue = value.HasValue;
        writer.Write(hasValue);
        if (hasValue)
        {
            writer.Write((int)value.Value);
        }
    }

    /// <summary>
    /// Decode a nullable BodyNode from binary.
    /// </summary>
    private static BodyNode? DecodeNullableBodyNode(BinaryReader reader)
    {
        bool hasValue = reader.ReadBoolean();
        if (hasValue)
        {
            return (BodyNode)reader.ReadInt32();
        }
        return null;
    }

    /// <summary>
    /// Encode an object to binary. For System.Object type, we skip it to avoid sync issues.
    /// </summary>
    private static void EncodeObject(BinaryWriter writer, object value)
    {
        // For generic object type, we write a null marker to maintain stream consistency
        // This prevents sync issues when object types are used in sync members
        writer.Write(false); // false = no data
    }

    /// <summary>
    /// Decode an object from binary. Returns null for System.Object type.
    /// </summary>
    private static object DecodeObject(BinaryReader reader)
    {
        // Read the null marker to maintain stream consistency
        reader.ReadBoolean(); // consume the false value
        return null;
    }
}
