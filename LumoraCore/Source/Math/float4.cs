using System;
using System.Runtime.InteropServices;

namespace Lumora.Core.Math;

/// <summary>
/// 4-component vector of 32-bit floating point values.
/// Pure C# implementation for 4D vector math (LumoraMath).
/// Used for colors (RGBA), quaternions (XYZW), and 4D vectors.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct float4 : IEquatable<float4>
{
    public float x;
    public float y;
    public float z;
    public float w;

    // Property aliases for common use cases
    public float X => x;
    public float Y => y;
    public float Z => z;
    public float W => w;

    public float R => x;
    public float G => y;
    public float B => z;
    public float A => w;

    public static readonly float4 Zero = new float4(0f, 0f, 0f, 0f);
    public static readonly float4 One = new float4(1f, 1f, 1f, 1f);

    // Swizzle properties
    public float3 xyz => new float3(x, y, z);
    public static readonly float4 UnitX = new float4(1f, 0f, 0f, 0f);
    public static readonly float4 UnitY = new float4(0f, 1f, 0f, 0f);
    public static readonly float4 UnitZ = new float4(0f, 0f, 1f, 0f);
    public static readonly float4 UnitW = new float4(0f, 0f, 0f, 1f);

    public float4(float x, float y, float z, float w)
    {
        this.x = x;
        this.y = y;
        this.z = z;
        this.w = w;
    }

    public float4(float value)
    {
        x = y = z = w = value;
    }

    public float4(float2 xy, float z, float w)
    {
        x = xy.x;
        y = xy.y;
        this.z = z;
        this.w = w;
    }

    public float4(float3 xyz, float w)
    {
        x = xyz.x;
        y = xyz.y;
        z = xyz.z;
        this.w = w;
    }

    /// <summary>
    /// Gets or sets the component at the specified index.
    /// </summary>
    public float this[int index]
    {
        get
        {
            return index switch
            {
                0 => x,
                1 => y,
                2 => z,
                3 => w,
                _ => throw new IndexOutOfRangeException()
            };
        }
        set
        {
            switch (index)
            {
                case 0: x = value; break;
                case 1: y = value; break;
                case 2: z = value; break;
                case 3: w = value; break;
                default: throw new IndexOutOfRangeException();
            }
        }
    }

    /// <summary>
    /// Returns the length (magnitude) of this vector.
    /// </summary>
    public float Length => MathF.Sqrt(x * x + y * y + z * z + w * w);

    /// <summary>
    /// Returns the squared length of this vector (faster than Length).
    /// </summary>
    public float LengthSquared => x * x + y * y + z * z + w * w;

    /// <summary>
    /// Returns a normalized copy of this vector.
    /// </summary>
    public float4 Normalized
    {
        get
        {
            float len = Length;
            if (len > 0f)
                return new float4(x / len, y / len, z / len, w / len);
            return Zero;
        }
    }

    /// <summary>
    /// Normalizes this vector in place.
    /// </summary>
    public void Normalize()
    {
        float len = Length;
        if (len > 0f)
        {
            x /= len;
            y /= len;
            z /= len;
            w /= len;
        }
    }

    /// <summary>
    /// Dot product of two vectors.
    /// </summary>
    public static float Dot(float4 a, float4 b)
    {
        return a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
    }

    /// <summary>
    /// Distance between two points.
    /// </summary>
    public static float Distance(float4 a, float4 b)
    {
        return (a - b).Length;
    }

    /// <summary>
    /// Squared distance between two points (faster than Distance).
    /// </summary>
    public static float DistanceSquared(float4 a, float4 b)
    {
        return (a - b).LengthSquared;
    }

    /// <summary>
    /// Linear interpolation between two vectors.
    /// </summary>
    public static float4 Lerp(float4 a, float4 b, float t)
    {
        return a + (b - a) * t;
    }

    // Operators
    public static float4 operator +(float4 a, float4 b) => new float4(a.x + b.x, a.y + b.y, a.z + b.z, a.w + b.w);
    public static float4 operator -(float4 a, float4 b) => new float4(a.x - b.x, a.y - b.y, a.z - b.z, a.w - b.w);
    public static float4 operator *(float4 a, float4 b) => new float4(a.x * b.x, a.y * b.y, a.z * b.z, a.w * b.w);
    public static float4 operator /(float4 a, float4 b) => new float4(a.x / b.x, a.y / b.y, a.z / b.z, a.w / b.w);
    public static float4 operator *(float4 a, float b) => new float4(a.x * b, a.y * b, a.z * b, a.w * b);
    public static float4 operator *(float a, float4 b) => new float4(a * b.x, a * b.y, a * b.z, a * b.w);
    public static float4 operator /(float4 a, float b) => new float4(a.x / b, a.y / b, a.z / b, a.w / b);
    public static float4 operator -(float4 a) => new float4(-a.x, -a.y, -a.z, -a.w);

    public static bool operator ==(float4 a, float4 b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
    public static bool operator !=(float4 a, float4 b) => !(a == b);

    public bool Equals(float4 other) => x == other.x && y == other.y && z == other.z && w == other.w;
    public override bool Equals(object obj) => obj is float4 other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(x, y, z, w);
    public override string ToString() => $"({x}, {y}, {z}, {w})";

    /// <summary>
    /// Conversion to Godot Vector4 (for rendering/platform layer).
    /// </summary>

    /// <summary>
    /// Conversion from Godot Vector4.
    /// </summary>

    /// <summary>
    /// Implicit conversion to Godot Vector4.
    /// </summary>

    /// <summary>
    /// Implicit conversion from Godot Vector4.
    /// </summary>

    /// <summary>
    /// Conversion to Godot Color (for RGBA colors).
    /// </summary>

    /// <summary>
    /// Conversion from Godot Color.
    /// </summary>
}

