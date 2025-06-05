using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using LumoraVR.Source.Scene;
using Godot;
using Vector2 = Godot.Vector2;
using Vector3 = Godot.Vector3;
using Vector4 = Godot.Vector4;

namespace LumoraVR.Source.Helpers;

public static class SerializationHelpers
{
    public const int SessionControlChannel = 7;
    public const int WorldUpdateChannel = 8;
    public const int AssetChannel = 16;
    public const int PrefabChannel = 15;

    public delegate bool OutFunction<T>(Variant variant, out T value);

    public delegate void SetFunction<T>(T value);

    public static bool TryGetValueDirect<T>(this Godot.Collections.Dictionary<string, Variant> data, string path, OutFunction<T> outFunc, out T value)
    {
        if (data.TryGetValue(path, out var variant) && outFunc(variant, out var v))
        {
            value = v;
            return true;
        }
        value = default;
        return false;
    }

    #region AssetPointers

    public static bool TryGetAsset<T>(this Godot.Collections.Dictionary<string, Variant> dict, string path, IRootObject root, out T asset) where T : class
    {
        if (dict.TryGetValue(path, out var variant) &&
            variant.TryGetInt32(out var index) &&
            root.TryGetAsset(index, out T a))
        {
            asset = a;
            return true;
        }
        asset = null;
        return false;
    }
    public static bool TryGetAsset<T>(this IRootObject root, int index, out T asset) where T : class
    {
        if (index > 0 && root.AssetProviders.TryGetValue((ushort)index, out var a) && a is T assetType)
        {
            asset = assetType;
            return true;
        }
        asset = null;
        return false;
    }

    #endregion

    #region SerializedVariantTryGet

    public static bool TryGetEnum<T>(this Variant variant, out T value) where T : struct, Enum
    {
        if (variant.VariantType is Variant.Type.String or Variant.Type.StringName)
        {
            var str = variant.AsString().ToLowerInvariant();
            if (Enum.TryParse(str, true, out T v))
            {
                value = v;
                return true;
            }
        }
        value = default;
        return false;
    }
    public static bool TryGetBool(this Variant variant, out bool value)
    {
        switch (variant.VariantType)
        {
            case Variant.Type.Bool:
                {
                    value = variant.AsBool();
                    return true;
                }
            case Variant.Type.String or Variant.Type.StringName:
                {
                    var str = variant.AsString().ToLowerInvariant();
                    if (str.StartsWith('t'))
                    {
                        value = true;
                        return true;
                    }
                    if (str.StartsWith('f'))
                    {
                        value = false;
                        return true;
                    }
                    break;
                }
        }
        value = false;
        return false;
    }
    public static bool TryGetSByte(this Variant variant, out sbyte value)
    {
        switch (variant.VariantType)
        {
            case Variant.Type.Int or Variant.Type.Float:
                value = variant.AsSByte();
                return true;
            case Variant.Type.String or Variant.Type.StringName when sbyte.TryParse(variant.AsString(), out value):
                return true;
            default:
                value = 0;
                return false;
        }
    }
    public static bool TryGetByte(this Variant variant, out byte value)
    {
        switch (variant.VariantType)
        {
            case Variant.Type.Int or Variant.Type.Float:
                value = variant.AsByte();
                return true;
            case Variant.Type.String or Variant.Type.StringName when byte.TryParse(variant.AsString(), out value):
                return true;
            default:
                value = 0;
                return false;
        }
    }
    public static bool TryGetInt16(this Variant variant, out short value)
    {
        switch (variant.VariantType)
        {
            case Variant.Type.Int or Variant.Type.Float:
                value = variant.AsInt16();
                return true;
            case Variant.Type.String or Variant.Type.StringName when short.TryParse(variant.AsString(), out value):
                return true;
            default:
                value = 0;
                return false;
        }
    }
    public static bool TryGetUInt16(this Variant variant, out ushort value)
    {
        switch (variant.VariantType)
        {
            case Variant.Type.Int or Variant.Type.Float:
                value = variant.AsUInt16();
                return true;
            case Variant.Type.String or Variant.Type.StringName when ushort.TryParse(variant.AsString(), out value):
                return true;
            default:
                value = 0;
                return false;
        }
    }
    public static bool TryGetInt32(this Variant variant, out int value)
    {
        switch (variant.VariantType)
        {
            case Variant.Type.Int or Variant.Type.Float:
                value = variant.AsInt32();
                return true;
            case Variant.Type.String or Variant.Type.StringName when int.TryParse(variant.AsString(), out value):
                return true;
            default:
                value = 0;
                return false;
        }
    }
    public static bool TryGetUInt32(this Variant variant, out uint value)
    {
        switch (variant.VariantType)
        {
            case Variant.Type.Int or Variant.Type.Float:
                value = variant.AsUInt32();
                return true;
            case Variant.Type.String or Variant.Type.StringName when uint.TryParse(variant.AsString(), out value):
                return true;
            default:
                value = 0;
                return false;
        }
    }
    public static bool TryGetInt64(this Variant variant, out long value)
    {
        switch (variant.VariantType)
        {
            case Variant.Type.Int or Variant.Type.Float:
                value = variant.AsInt64();
                return true;
            case Variant.Type.String or Variant.Type.StringName when long.TryParse(variant.AsString(), out value):
                return true;
            default:
                value = 0;
                return false;
        }
    }
    public static bool TryGetUInt64(this Variant variant, out ulong value)
    {
        switch (variant.VariantType)
        {
            case Variant.Type.Int or Variant.Type.Float:
                value = variant.AsUInt64();
                return true;
            case Variant.Type.String or Variant.Type.StringName when ulong.TryParse(variant.AsString(), out value):
                return true;
            default:
                value = 0;
                return false;
        }
    }
    public static bool TryGetSingle(this Variant variant, out float value)
    {
        switch (variant.VariantType)
        {
            case Variant.Type.Int or Variant.Type.Float:
                value = variant.AsSingle();
                return true;
            case Variant.Type.String or Variant.Type.StringName when float.TryParse(variant.AsString(), out value):
                return true;
            default:
                value = 0;
                return false;
        }
    }
    public static bool TryGetDouble(this Variant variant, out double value)
    {
        switch (variant.VariantType)
        {
            case Variant.Type.Int or Variant.Type.Float:
                value = variant.AsDouble();
                return true;
            case Variant.Type.String or Variant.Type.StringName when double.TryParse(variant.AsString(), out value):
                return true;
            default:
                value = 0;
                return false;
        }
    }


    public static bool TryGetVector2(this Variant variant, out Vector2 value)
    {
        if (variant.TryGetFloat32Array(out var array) && array.Length == 2)
        {
            value = array.ToVector2();
            return true;
        }
        value = Vector2.Zero;
        return false;
    }
    public static bool TryGetVector3(this Variant variant, out Vector3 value)
    {
        if (variant.TryGetFloat32Array(out var array) && array.Length == 3)
        {
            value = array.ToVector3();
            return true;
        }
        value = Vector3.Zero;
        return false;
    }
    public static bool TryGetVector4(this Variant variant, out Vector4 value)
    {
        if (variant.TryGetFloat32Array(out var array) && array.Length == 4)
        {
            value = array.ToVector4();
            return true;
        }
        value = Vector4.Zero;
        return false;
    }
    public static bool TryGetColor(this Variant variant, out Color value)
    {
        if (variant.TryGetFloat32Array(out var array) && array.Length == 4)
        {
            value = array.ToColor();
            return true;
        }
        value = Colors.White;
        return false;
    }
    public static bool TryGetInt32Array(this Variant variant, out int[] value)
    {
        if (variant.VariantType is Variant.Type.Array or Variant.Type.PackedInt32Array)
        {
            value = variant.AsInt32Array();
            return true;
        }
        value = null;
        return false;
    }
    public static bool TryGetFloat32Array(this Variant variant, out float[] value)
    {
        if (variant.VariantType is Variant.Type.Array or Variant.Type.PackedFloat32Array)
        {
            value = variant.AsFloat32Array();
            return true;
        }
        value = null;
        return false;
    }
    public static bool TryGetFloat64Array(this Variant variant, out double[] value)
    {
        if (variant.VariantType is Variant.Type.Array or Variant.Type.PackedFloat64Array)
        {
            value = variant.AsFloat64Array();
            return true;
        }
        value = null;
        return false;
    }
    public static bool TryGetTransform3D(this Variant variant, out Transform3D value)
    {
        if (variant.TryGetFloat32Array(out var array) && array.Length == 12)
        {
            value = array.ToTransform3D();
            return true;
        }
        value = Transform3D.Identity;
        return false;
    }
    public static bool TryGetBasis(this Variant variant, out Basis value)
    {
        if (variant.TryGetFloat32Array(out var array) && array.Length == 9)
        {
            value = array.ToBasis();
            return true;
        }
        value = Basis.Identity;
        return false;
    }

    #endregion

    #region FloatArraySerialization

    public static float[] ToFloatArray(this Vector2 vector) => [vector.X, vector.Y];
    public static float[] ToFloatArray(this Vector3 vector) => [vector.X, vector.Y, vector.Z];
    public static float[] ToFloatArray(this Vector4 vector) => [vector.X, vector.Y, vector.Z, vector.W];
    public static float[] ToFloatArray(this Color color) => [color.R, color.G, color.B, color.A];
    public static float[] ToFloatArray(this Transform3D transform)
    {
        var basis = transform.Basis;
        var col0 = basis.Column0;
        var col1 = basis.Column1;
        var col2 = basis.Column2;
        var origin = transform.Origin;
        return
        [
            col0.X,
            col0.Y,
            col0.Z,
            col1.X,
            col1.Y,
            col1.Z,
            col2.X,
            col2.Y,
            col2.Z,
            origin.X,
            origin.Y,
            origin.Z,
        ];
    }
    public static float[] ToFloatArray(this Basis basis)
    {
        var col0 = basis.Column0;
        var col1 = basis.Column1;
        var col2 = basis.Column2;
        return
        [
            col0.X,
            col0.Y,
            col0.Z,
            col1.X,
            col1.Y,
            col1.Z,
            col2.X,
            col2.Y,
            col2.Z,
        ];
    }

    public static Vector2 ToVector2(this float[] a) => a.Length != 2 ? Vector2.Zero : new Vector2(a[0], a[1]);
    public static Vector3 ToVector3(this float[] a) => a.Length != 3 ? Vector3.Zero : new Vector3(a[0], a[1], a[2]);
    public static Vector4 ToVector4(this float[] a) => a.Length != 4 ? Vector4.Zero : new Vector4(a[0], a[1], a[2], a[3]);
    public static Color ToColor(this float[] a) => a.Length != 4 ? Colors.White : new Color(a[0], a[1], a[2], a[3]);
    public static Color ToColor(this float[] a, Color defaultColor) => a.Length != 4 ? defaultColor : new Color(a[0], a[1], a[2], a[3]);
    public static Transform3D ToTransform3D(this float[] a)
    {
        if (a.Length != 12) return Transform3D.Identity;
        return new Transform3D(
            new Vector3(a[0], a[1], a[2]),
            new Vector3(a[3], a[4], a[5]),
            new Vector3(a[6], a[7], a[8]),
            new Vector3(a[9], a[10], a[11]));
    }
    public static Basis ToBasis(this float[] a)
    {
        if (a.Length != 9) return Basis.Identity;
        return new Basis(
            new Vector3(a[0], a[1], a[2]),
            new Vector3(a[3], a[4], a[5]),
            new Vector3(a[6], a[7], a[8]));
    }

    #endregion

    #region SerializeDirtyFlags

    //DirtyFlags
    public static DirtyFlags8 ReadDirtyFlags8(this BinaryReader reader) => new(reader.ReadByte());
    public static DirtyFlags16 ReadDirtyFlags16(this BinaryReader reader) => new(reader.ReadUInt16());
    public static DirtyFlags32 ReadDirtyFlags32(this BinaryReader reader) => new(reader.ReadUInt32());
    public static DirtyFlags64 ReadDirtyFlags64(this BinaryReader reader) => new(reader.ReadUInt64());

    public static void Write(this BinaryWriter writer, DirtyFlags8 value) => writer.Write(value.Value);
    public static void Write(this BinaryWriter writer, DirtyFlags16 value) => writer.Write(value.Value);
    public static void Write(this BinaryWriter writer, DirtyFlags32 value) => writer.Write(value.Value);
    public static void Write(this BinaryWriter writer, DirtyFlags64 value) => writer.Write(value.Value);

    #endregion

    #region SerializeGodotTypes

    //Godot types
    public static Vector2 ReadVector2(this BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle());
    public static Vector3 ReadVector3(this BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    public static Vector4 ReadVector4(this BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    public static Color ReadColor(this BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    public static Vector2I ReadVector2I(this BinaryReader reader) => new(reader.ReadInt32(), reader.ReadInt32());
    public static Vector3I ReadVector3I(this BinaryReader reader) => new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
    public static Vector4I ReadVector4I(this BinaryReader reader) => new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
    public static Basis ReadBasis(this BinaryReader reader) => new(reader.ReadVector3(), reader.ReadVector3(), reader.ReadVector3());
    public static Transform3D ReadTransform3D(this BinaryReader reader) => new(reader.ReadBasis(), reader.ReadVector3());

    public static void Write(this BinaryWriter writer, Vector2 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
    }
    public static void Write(this BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }
    public static void Write(this BinaryWriter writer, Vector4 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
        writer.Write(value.W);
    }
    public static void Write(this BinaryWriter writer, Color value)
    {
        writer.Write(value.R);
        writer.Write(value.G);
        writer.Write(value.B);
        writer.Write(value.A);
    }
    public static void Write(this BinaryWriter writer, Vector2I value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
    }
    public static void Write(this BinaryWriter writer, Vector3I value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }
    public static void Write(this BinaryWriter writer, Vector4I value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
        writer.Write(value.W);
    }
    public static void Write(this BinaryWriter writer, Basis value)
    {
        writer.Write(value.Column0);
        writer.Write(value.Column1);
        writer.Write(value.Column2);
    }
    public static void Write(this BinaryWriter writer, Transform3D value)
    {
        writer.Write(value.Basis);
        writer.Write(value.Origin);
    }
    #endregion

    #region SerializeCollections
    public delegate void WriteAction<T>(BinaryWriter stream, T value);

    public static void WriteCollection<T>(this BinaryWriter stream, ICollection<T> collection, WriteAction<T> writer)
    {
        stream.Write(collection.Count);
        foreach (var item in collection) writer(stream, item);
    }

    public delegate void ReadAction<T>(BinaryReader stream, out T value);

    public static void ReadCollection<T>(this BinaryReader stream, ICollection<T> collection, ReadAction<T> reader)
    {
        collection.Clear();
        var length = stream.ReadInt32();
        for (var i = 0; i < length; i++)
        {
            reader(stream, out var value);
            collection.Add(value);
        }
    }
    public static void ReadArray<T>(this BinaryReader stream, out T[] array, ReadAction<T> reader)
    {
        var length = stream.ReadInt32();
        array = new T[length];
        for (var i = 0; i < length; i++)
        {
            reader(stream, out var value);
            array[i] = value;
        }
    }
    #endregion
}
