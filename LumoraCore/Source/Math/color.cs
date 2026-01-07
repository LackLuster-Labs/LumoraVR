using System;
using System.Runtime.InteropServices;

namespace Lumora.Core.Math;

/// <summary>
/// RGBA color with 32-bit floating point components.
/// Pure C# implementation for color representation (LumoraMath).
/// Variant-compatible via implicit Godot.Color conversion.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct color : IEquatable<color>
{
    public float r;
    public float g;
    public float b;
    public float a;

    public static readonly color White = new color(1f, 1f, 1f, 1f);
    public static readonly color Black = new color(0f, 0f, 0f, 1f);
    public static readonly color Red = new color(1f, 0f, 0f, 1f);
    public static readonly color Green = new color(0f, 1f, 0f, 1f);
    public static readonly color Blue = new color(0f, 0f, 1f, 1f);
    public static readonly color Yellow = new color(1f, 1f, 0f, 1f);
    public static readonly color Cyan = new color(0f, 1f, 1f, 1f);
    public static readonly color Magenta = new color(1f, 0f, 1f, 1f);
    public static readonly color Transparent = new color(0f, 0f, 0f, 0f);

    public color(float r, float g, float b, float a = 1f)
    {
        this.r = r;
        this.g = g;
        this.b = b;
        this.a = a;
    }

    public color(float value, float a = 1f)
    {
        r = g = b = value;
        this.a = a;
    }

    /// <summary>
    /// Gets or sets the component at the specified index.
    /// 0=r, 1=g, 2=b, 3=a
    /// </summary>
    public float this[int index]
    {
        get
        {
            return index switch
            {
                0 => r,
                1 => g,
                2 => b,
                3 => a,
                _ => throw new IndexOutOfRangeException()
            };
        }
        set
        {
            switch (index)
            {
                case 0: r = value; break;
                case 1: g = value; break;
                case 2: b = value; break;
                case 3: a = value; break;
                default: throw new IndexOutOfRangeException();
            }
        }
    }

    /// <summary>
    /// Creates a color from HSV (Hue, Saturation, Value).
    /// </summary>
    /// <param name="h">Hue (0-1)</param>
    /// <param name="s">Saturation (0-1)</param>
    /// <param name="v">Value (0-1)</param>
    /// <param name="a">Alpha (0-1)</param>
    public static color FromHSV(float h, float s, float v, float a = 1f)
    {
        h = (h % 1f + 1f) % 1f; // Wrap to 0-1
        s = MathF.Max(0f, MathF.Min(1f, s));
        v = MathF.Max(0f, MathF.Min(1f, v));

        if (s == 0f)
            return new color(v, v, v, a);

        h *= 6f;
        int i = (int)h;
        float f = h - i;
        float p = v * (1f - s);
        float q = v * (1f - s * f);
        float t = v * (1f - s * (1f - f));

        return (i % 6) switch
        {
            0 => new color(v, t, p, a),
            1 => new color(q, v, p, a),
            2 => new color(p, v, t, a),
            3 => new color(p, q, v, a),
            4 => new color(t, p, v, a),
            _ => new color(v, p, q, a),
        };
    }

    /// <summary>
    /// Converts this color to HSV (Hue, Saturation, Value).
    /// </summary>
    /// <returns>(h, s, v) where each component is 0-1</returns>
    public (float h, float s, float v) ToHSV()
    {
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float delta = max - min;

        float h = 0f;
        float s = max == 0f ? 0f : delta / max;
        float v = max;

        if (delta != 0f)
        {
            if (max == r)
                h = (g - b) / delta + (g < b ? 6f : 0f);
            else if (max == g)
                h = (b - r) / delta + 2f;
            else
                h = (r - g) / delta + 4f;

            h /= 6f;
        }

        return (h, s, v);
    }

    /// <summary>
    /// Creates a color from a hex string (e.g., "#RRGGBB" or "#RRGGBBAA").
    /// </summary>
    public static color FromHex(string hex)
    {
        hex = hex.TrimStart('#');

        if (hex.Length == 6)
        {
            return new color(
                Convert.ToInt32(hex.Substring(0, 2), 16) / 255f,
                Convert.ToInt32(hex.Substring(2, 2), 16) / 255f,
                Convert.ToInt32(hex.Substring(4, 2), 16) / 255f,
                1f
            );
        }
        else if (hex.Length == 8)
        {
            return new color(
                Convert.ToInt32(hex.Substring(0, 2), 16) / 255f,
                Convert.ToInt32(hex.Substring(2, 2), 16) / 255f,
                Convert.ToInt32(hex.Substring(4, 2), 16) / 255f,
                Convert.ToInt32(hex.Substring(6, 2), 16) / 255f
            );
        }

        throw new ArgumentException("Invalid hex color format");
    }

    /// <summary>
    /// Converts this color to a hex string (e.g., "#RRGGBBAA").
    /// </summary>
    public string ToHex()
    {
        int rByte = (int)(MathF.Max(0f, MathF.Min(1f, r)) * 255f);
        int gByte = (int)(MathF.Max(0f, MathF.Min(1f, g)) * 255f);
        int bByte = (int)(MathF.Max(0f, MathF.Min(1f, b)) * 255f);
        int aByte = (int)(MathF.Max(0f, MathF.Min(1f, a)) * 255f);
        return $"#{rByte:X2}{gByte:X2}{bByte:X2}{aByte:X2}";
    }

    /// <summary>
    /// Linear interpolation between two colors.
    /// </summary>
    public static color Lerp(color a, color b, float t)
    {
        return new color(
            a.r + (b.r - a.r) * t,
            a.g + (b.g - a.g) * t,
            a.b + (b.b - a.b) * t,
            a.a + (b.a - a.a) * t
        );
    }

    /// <summary>
    /// Returns the grayscale value (luminance) of this color.
    /// </summary>
    public float Grayscale => 0.299f * r + 0.587f * g + 0.114f * b;

    // Operators
    public static color operator +(color a, color b) => new color(a.r + b.r, a.g + b.g, a.b + b.b, a.a + b.a);
    public static color operator -(color a, color b) => new color(a.r - b.r, a.g - b.g, a.b - b.b, a.a - b.a);
    public static color operator *(color a, color b) => new color(a.r * b.r, a.g * b.g, a.b * b.b, a.a * b.a);
    public static color operator /(color a, color b) => new color(a.r / b.r, a.g / b.g, a.b / b.b, a.a / b.a);
    public static color operator *(color a, float b) => new color(a.r * b, a.g * b, a.b * b, a.a * b);
    public static color operator *(float a, color b) => new color(a * b.r, a * b.g, a * b.b, a * b.a);
    public static color operator /(color a, float b) => new color(a.r / b, a.g / b, a.b / b, a.a / b);

    public static bool operator ==(color a, color b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
    public static bool operator !=(color a, color b) => !(a == b);

    public bool Equals(color other) => r == other.r && g == other.g && b == other.b && a == other.a;
    public override bool Equals(object obj) => obj is color other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(r, g, b, a);
    public override string ToString() => $"({r:F3}, {g:F3}, {b:F3}, {a:F3})";

    /// <summary>
    /// Conversion to Godot Color (for rendering/platform layer).
    /// </summary>

    /// <summary>
    /// Conversion from Godot Color.
    /// </summary>

    /// <summary>
    /// Implicit conversion to Godot Color.
    /// </summary>

    /// <summary>
    /// Implicit conversion from Godot Color.
    /// </summary>
}

