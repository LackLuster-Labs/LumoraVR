using System;
using System.IO;
using Godot;

namespace Aquamarine.Source.Godot.Extensions;

public static class GodotSerializationExtensions
{
    // Vector2
    public static void Write(this BinaryWriter writer, Vector2 v)
    {
        writer.Write(v.X);
        writer.Write(v.Y);
    }

    public static Vector2 ReadVector2(this BinaryReader reader)
    {
        return new Vector2(reader.ReadSingle(), reader.ReadSingle());
    }

    // Vector3
    public static void Write(this BinaryWriter writer, Vector3 v)
    {
        writer.Write(v.X);
        writer.Write(v.Y);
        writer.Write(v.Z);
    }

    public static Vector3 ReadVector3(this BinaryReader reader)
    {
        return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    // Vector3I
    public static void Write(this BinaryWriter writer, Vector3I v)
    {
        writer.Write(v.X);
        writer.Write(v.Y);
        writer.Write(v.Z);
    }

    public static Vector3I ReadVector3I(this BinaryReader reader)
    {
        return new Vector3I(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
    }

    // Vector4
    public static void Write(this BinaryWriter writer, Vector4 v)
    {
        writer.Write(v.X);
        writer.Write(v.Y);
        writer.Write(v.Z);
        writer.Write(v.W);
    }

    public static Vector4 ReadVector4(this BinaryReader reader)
    {
        return new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    // Vector4I
    public static void Write(this BinaryWriter writer, Vector4I v)
    {
        writer.Write(v.X);
        writer.Write(v.Y);
        writer.Write(v.Z);
        writer.Write(v.W);
    }

    public static Vector4I ReadVector4I(this BinaryReader reader)
    {
        return new Vector4I(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
    }

    // Color
    public static void Write(this BinaryWriter writer, Color c)
    {
        writer.Write(c.R);
        writer.Write(c.G);
        writer.Write(c.B);
        writer.Write(c.A);
    }

    public static Color ReadColor(this BinaryReader reader)
    {
        return new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    // Transform3D
    public static void Write(this BinaryWriter writer, Transform3D t)
    {
        // Basis (3x3 matrix stored as column vectors)
        writer.Write(t.Basis.X);
        writer.Write(t.Basis.Y);
        writer.Write(t.Basis.Z);
        // Origin
        writer.Write(t.Origin);
    }

    public static Transform3D ReadTransform3D(this BinaryReader reader)
    {
        var x = reader.ReadVector3();
        var y = reader.ReadVector3();
        var z = reader.ReadVector3();
        var origin = reader.ReadVector3();
        return new Transform3D(new Basis(x, y, z), origin);
    }
}
