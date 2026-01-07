using System;
using System.Runtime.InteropServices;

namespace Lumora.Core.Math;

/// <summary>
/// Quaternion using 32-bit floating point components.
/// Pure C# implementation for quaternion math (LumoraMath).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct floatQ : IEquatable<floatQ>
{
    public float x;
    public float y;
    public float z;
    public float w;

    public static readonly floatQ Identity = new floatQ(0f, 0f, 0f, 1f);

    public floatQ(float x, float y, float z, float w)
    {
        this.x = x;
        this.y = y;
        this.z = z;
        this.w = w;
    }

    /// <summary>
    /// Creates a quaternion from axis and angle (in radians).
    /// </summary>
    public static floatQ AxisAngle(float3 axis, float angle)
    {
        float halfAngle = angle * 0.5f;
        float s = MathF.Sin(halfAngle);
        float c = MathF.Cos(halfAngle);

        axis.Normalize();
        return new floatQ(
            axis.x * s,
            axis.y * s,
            axis.z * s,
            c
        );
    }

    /// <summary>
    /// Create rotation from axis and angle in radians (alias for AxisAngle).
    /// </summary>
    public static floatQ AxisAngleRad(float3 axis, float angle)
    {
        return AxisAngle(axis, angle);
    }

    /// <summary>
    /// Creates a quaternion from Euler angles (in radians).
    /// Order: YXZ (Yaw, Pitch, Roll)
    /// </summary>
    public static floatQ Euler(float yaw, float pitch, float roll)
    {
        float cy = MathF.Cos(yaw * 0.5f);
        float sy = MathF.Sin(yaw * 0.5f);
        float cp = MathF.Cos(pitch * 0.5f);
        float sp = MathF.Sin(pitch * 0.5f);
        float cr = MathF.Cos(roll * 0.5f);
        float sr = MathF.Sin(roll * 0.5f);

        return new floatQ(
            cy * sp * cr + sy * cp * sr,
            sy * cp * cr - cy * sp * sr,
            cy * cp * sr - sy * sp * cr,
            cy * cp * cr + sy * sp * sr
        );
    }

    /// <summary>
    /// Creates a quaternion from Euler angles (in radians).
    /// </summary>
    public static floatQ Euler(float3 eulerAngles)
    {
        return Euler(eulerAngles.y, eulerAngles.x, eulerAngles.z);
    }

    /// <summary>
    /// Alias for Euler(float3) for standard quaternion operations.
    /// </summary>
    public static floatQ FromEuler(float3 eulerAngles)
    {
        return Euler(eulerAngles);
    }

    /// <summary>
    /// Converts this quaternion to Euler angles (in radians).
    /// </summary>
    public float3 ToEuler()
    {
        float3 euler;

        // Pitch (x-axis rotation)
        float sinp = 2f * (w * x - z * y);
        if (MathF.Abs(sinp) >= 1f)
            euler.x = MathF.CopySign(MathF.PI / 2f, sinp);
        else
            euler.x = MathF.Asin(sinp);

        // Yaw (y-axis rotation)
        float siny_cosp = 2f * (w * y + z * x);
        float cosy_cosp = 1f - 2f * (x * x + y * y);
        euler.y = MathF.Atan2(siny_cosp, cosy_cosp);

        // Roll (z-axis rotation)
        float sinr_cosp = 2f * (w * z + x * y);
        float cosr_cosp = 1f - 2f * (y * y + z * z);
        euler.z = MathF.Atan2(sinr_cosp, cosr_cosp);

        return euler;
    }

    /// <summary>
    /// Creates a quaternion that rotates from one direction to another.
    /// </summary>
    public static floatQ LookRotation(float3 forward, float3 up)
    {
        forward.Normalize();
        float3 right = float3.Cross(up, forward).Normalized;
        up = float3.Cross(forward, right);

        float m00 = right.x;
        float m01 = right.y;
        float m02 = right.z;
        float m10 = up.x;
        float m11 = up.y;
        float m12 = up.z;
        float m20 = forward.x;
        float m21 = forward.y;
        float m22 = forward.z;

        float trace = m00 + m11 + m22;
        floatQ q = new floatQ();

        if (trace > 0f)
        {
            float s = MathF.Sqrt(trace + 1f) * 2f;
            q.w = 0.25f * s;
            q.x = (m21 - m12) / s;
            q.y = (m02 - m20) / s;
            q.z = (m10 - m01) / s;
        }
        else if (m00 > m11 && m00 > m22)
        {
            float s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f;
            q.w = (m21 - m12) / s;
            q.x = 0.25f * s;
            q.y = (m01 + m10) / s;
            q.z = (m02 + m20) / s;
        }
        else if (m11 > m22)
        {
            float s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f;
            q.w = (m02 - m20) / s;
            q.x = (m01 + m10) / s;
            q.y = 0.25f * s;
            q.z = (m12 + m21) / s;
        }
        else
        {
            float s = MathF.Sqrt(1f + m22 - m00 - m11) * 2f;
            q.w = (m10 - m01) / s;
            q.x = (m02 + m20) / s;
            q.y = (m12 + m21) / s;
            q.z = 0.25f * s;
        }

        return q;
    }

    /// <summary>
    /// Returns the length (magnitude) of this quaternion.
    /// </summary>
    public float Length => MathF.Sqrt(x * x + y * y + z * z + w * w);

    /// <summary>
    /// Returns the squared length of this quaternion.
    /// </summary>
    public float LengthSquared => x * x + y * y + z * z + w * w;

    /// <summary>
    /// Returns a normalized copy of this quaternion.
    /// </summary>
    public floatQ Normalized
    {
        get
        {
            float len = Length;
            if (len > 0f)
                return new floatQ(x / len, y / len, z / len, w / len);
            return Identity;
        }
    }

    /// <summary>
    /// Normalizes this quaternion in place.
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
    /// Returns the conjugate of this quaternion.
    /// </summary>
    public floatQ Conjugate => new floatQ(-x, -y, -z, w);

    /// <summary>
    /// Returns the inverse of this quaternion.
    /// </summary>
    public floatQ Inverse
    {
        get
        {
            float lengthSq = LengthSquared;
            if (lengthSq > 0f)
            {
                float invLengthSq = 1f / lengthSq;
                return new floatQ(-x * invLengthSq, -y * invLengthSq, -z * invLengthSq, w * invLengthSq);
            }
            return Identity;
        }
    }

    /// <summary>
    /// Dot product of two quaternions.
    /// </summary>
    public static float Dot(floatQ a, floatQ b)
    {
        return a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
    }

    /// <summary>
    /// Spherical linear interpolation between two quaternions.
    /// </summary>
    public static floatQ Slerp(floatQ a, floatQ b, float t)
    {
        float dot = Dot(a, b);

        // If the dot product is negative, negate one quaternion to take the shorter path
        if (dot < 0f)
        {
            b = new floatQ(-b.x, -b.y, -b.z, -b.w);
            dot = -dot;
        }

        const float threshold = 0.9995f;
        if (dot > threshold)
        {
            // Linear interpolation for very close quaternions
            return new floatQ(
                a.x + t * (b.x - a.x),
                a.y + t * (b.y - a.y),
                a.z + t * (b.z - a.z),
                a.w + t * (b.w - a.w)
            ).Normalized;
        }

        float theta = MathF.Acos(dot);
        float sinTheta = MathF.Sin(theta);
        float wa = MathF.Sin((1f - t) * theta) / sinTheta;
        float wb = MathF.Sin(t * theta) / sinTheta;

        return new floatQ(
            wa * a.x + wb * b.x,
            wa * a.y + wb * b.y,
            wa * a.z + wb * b.z,
            wa * a.w + wb * b.w
        );
    }

    /// <summary>
    /// Rotates a vector by this quaternion.
    /// </summary>
    public float3 Rotate(float3 v)
    {
        float3 qVec = new float3(x, y, z);
        float3 cross1 = float3.Cross(qVec, v);
        float3 cross2 = float3.Cross(qVec, cross1);
        return v + 2f * (cross1 * w + cross2);
    }

    // Operators
    public static floatQ operator *(floatQ a, floatQ b)
    {
        return new floatQ(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y + a.y * b.w + a.z * b.x - a.x * b.z,
            a.w * b.z + a.z * b.w + a.x * b.y - a.y * b.x,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z
        );
    }

    public static float3 operator *(floatQ q, float3 v) => q.Rotate(v);

    public static bool operator ==(floatQ a, floatQ b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
    public static bool operator !=(floatQ a, floatQ b) => !(a == b);

    public bool Equals(floatQ other) => x == other.x && y == other.y && z == other.z && w == other.w;
    public override bool Equals(object obj) => obj is floatQ other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(x, y, z, w);
    public override string ToString() => $"({x}, {y}, {z}, {w})";

    /// <summary>
    /// Conversion to Godot Quaternion (for rendering/platform layer).
    /// </summary>

    /// <summary>
    /// Conversion from Godot Quaternion.
    /// </summary>

    /// <summary>
    /// Implicit conversion to Godot Quaternion.
    /// </summary>

    /// <summary>
    /// Implicit conversion from Godot Quaternion.
    /// </summary>
}

