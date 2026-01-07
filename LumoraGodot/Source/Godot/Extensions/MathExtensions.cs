using Godot;
using Lumora.Core.Math;

namespace Aquamarine.Godot.Extensions;

/// <summary>
/// Extension methods for converting between Lumora.Core.Math types and Godot types.
/// Math conversion utilities for Godot.
/// </summary>
public static class MathExtensions
{
    // ===== float3 conversions =====

    public static Vector3 ToGodot(this float3 v)
    {
        return new Vector3(v.x, v.y, v.z);
    }

    public static float3 ToLumora(this Vector3 v)
    {
        return new float3(v.X, v.Y, v.Z);
    }

    // ===== float2 conversions =====

    public static Vector2 ToGodot(this float2 v)
    {
        return new Vector2(v.x, v.y);
    }

    public static float2 ToLumora(this Vector2 v)
    {
        return new float2(v.X, v.Y);
    }

    // ===== floatQ (quaternion) conversions =====

    public static Quaternion ToGodot(this floatQ q)
    {
        return new Quaternion(q.x, q.y, q.z, q.w);
    }

    public static floatQ ToLumora(this Quaternion q)
    {
        return new floatQ(q.X, q.Y, q.Z, q.W);
    }

    // ===== float4x4 (matrix) conversions =====

    public static Transform3D ToGodot(this float4x4 m)
    {
        // float4x4 is column-major (c0, c1, c2, c3), Godot Basis is also column-major
        var basis = new Basis(
            new Vector3(m.c0.x, m.c0.y, m.c0.z), // Column 0
            new Vector3(m.c1.x, m.c1.y, m.c1.z), // Column 1
            new Vector3(m.c2.x, m.c2.y, m.c2.z)  // Column 2
        );

        var origin = new Vector3(m.c3.x, m.c3.y, m.c3.z);

        return new Transform3D(basis, origin);
    }

    public static float4x4 ToLumora(this Transform3D t)
    {
        var b = t.Basis;
        var o = t.Origin;

        return new float4x4(
            b.X.X, b.Y.X, b.Z.X, o.X,  // Row 0
            b.X.Y, b.Y.Y, b.Z.Y, o.Y,  // Row 1
            b.X.Z, b.Y.Z, b.Z.Z, o.Z,  // Row 2
            0f, 0f, 0f, 1f             // Row 3
        );
    }

    // ===== color conversions =====

    public static Color ToGodot(this color c)
    {
        return new Color(c.r, c.g, c.b, c.a);
    }

    public static color ToLumora(this Color c)
    {
        return new color(c.R, c.G, c.B, c.A);
    }

    // ===== float4 conversions (used for colors/vectors) =====

    public static Vector4 ToGodot(this float4 v)
    {
        return new Vector4(v.x, v.y, v.z, v.w);
    }

    public static float4 ToLumora(this Vector4 v)
    {
        return new float4(v.X, v.Y, v.Z, v.W);
    }
}
