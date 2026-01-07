using System;
using System.Runtime.InteropServices;

namespace Lumora.Core.Math;

/// <summary>
/// 2D vector with float components for 2D vector math (LumoraMath)
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct float2 : IEquatable<float2>
{
    public float x;
    public float y;

    public float2(float x, float y)
    {
        this.x = x;
        this.y = y;
    }

    public float2(float value)
    {
        x = y = value;
    }

    // Common constants
    public static readonly float2 Zero = new float2(0f, 0f);
    public static readonly float2 One = new float2(1f, 1f);
    public static readonly float2 Right = new float2(1f, 0f);
    public static readonly float2 Up = new float2(0f, 1f);

    // Properties
    public float Length => MathF.Sqrt(x * x + y * y);
    public float LengthSquared => x * x + y * y;

    public float2 Normalized
    {
        get
        {
            float length = Length;
            return length > float.Epsilon ? this / length : Zero;
        }
    }

    // Methods
    public static float Dot(float2 a, float2 b) => a.x * b.x + a.y * b.y;

    public static float Distance(float2 a, float2 b)
    {
        float dx = a.x - b.x;
        float dy = a.y - b.y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    public static float2 Lerp(float2 a, float2 b, float t)
    {
        t = System.Math.Clamp(t, 0f, 1f);
        return new float2(
            a.x + (b.x - a.x) * t,
            a.y + (b.y - a.y) * t
        );
    }

    public static float2 Min(float2 a, float2 b)
    {
        return new float2(
            MathF.Min(a.x, b.x),
            MathF.Min(a.y, b.y)
        );
    }

    public static float2 Max(float2 a, float2 b)
    {
        return new float2(
            MathF.Max(a.x, b.x),
            MathF.Max(a.y, b.y)
        );
    }

    // Operators
    public static float2 operator +(float2 a, float2 b) => new float2(a.x + b.x, a.y + b.y);
    public static float2 operator -(float2 a, float2 b) => new float2(a.x - b.x, a.y - b.y);
    public static float2 operator *(float2 a, float2 b) => new float2(a.x * b.x, a.y * b.y);
    public static float2 operator /(float2 a, float2 b) => new float2(a.x / b.x, a.y / b.y);
    public static float2 operator *(float2 a, float b) => new float2(a.x * b, a.y * b);
    public static float2 operator /(float2 a, float b) => new float2(a.x / b, a.y / b);
    public static float2 operator -(float2 a) => new float2(-a.x, -a.y);

    // Equality
    public static bool operator ==(float2 a, float2 b) => a.x == b.x && a.y == b.y;
    public static bool operator !=(float2 a, float2 b) => !(a == b);

    public bool Equals(float2 other) => x == other.x && y == other.y;

    public override bool Equals(object obj) => obj is float2 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(x, y);

    public override string ToString() => $"({x}, {y})";

    // Godot conversion


}

