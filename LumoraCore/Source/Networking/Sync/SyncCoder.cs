// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Input;
using Lumora.Nexus.Transport;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Sync;

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
        // OriginalString, not ToString: that one lowercases the host, and a local:// machine id is
        // case-sensitive, so the owner of a client-imported asset could never be found. -xlinka
        [typeof(Uri)] = (w, v) => w.Write(((Uri)v)?.OriginalString ?? string.Empty),
        [typeof(float2)] = (w, v) => { var f = (float2)v; w.Write(f.x); w.Write(f.y); },
        [typeof(float3)] = (w, v) => { var f = (float3)v; w.Write(f.x); w.Write(f.y); w.Write(f.z); },
        [typeof(float4)] = (w, v) => { var f = (float4)v; w.Write(f.x); w.Write(f.y); w.Write(f.z); w.Write(f.w); },
        [typeof(int4)] = (w, v) => { var i = (int4)v; w.Write(i.x); w.Write(i.y); w.Write(i.z); w.Write(i.w); },
        // The rest of the math types, so a value component on any of them replicates its value rather than an
        // empty marker. Same set as DataTreeCoder: a type has to both replicate and save, or neither. -xlinka
        [typeof(int2)] = (w, v) => { var i = (int2)v; w.Write(i.x); w.Write(i.y); },
        [typeof(int3)] = (w, v) => { var i = (int3)v; w.Write(i.x); w.Write(i.y); w.Write(i.z); },
        [typeof(bool2)] = (w, v) => { var b = (bool2)v; w.Write(b.x); w.Write(b.y); },
        [typeof(bool3)] = (w, v) => { var b = (bool3)v; w.Write(b.x); w.Write(b.y); w.Write(b.z); },
        [typeof(bool4)] = (w, v) => { var b = (bool4)v; w.Write(b.x); w.Write(b.y); w.Write(b.z); w.Write(b.w); },
        [typeof(float2x2)] = (w, v) => { var m = (float2x2)v; w.Write(m.c0.x); w.Write(m.c0.y); w.Write(m.c1.x); w.Write(m.c1.y); },
        [typeof(SphericalHarmonicsL2)] = (w, v) => EncodeHarmonics(w, (SphericalHarmonicsL2)v),
        [typeof(char)] = (w, v) => w.Write((ushort)(char)v),
        [typeof(decimal)] = (w, v) => w.Write((decimal)v),
        [typeof(DateTime)] = (w, v) => w.Write(((DateTime)v).ToBinary()),
        [typeof(floatQ)] = (w, v) => { var q = (floatQ)v; w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); },
        [typeof(color)] = (w, v) => { var c = (color)v; w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); },
        [typeof(colorHDR)] = (w, v) => { var c = (colorHDR)v; w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); },
        [typeof(RefID)] = (w, v) => w.Write((ulong)(RefID)v),
        [typeof(WorldDelegate)] = (w, v) => EncodeWorldDelegate(w, (WorldDelegate)v),
        [typeof(BoundingBox)] = (w, v) => EncodeBoundingBox(w, (BoundingBox)v),
        // These three had no entry and went out as an empty marker, so every joiner read the default: a
        // zero UV rect on every UI image and UI material, no rest pose on a skeleton builder, no forward
        // axis on a humanoid rig. -xlinka
        [typeof(Rect)] = (w, v) => { var r = (Rect)v; w.Write(r.x); w.Write(r.y); w.Write(r.width); w.Write(r.height); },
        [typeof(float4x4)] = (w, v) => EncodeMatrix(w, (float4x4)v),
        [typeof(float3x3)] = (w, v) => EncodeMatrix3(w, (float3x3)v),
        [typeof(float3?)] = (w, v) => EncodeNullableFloat3(w, (float3?)v),
        [typeof(BodyNode)] = (w, v) => w.Write((int)(BodyNode)v),
        [typeof(BodyNode?)] = (w, v) => EncodeNullableBodyNode(w, (BodyNode?)v),
        [typeof(float?)] = (w, v) => EncodeNullableFloat(w, (float?)v),
        [typeof(int?)] = (w, v) => EncodeNullableInt(w, (int?)v),
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
        [typeof(Uri)] = r =>
        {
            // Bound the string before alloc, then use TryCreate so a malformed peer
            // URI returns null instead of throwing. The outer Decode<T> catch tries
            // to "resync" by reading a bool, which would actually desync the stream
            // for Uri (its wire format is length-prefixed text, not a marker).
            var s = r.ReadBoundedString(NetworkLimits.MaxAssetUriBytes);
            if (string.IsNullOrEmpty(s)) return null!;
            return Uri.TryCreate(s, UriKind.RelativeOrAbsolute, out var u) ? u : null!;
        },
        [typeof(float2)] = r => new float2(r.ReadSingle(), r.ReadSingle()),
        [typeof(float3)] = r => new float3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        [typeof(float4)] = r => new float4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        [typeof(int4)] = r => new int4(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()),
        [typeof(int2)] = r => new int2(r.ReadInt32(), r.ReadInt32()),
        [typeof(int3)] = r => new int3(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()),
        [typeof(bool2)] = r => new bool2(r.ReadBoolean(), r.ReadBoolean()),
        [typeof(bool3)] = r => new bool3(r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean()),
        [typeof(bool4)] = r => new bool4(r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean()),
        [typeof(float2x2)] = r => new float2x2(new float2(r.ReadSingle(), r.ReadSingle()), new float2(r.ReadSingle(), r.ReadSingle())),
        [typeof(SphericalHarmonicsL2)] = r => DecodeHarmonics(r),
        [typeof(char)] = r => (char)r.ReadUInt16(),
        [typeof(decimal)] = r => r.ReadDecimal(),
        [typeof(DateTime)] = r => DateTime.FromBinary(r.ReadInt64()),
        [typeof(floatQ)] = r => new floatQ(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        [typeof(color)] = r => new color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        [typeof(colorHDR)] = r => new colorHDR(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        [typeof(RefID)] = r => new RefID(r.ReadUInt64()),
        [typeof(WorldDelegate)] = r => DecodeWorldDelegate(r),
        [typeof(BoundingBox)] = r => DecodeBoundingBox(r),
        [typeof(Rect)] = r => new Rect(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        [typeof(float4x4)] = r => DecodeMatrix(r),
        [typeof(float3x3)] = r => DecodeMatrix3(r),
        [typeof(float3?)] = r => DecodeNullableFloat3(r)!,
        [typeof(BodyNode)] = r => (BodyNode)r.ReadInt32(),
        [typeof(BodyNode?)] = r => DecodeNullableBodyNode(r)!,
        [typeof(float?)] = r => DecodeNullableFloat(r)!,
        [typeof(int?)] = r => DecodeNullableInt(r)!,
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
        [typeof(Uri)] = null!,
        [typeof(float2)] = new float2(0, 0),
        [typeof(float3)] = new float3(0, 0, 0),
        [typeof(float4)] = new float4(0, 0, 0, 0),
        [typeof(int4)] = new int4(0, 0, 0, 0),
        [typeof(int2)] = new int2(0, 0),
        [typeof(int3)] = new int3(0, 0, 0),
        [typeof(floatQ)] = floatQ.Identity,
        [typeof(color)] = new color(1f, 1f, 1f, 1f),
        [typeof(colorHDR)] = new colorHDR(1f, 1f, 1f, 1f),
        [typeof(RefID)] = RefID.Null,
        [typeof(WorldDelegate)] = default(WorldDelegate),
        [typeof(BoundingBox)] = new BoundingBox(),
        [typeof(float3x3)] = float3x3.Identity,
        [typeof(BodyNode)] = BodyNode.NONE,
        [typeof(BodyNode?)] = null!,
        [typeof(float?)] = null!,
        [typeof(object)] = null!,
    };

    // The value types a frame is mostly made of, written and read without going through object. The tables
    // box every value they are handed, and with a crowd in that was a box per bone per pose, both ways, on the
    // sync threads. Each case writes and reads exactly what its table entry does; everything else, enums not
    // stored as int included, still goes through the tables. -xlinka
    private static class Int32Enum<T>
    {
        public static readonly bool Is = typeof(T).IsEnum && Enum.GetUnderlyingType(typeof(T)) == typeof(int);
    }

    private static bool TryEncodeDirect<T>(BinaryWriter w, ref T value)
    {
        if (!typeof(T).IsValueType)
            return false;
        if (typeof(T) == typeof(float)) { w.Write(Unsafe.As<T, float>(ref value)); return true; }
        if (typeof(T) == typeof(bool)) { w.Write(Unsafe.As<T, bool>(ref value)); return true; }
        if (typeof(T) == typeof(int)) { w.Write(Unsafe.As<T, int>(ref value)); return true; }
        if (typeof(T) == typeof(uint)) { w.Write(Unsafe.As<T, uint>(ref value)); return true; }
        if (typeof(T) == typeof(long)) { w.Write(Unsafe.As<T, long>(ref value)); return true; }
        if (typeof(T) == typeof(ulong)) { w.Write(Unsafe.As<T, ulong>(ref value)); return true; }
        if (typeof(T) == typeof(double)) { w.Write(Unsafe.As<T, double>(ref value)); return true; }
        if (typeof(T) == typeof(byte)) { w.Write(Unsafe.As<T, byte>(ref value)); return true; }
        if (typeof(T) == typeof(float2)) { var f = Unsafe.As<T, float2>(ref value); w.Write(f.x); w.Write(f.y); return true; }
        if (typeof(T) == typeof(float3)) { var f = Unsafe.As<T, float3>(ref value); w.Write(f.x); w.Write(f.y); w.Write(f.z); return true; }
        if (typeof(T) == typeof(float4)) { var f = Unsafe.As<T, float4>(ref value); w.Write(f.x); w.Write(f.y); w.Write(f.z); w.Write(f.w); return true; }
        if (typeof(T) == typeof(floatQ)) { var q = Unsafe.As<T, floatQ>(ref value); w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); return true; }
        if (typeof(T) == typeof(color)) { var c = Unsafe.As<T, color>(ref value); w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); return true; }
        if (typeof(T) == typeof(colorHDR)) { var c = Unsafe.As<T, colorHDR>(ref value); w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); return true; }
        if (typeof(T) == typeof(RefID)) { w.Write((ulong)Unsafe.As<T, RefID>(ref value)); return true; }
        if (Int32Enum<T>.Is) { w.Write(Unsafe.As<T, int>(ref value)); return true; }
        return false;
    }

    private static bool TryDecodeDirect<T>(BinaryReader r, out T value)
    {
        value = default!;
        if (!typeof(T).IsValueType)
            return false;
        if (typeof(T) == typeof(float)) { float v = r.ReadSingle(); value = Unsafe.As<float, T>(ref v); return true; }
        if (typeof(T) == typeof(bool)) { bool v = r.ReadBoolean(); value = Unsafe.As<bool, T>(ref v); return true; }
        if (typeof(T) == typeof(int)) { int v = r.ReadInt32(); value = Unsafe.As<int, T>(ref v); return true; }
        if (typeof(T) == typeof(uint)) { uint v = r.ReadUInt32(); value = Unsafe.As<uint, T>(ref v); return true; }
        if (typeof(T) == typeof(long)) { long v = r.ReadInt64(); value = Unsafe.As<long, T>(ref v); return true; }
        if (typeof(T) == typeof(ulong)) { ulong v = r.ReadUInt64(); value = Unsafe.As<ulong, T>(ref v); return true; }
        if (typeof(T) == typeof(double)) { double v = r.ReadDouble(); value = Unsafe.As<double, T>(ref v); return true; }
        if (typeof(T) == typeof(byte)) { byte v = r.ReadByte(); value = Unsafe.As<byte, T>(ref v); return true; }
        if (typeof(T) == typeof(float2)) { var v = new float2(r.ReadSingle(), r.ReadSingle()); value = Unsafe.As<float2, T>(ref v); return true; }
        if (typeof(T) == typeof(float3)) { var v = new float3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); value = Unsafe.As<float3, T>(ref v); return true; }
        if (typeof(T) == typeof(float4)) { var v = new float4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); value = Unsafe.As<float4, T>(ref v); return true; }
        if (typeof(T) == typeof(floatQ)) { var v = new floatQ(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); value = Unsafe.As<floatQ, T>(ref v); return true; }
        if (typeof(T) == typeof(color)) { var v = new color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); value = Unsafe.As<color, T>(ref v); return true; }
        if (typeof(T) == typeof(colorHDR)) { var v = new colorHDR(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); value = Unsafe.As<colorHDR, T>(ref v); return true; }
        if (typeof(T) == typeof(RefID)) { var v = new RefID(r.ReadUInt64()); value = Unsafe.As<RefID, T>(ref v); return true; }
        if (Int32Enum<T>.Is) { int v = r.ReadInt32(); value = Unsafe.As<int, T>(ref v); return true; }
        return false;
    }

    public static void Encode<T>(BinaryWriter writer, T value)
    {
        var type = typeof(T);

        try
        {
            if (TryEncodeDirect(writer, ref value))
                return;
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"SyncCoder: Failed to encode {type}: {ex.Message}");
            EncodeObject(writer, null!);
            return;
        }

        if (Encoders.TryGetValue(type, out var encoder))
        {
            try
            {
                encoder(writer, value!);
            }
            catch (Exception ex)
            {
                // Log error but don't crash - write default value instead
                LumoraLogger.Warn($"SyncCoder: Failed to encode {type}: {ex.Message}");
                EncodeObject(writer, null!); // Write safe null marker
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
                LumoraLogger.Warn($"SyncCoder: Failed to encode enum {type}: {ex.Message}");
                writer.Write(0); // Default enum value
            }
            return;
        }

        // Instead of throwing, log the error and write a null marker
        LumoraLogger.Warn($"SyncCoder: Type {type} not supported - using null encoding");
        EncodeObject(writer, null!);
    }

    public static T Decode<T>(BinaryReader reader)
    {
        var type = typeof(T);

        try
        {
            if (TryDecodeDirect(reader, out T direct))
                return direct;
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"SyncCoder: Failed to decode {type}: {ex.Message}");
            try { reader.ReadBoolean(); } catch { }
            return GetDefault<T>();
        }

        if (Decoders.TryGetValue(type, out var decoder))
        {
            try
            {
                return (T)decoder(reader);
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"SyncCoder: Failed to decode {type}: {ex.Message}");
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
                LumoraLogger.Warn($"SyncCoder: Failed to decode enum {type}: {ex.Message}");
                return GetDefault<T>();
            }
        }

        // Instead of throwing, log the error and return default value
        LumoraLogger.Warn($"SyncCoder: Type {type} not supported - using default value");
        // Try to consume a null marker to keep stream consistent
        try { reader.ReadBoolean(); } catch { }
        return GetDefault<T>();
    }

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

    // The default comparer already answers null against null and null against a value. Checking `a == null`
    // here first boxed both values of a struct field on every write wherever the code ran unoptimized: the
    // editor build, and every method before it tiers up. -xlinka
    public static bool Equals<T>(T a, T b) => EqualityComparer<T>.Default.Equals(a, b);

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
        return new WorldDelegate(target, method, type!);
    }

    private static void EncodeBoundingBox(BinaryWriter writer, BoundingBox value)
    {
        writer.Write(value.Min.x);
        writer.Write(value.Min.y);
        writer.Write(value.Min.z);
        writer.Write(value.Max.x);
        writer.Write(value.Max.y);
        writer.Write(value.Max.z);
    }

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

    private static void EncodeHarmonics(BinaryWriter writer, in SphericalHarmonicsL2 value)
    {
        for (int i = 0; i < SphericalHarmonicsL2.Count; i++)
            WriteFloat3(writer, value[i]);
    }

    private static SphericalHarmonicsL2 DecodeHarmonics(BinaryReader reader)
    {
        var value = default(SphericalHarmonicsL2);
        for (int i = 0; i < SphericalHarmonicsL2.Count; i++)
            value[i] = new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        return value;
    }

    private static void EncodeMatrix3(BinaryWriter writer, float3x3 value)
    {
        WriteFloat3(writer, value.c0);
        WriteFloat3(writer, value.c1);
        WriteFloat3(writer, value.c2);
    }

    private static float3x3 DecodeMatrix3(BinaryReader reader)
    {
        var c0 = new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var c1 = new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var c2 = new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        return new float3x3(c0, c1, c2);
    }

    private static void WriteFloat3(BinaryWriter writer, in float3 value)
    {
        writer.Write(value.x);
        writer.Write(value.y);
        writer.Write(value.z);
    }

    private static void EncodeMatrix(BinaryWriter writer, float4x4 value)
    {
        void Column(in float4 c)
        {
            writer.Write(c.x);
            writer.Write(c.y);
            writer.Write(c.z);
            writer.Write(c.w);
        }
        Column(value.c0);
        Column(value.c1);
        Column(value.c2);
        Column(value.c3);
    }

    private static float4x4 DecodeMatrix(BinaryReader reader)
    {
        float4 Column() => new float4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var c0 = Column();
        var c1 = Column();
        var c2 = Column();
        var c3 = Column();
        return new float4x4(c0, c1, c2, c3);
    }

    private static void EncodeNullableFloat3(BinaryWriter writer, float3? value)
    {
        writer.Write(value.HasValue);
        if (value is { } v)
        {
            writer.Write(v.x);
            writer.Write(v.y);
            writer.Write(v.z);
        }
    }

    private static float3? DecodeNullableFloat3(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
            return null;
        return new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static void EncodeNullableBodyNode(BinaryWriter writer, BodyNode? value)
    {
        bool hasValue = value.HasValue;
        writer.Write(hasValue);
        if (hasValue)
        {
            writer.Write((int)value.GetValueOrDefault());
        }
    }

    private static BodyNode? DecodeNullableBodyNode(BinaryReader reader)
    {
        bool hasValue = reader.ReadBoolean();
        if (hasValue)
        {
            return (BodyNode)reader.ReadInt32();
        }
        return null;
    }

    // Same marker-then-payload shape as the nullable body node: an avatar's optional clip planes are
    // a float? per plane, and a nullable with no coder silently falls through to the object encoder.
    private static void EncodeNullableFloat(BinaryWriter writer, float? value)
    {
        bool hasValue = value.HasValue;
        writer.Write(hasValue);
        if (hasValue)
        {
            writer.Write(value.GetValueOrDefault());
        }
    }

    private static float? DecodeNullableFloat(BinaryReader reader)
    {
        bool hasValue = reader.ReadBoolean();
        if (hasValue)
        {
            return reader.ReadSingle();
        }
        return null;
    }

    // A grabbable's priority while held is an int?, and with no coder it went out as an empty marker: every
    // other machine read null, and every encode of it printed a line to the console.
    private static void EncodeNullableInt(BinaryWriter writer, int? value)
    {
        bool hasValue = value.HasValue;
        writer.Write(hasValue);
        if (hasValue)
        {
            writer.Write(value.GetValueOrDefault());
        }
    }

    private static int? DecodeNullableInt(BinaryReader reader)
    {
        bool hasValue = reader.ReadBoolean();
        if (hasValue)
        {
            return reader.ReadInt32();
        }
        return null;
    }

    private static void EncodeObject(BinaryWriter writer, object value)
    {
        // For generic object type, we write a null marker to maintain stream consistency
        // This prevents sync issues when object types are used in sync members
        writer.Write(false); // false = no data
    }

    // Returns null for System.Object.
    private static object DecodeObject(BinaryReader reader)
    {
        // Read the null marker to maintain stream consistency
        reader.ReadBoolean(); // consume the false value
        return null!;
    }
}
