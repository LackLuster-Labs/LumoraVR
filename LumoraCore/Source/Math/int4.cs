using System;
using System.Runtime.InteropServices;

namespace Lumora.Core.Math;

/// <summary>
/// A 4-component integer vector.
/// Used for bone indices in skeletal animation (4 bones per vertex).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct int4 : IEquatable<int4>
{
    public int x;
    public int y;
    public int z;
    public int w;

    // ===== CONSTRUCTORS =====

    public int4(int x, int y, int z, int w)
    {
        this.x = x;
        this.y = y;
        this.z = z;
        this.w = w;
    }

    public int4(int value)
    {
        x = y = z = w = value;
    }

    // ===== CONSTANTS =====

    public static int4 Zero => new int4(0, 0, 0, 0);
    public static int4 One => new int4(1, 1, 1, 1);

    // ===== INDEXER =====

    public int this[int index]
    {
        get
        {
            return index switch
            {
                0 => x,
                1 => y,
                2 => z,
                3 => w,
                _ => throw new IndexOutOfRangeException($"Invalid int4 index: {index}")
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
                default: throw new IndexOutOfRangeException($"Invalid int4 index: {index}");
            }
        }
    }

    // ===== OPERATORS =====

    public static int4 operator +(int4 a, int4 b) => new int4(a.x + b.x, a.y + b.y, a.z + b.z, a.w + b.w);
    public static int4 operator -(int4 a, int4 b) => new int4(a.x - b.x, a.y - b.y, a.z - b.z, a.w - b.w);
    public static int4 operator *(int4 a, int b) => new int4(a.x * b, a.y * b, a.z * b, a.w * b);
    public static int4 operator *(int a, int4 b) => new int4(a * b.x, a * b.y, a * b.z, a * b.w);
    public static int4 operator /(int4 a, int b) => new int4(a.x / b, a.y / b, a.z / b, a.w / b);

    public static bool operator ==(int4 a, int4 b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
    public static bool operator !=(int4 a, int4 b) => !(a == b);

    // ===== OBJECT OVERRIDES =====

    public override bool Equals(object? obj) => obj is int4 other && Equals(other);
    public bool Equals(int4 other) => x == other.x && y == other.y && z == other.z && w == other.w;
    public override int GetHashCode() => HashCode.Combine(x, y, z, w);
    public override string ToString() => $"int4({x}, {y}, {z}, {w})";
}
