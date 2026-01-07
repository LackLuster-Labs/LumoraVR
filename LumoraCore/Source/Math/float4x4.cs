using System;
using System.Runtime.InteropServices;

namespace Lumora.Core.Math;

/// <summary>
/// 4x4 matrix of 32-bit floating point values.
/// Pure C# implementation for 4x4 matrix math (LumoraMath).
/// Used for transformations, projections, and linear algebra.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct float4x4 : IEquatable<float4x4>
{
    // Column-major storage (matching Godot and OpenGL conventions)
    public float4 c0; // Column 0
    public float4 c1; // Column 1
    public float4 c2; // Column 2
    public float4 c3; // Column 3

    public static readonly float4x4 Identity = new float4x4(
        new float4(1, 0, 0, 0),
        new float4(0, 1, 0, 0),
        new float4(0, 0, 1, 0),
        new float4(0, 0, 0, 1)
    );

    public static readonly float4x4 Zero = new float4x4(
        float4.Zero,
        float4.Zero,
        float4.Zero,
        float4.Zero
    );

    /// <summary>
    /// Construct matrix from four column vectors.
    /// </summary>
    public float4x4(float4 c0, float4 c1, float4 c2, float4 c3)
    {
        this.c0 = c0;
        this.c1 = c1;
        this.c2 = c2;
        this.c3 = c3;
    }

    /// <summary>
    /// Construct matrix from 16 values (column-major order).
    /// </summary>
    public float4x4(
        float m00, float m10, float m20, float m30,
        float m01, float m11, float m21, float m31,
        float m02, float m12, float m22, float m32,
        float m03, float m13, float m23, float m33)
    {
        c0 = new float4(m00, m10, m20, m30);
        c1 = new float4(m01, m11, m21, m31);
        c2 = new float4(m02, m12, m22, m32);
        c3 = new float4(m03, m13, m23, m33);
    }

    /// <summary>
    /// Access matrix by column index.
    /// </summary>
    public float4 this[int column]
    {
        get
        {
            return column switch
            {
                0 => c0,
                1 => c1,
                2 => c2,
                3 => c3,
                _ => throw new IndexOutOfRangeException()
            };
        }
        set
        {
            switch (column)
            {
                case 0: c0 = value; break;
                case 1: c1 = value; break;
                case 2: c2 = value; break;
                case 3: c3 = value; break;
                default: throw new IndexOutOfRangeException();
            }
        }
    }

    /// <summary>
    /// Access matrix by [row, column].
    /// </summary>
    public float this[int row, int column]
    {
        get => this[column][row];
        set
        {
            float4 col = this[column];
            col[row] = value;
            this[column] = col;
        }
    }

    /// <summary>
    /// Create a translation matrix.
    /// </summary>
    public static float4x4 Translate(float3 translation)
    {
        return new float4x4(
            new float4(1, 0, 0, 0),
            new float4(0, 1, 0, 0),
            new float4(0, 0, 1, 0),
            new float4(translation.x, translation.y, translation.z, 1)
        );
    }

    /// <summary>
    /// Create a scale matrix.
    /// </summary>
    public static float4x4 Scale(float3 scale)
    {
        return new float4x4(
            new float4(scale.x, 0, 0, 0),
            new float4(0, scale.y, 0, 0),
            new float4(0, 0, scale.z, 0),
            new float4(0, 0, 0, 1)
        );
    }

    /// <summary>
    /// Create a uniform scale matrix.
    /// </summary>
    public static float4x4 Scale(float scale)
    {
        return Scale(new float3(scale, scale, scale));
    }

    /// <summary>
    /// Create a rotation matrix from a quaternion.
    /// </summary>
    public static float4x4 Rotate(floatQ rotation)
    {
        float x = rotation.x;
        float y = rotation.y;
        float z = rotation.z;
        float w = rotation.w;

        float x2 = x + x;
        float y2 = y + y;
        float z2 = z + z;

        float xx = x * x2;
        float yy = y * y2;
        float zz = z * z2;
        float xy = x * y2;
        float xz = x * z2;
        float yz = y * z2;
        float wx = w * x2;
        float wy = w * y2;
        float wz = w * z2;

        return new float4x4(
            new float4(1 - (yy + zz), xy + wz, xz - wy, 0),
            new float4(xy - wz, 1 - (xx + zz), yz + wx, 0),
            new float4(xz + wy, yz - wx, 1 - (xx + yy), 0),
            new float4(0, 0, 0, 1)
        );
    }

    /// <summary>
    /// Create a TRS (Translation, Rotation, Scale) matrix.
    /// </summary>
    public static float4x4 TRS(float3 translation, floatQ rotation, float3 scale)
    {
        return Translate(translation) * Rotate(rotation) * Scale(scale);
    }

    /// <summary>
    /// Matrix multiplication.
    /// </summary>
    public static float4x4 operator *(float4x4 a, float4x4 b)
    {
        float4x4 result = default;
        for (int col = 0; col < 4; col++)
        {
            for (int row = 0; row < 4; row++)
            {
                result[row, col] =
                    a[row, 0] * b[0, col] +
                    a[row, 1] * b[1, col] +
                    a[row, 2] * b[2, col] +
                    a[row, 3] * b[3, col];
            }
        }
        return result;
    }

    /// <summary>
    /// Transform a float4 by this matrix.
    /// </summary>
    public static float4 operator *(float4x4 m, float4 v)
    {
        return new float4(
            m[0, 0] * v.x + m[0, 1] * v.y + m[0, 2] * v.z + m[0, 3] * v.w,
            m[1, 0] * v.x + m[1, 1] * v.y + m[1, 2] * v.z + m[1, 3] * v.w,
            m[2, 0] * v.x + m[2, 1] * v.y + m[2, 2] * v.z + m[2, 3] * v.w,
            m[3, 0] * v.x + m[3, 1] * v.y + m[3, 2] * v.z + m[3, 3] * v.w
        );
    }

    /// <summary>
    /// Transform a float3 position by this matrix (w=1).
    /// </summary>
    public float3 MultiplyPoint(float3 point)
    {
        float4 v = this * new float4(point.x, point.y, point.z, 1);
        return new float3(v.x / v.w, v.y / v.w, v.z / v.w);
    }

    /// <summary>
    /// Transform a float3 position by this matrix (w=1) - ref version.
    /// </summary>
    public float3 MultiplyPoint(in float3 point)
    {
        float4 v = this * new float4(point.x, point.y, point.z, 1);
        return new float3(v.x / v.w, v.y / v.w, v.z / v.w);
    }

    /// <summary>
    /// Transform a float3 direction by this matrix (w=0).
    /// </summary>
    public float3 MultiplyVector(float3 vector)
    {
        float4 v = this * new float4(vector.x, vector.y, vector.z, 0);
        return new float3(v.x, v.y, v.z);
    }

    /// <summary>
    /// Transform a float3 direction by this matrix (w=0) - ref version.
    /// </summary>
    public float3 MultiplyVector(in float3 vector)
    {
        float4 v = this * new float4(vector.x, vector.y, vector.z, 0);
        return new float3(v.x, v.y, v.z);
    }

    /// <summary>
    /// Returns the transpose of this matrix.
    /// </summary>
    public float4x4 Transposed
    {
        get
        {
            return new float4x4(
                new float4(c0.x, c1.x, c2.x, c3.x),
                new float4(c0.y, c1.y, c2.y, c3.y),
                new float4(c0.z, c1.z, c2.z, c3.z),
                new float4(c0.w, c1.w, c2.w, c3.w)
            );
        }
    }

    /// <summary>
    /// Returns the determinant of this matrix.
    /// </summary>
    public float Determinant
    {
        get
        {
            float a = c0.x, b = c1.x, c = c2.x, d = c3.x;
            float e = c0.y, f = c1.y, g = c2.y, h = c3.y;
            float i = c0.z, j = c1.z, k = c2.z, l = c3.z;
            float m = c0.w, n = c1.w, o = c2.w, p = c3.w;

            float kp_lo = k * p - l * o;
            float jp_ln = j * p - l * n;
            float jo_kn = j * o - k * n;
            float ip_lm = i * p - l * m;
            float io_km = i * o - k * m;
            float in_jm = i * n - j * m;

            return a * (f * kp_lo - g * jp_ln + h * jo_kn) -
                   b * (e * kp_lo - g * ip_lm + h * io_km) +
                   c * (e * jp_ln - f * ip_lm + h * in_jm) -
                   d * (e * jo_kn - f * io_km + g * in_jm);
        }
    }

    /// <summary>
    /// Returns the inverse of this matrix using affine transformation optimization.
    /// Assumes the matrix is an affine transformation (bottom row is 0,0,0,1).
    /// </summary>
    public float4x4 Inverse
    {
        get
        {
            // Extract the 3x3 rotation/scale part
            float m00 = c0.x, m01 = c1.x, m02 = c2.x;
            float m10 = c0.y, m11 = c1.y, m12 = c2.y;
            float m20 = c0.z, m21 = c1.z, m22 = c2.z;

            // Calculate determinant of 3x3 matrix
            float det = m00 * (m11 * m22 - m12 * m21)
                      - m01 * (m10 * m22 - m12 * m20)
                      + m02 * (m10 * m21 - m11 * m20);

            if (System.Math.Abs(det) < 1e-10f)
                return Identity; // Return identity if matrix is singular

            float invDet = 1.0f / det;

            // Calculate inverse of 3x3 rotation/scale part
            float i00 = (m11 * m22 - m12 * m21) * invDet;
            float i01 = (m02 * m21 - m01 * m22) * invDet;
            float i02 = (m01 * m12 - m02 * m11) * invDet;
            float i10 = (m12 * m20 - m10 * m22) * invDet;
            float i11 = (m00 * m22 - m02 * m20) * invDet;
            float i12 = (m02 * m10 - m00 * m12) * invDet;
            float i20 = (m10 * m21 - m11 * m20) * invDet;
            float i21 = (m01 * m20 - m00 * m21) * invDet;
            float i22 = (m00 * m11 - m01 * m10) * invDet;

            // Calculate inverse translation: -inv(R) * T
            float tx = c3.x, ty = c3.y, tz = c3.z;
            float i03 = -(i00 * tx + i01 * ty + i02 * tz);
            float i13 = -(i10 * tx + i11 * ty + i12 * tz);
            float i23 = -(i20 * tx + i21 * ty + i22 * tz);

            return new float4x4(
                new float4(i00, i10, i20, 0),
                new float4(i01, i11, i21, 0),
                new float4(i02, i12, i22, 0),
                new float4(i03, i13, i23, 1)
            );
        }
    }

    public static bool operator ==(float4x4 a, float4x4 b) =>
        a.c0 == b.c0 && a.c1 == b.c1 && a.c2 == b.c2 && a.c3 == b.c3;

    public static bool operator !=(float4x4 a, float4x4 b) => !(a == b);

    public bool Equals(float4x4 other) =>
        c0.Equals(other.c0) && c1.Equals(other.c1) && c2.Equals(other.c2) && c3.Equals(other.c3);

    public override bool Equals(object obj) => obj is float4x4 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(c0, c1, c2, c3);

    public override string ToString() =>
        $"[{c0.x:F3}, {c1.x:F3}, {c2.x:F3}, {c3.x:F3}]\n" +
        $"[{c0.y:F3}, {c1.y:F3}, {c2.y:F3}, {c3.y:F3}]\n" +
        $"[{c0.z:F3}, {c1.z:F3}, {c2.z:F3}, {c3.z:F3}]\n" +
        $"[{c0.w:F3}, {c1.w:F3}, {c2.w:F3}, {c3.w:F3}]";
}
