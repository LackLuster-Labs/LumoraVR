using System;
using System.Runtime.InteropServices;

namespace Lumora.Core.Math;

/// <summary>
/// 3-component vector of 32-bit floating point values.
/// Pure C# implementation for 3D vector math (LumoraMath).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct float3 : IEquatable<float3>
{
    public float x;
    public float y;
    public float z;

    public static readonly float3 Zero = new float3(0f, 0f, 0f);
    public static readonly float3 One = new float3(1f, 1f, 1f);

    // Direction constants
    public static readonly float3 Forward = new float3(0f, 0f, 1f);
    public static readonly float3 Backward = new float3(0f, 0f, -1f);
    public static readonly float3 Up = new float3(0f, 1f, 0f);
    public static readonly float3 Down = new float3(0f, -1f, 0f);
    public static readonly float3 Right = new float3(1f, 0f, 0f);
    public static readonly float3 Left = new float3(-1f, 0f, 0f);

    public float3(float x, float y, float z)
    {
        this.x = x;
        this.y = y;
        this.z = z;
    }

    public float3(float value)
    {
        x = y = z = value;
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
                default: throw new IndexOutOfRangeException();
            }
        }
    }

    /// <summary>
    /// Returns the length (magnitude) of this vector.
    /// </summary>
    public float Length => MathF.Sqrt(x * x + y * y + z * z);

    /// <summary>
    /// Returns the squared length of this vector (faster than Length).
    /// </summary>
    public float LengthSquared => x * x + y * y + z * z;

    /// <summary>
    /// Returns a normalized copy of this vector.
    /// </summary>
    public float3 Normalized
    {
        get
        {
            float len = Length;
            if (len > 0f)
                return new float3(x / len, y / len, z / len);
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
        }
    }

    /// <summary>
    /// Dot product of two vectors.
    /// </summary>
    public static float Dot(float3 a, float3 b)
    {
        return a.x * b.x + a.y * b.y + a.z * b.z;
    }

    /// <summary>
    /// Cross product of two vectors.
    /// </summary>
    public static float3 Cross(float3 a, float3 b)
    {
        return new float3(
            a.y * b.z - a.z * b.y,
            a.z * b.x - a.x * b.z,
            a.x * b.y - a.y * b.x
        );
    }

    /// <summary>
    /// Distance between two points.
    /// </summary>
    public static float Distance(float3 a, float3 b)
    {
        return (a - b).Length;
    }

    /// <summary>
    /// Squared distance between two points (faster than Distance).
    /// </summary>
    public static float DistanceSquared(float3 a, float3 b)
    {
        return (a - b).LengthSquared;
    }

    /// <summary>
    /// Linear interpolation between two vectors.
    /// </summary>
    public static float3 Lerp(float3 a, float3 b, float t)
    {
        return a + (b - a) * t;
    }

    /// <summary>
    /// Reflects a vector off a surface with the given normal.
    /// </summary>
    public static float3 Reflect(float3 vector, float3 normal)
    {
        return vector - 2f * Dot(vector, normal) * normal;
    }

    /// <summary>
    /// Projects a vector onto another vector.
    /// </summary>
    public static float3 Project(float3 vector, float3 onNormal)
    {
        float sqrMag = Dot(onNormal, onNormal);
        if (sqrMag < float.Epsilon)
            return Zero;
        return onNormal * Dot(vector, onNormal) / sqrMag;
    }

    /// <summary>
    /// Returns the angle in radians between two vectors.
    /// </summary>
    public static float Angle(float3 a, float3 b)
    {
        float denominator = MathF.Sqrt(a.LengthSquared * b.LengthSquared);
        if (denominator < float.Epsilon)
            return 0f;
        float dot = System.Math.Clamp(Dot(a, b) / denominator, -1f, 1f);
        return MathF.Acos(dot);
    }

    // Operators
    public static float3 operator +(float3 a, float3 b) => new float3(a.x + b.x, a.y + b.y, a.z + b.z);
    public static float3 operator -(float3 a, float3 b) => new float3(a.x - b.x, a.y - b.y, a.z - b.z);
    public static float3 operator *(float3 a, float3 b) => new float3(a.x * b.x, a.y * b.y, a.z * b.z);
    public static float3 operator /(float3 a, float3 b) => new float3(a.x / b.x, a.y / b.y, a.z / b.z);
    public static float3 operator *(float3 a, float b) => new float3(a.x * b, a.y * b, a.z * b);
    public static float3 operator *(float a, float3 b) => new float3(a * b.x, a * b.y, a * b.z);
    public static float3 operator /(float3 a, float b) => new float3(a.x / b, a.y / b, a.z / b);
    public static float3 operator -(float3 a) => new float3(-a.x, -a.y, -a.z);

    public static bool operator ==(float3 a, float3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
    public static bool operator !=(float3 a, float3 b) => !(a == b);

    public bool Equals(float3 other) => x == other.x && y == other.y && z == other.z;
    public override bool Equals(object obj) => obj is float3 other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(x, y, z);
    public override string ToString() => $"({x}, {y}, {z})";

    /// <summary>
    /// Conversion to Godot Vector3 (for rendering/platform layer).
    /// </summary>

    /// <summary>
    /// Conversion from Godot Vector3.
    /// </summary>

    /// <summary>
    /// Implicit conversion to Godot Vector3.
    /// </summary>

    /// <summary>
    /// Implicit conversion from Godot Vector3.
    /// </summary>
}

