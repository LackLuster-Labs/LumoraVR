using System;
using System.Runtime.InteropServices;

namespace Lumora.Core.Math;

/// <summary>
/// RGBA color with 32-bit floating point components supporting High Dynamic Range (HDR).
/// RGB components can exceed 1.0 to represent brighter-than-white colors.
/// Pure C# implementation for HDR color representation (LumoraMath).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct colorHDR : IEquatable<colorHDR>
{
    public float r;
    public float g;
    public float b;
    public float a;

    public static readonly colorHDR White = new colorHDR(1f, 1f, 1f, 1f);
    public static readonly colorHDR Black = new colorHDR(0f, 0f, 0f, 1f);
    public static readonly colorHDR Red = new colorHDR(1f, 0f, 0f, 1f);
    public static readonly colorHDR Green = new colorHDR(0f, 1f, 0f, 1f);
    public static readonly colorHDR Blue = new colorHDR(0f, 0f, 1f, 1f);
    public static readonly colorHDR Yellow = new colorHDR(1f, 1f, 0f, 1f);
    public static readonly colorHDR Cyan = new colorHDR(0f, 1f, 1f, 1f);
    public static readonly colorHDR Magenta = new colorHDR(1f, 0f, 1f, 1f);
    public static readonly colorHDR Transparent = new colorHDR(0f, 0f, 0f, 0f);

    public colorHDR(float r, float g, float b, float a = 1f)
    {
        this.r = r;
        this.g = g;
        this.b = b;
        this.a = a;
    }

    public colorHDR(float value, float a = 1f)
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
    /// Creates an HDR color from HSV (Hue, Saturation, Value).
    /// Value can exceed 1.0 for HDR brightness.
    /// </summary>
    /// <param name="h">Hue (0-1)</param>
    /// <param name="s">Saturation (0-1)</param>
    /// <param name="v">Value (0+), can exceed 1.0 for HDR</param>
    /// <param name="a">Alpha (0-1)</param>
    public static colorHDR FromHSV(float h, float s, float v, float a = 1f)
    {
        h = (h % 1f + 1f) % 1f; // Wrap to 0-1
        s = MathF.Max(0f, MathF.Min(1f, s));
        v = MathF.Max(0f, v); // Allow values > 1.0 for HDR

        if (s == 0f)
            return new colorHDR(v, v, v, a);

        h *= 6f;
        int i = (int)h;
        float f = h - i;
        float p = v * (1f - s);
        float q = v * (1f - s * f);
        float t = v * (1f - s * (1f - f));

        return (i % 6) switch
        {
            0 => new colorHDR(v, t, p, a),
            1 => new colorHDR(q, v, p, a),
            2 => new colorHDR(p, v, t, a),
            3 => new colorHDR(p, q, v, a),
            4 => new colorHDR(t, p, v, a),
            _ => new colorHDR(v, p, q, a),
        };
    }

    /// <summary>
    /// Converts this HDR color to HSV (Hue, Saturation, Value).
    /// Value may exceed 1.0 for HDR colors.
    /// </summary>
    /// <returns>(h, s, v) where h and s are 0-1, v can exceed 1.0</returns>
    public (float h, float s, float v) ToHSV()
    {
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float delta = max - min;

        float h = 0f;
        float s = max == 0f ? 0f : delta / max;
        float v = max; // Can exceed 1.0 for HDR

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
    /// Creates an HDR color from a hex string (e.g., "#RRGGBB" or "#RRGGBBAA").
    /// Note: Hex colors are LDR (0-1 range), so this creates a standard color.
    /// </summary>
    public static colorHDR FromHex(string hex)
    {
        hex = hex.TrimStart('#');

        if (hex.Length == 6)
        {
            return new colorHDR(
                Convert.ToInt32(hex.Substring(0, 2), 16) / 255f,
                Convert.ToInt32(hex.Substring(2, 2), 16) / 255f,
                Convert.ToInt32(hex.Substring(4, 2), 16) / 255f,
                1f
            );
        }
        else if (hex.Length == 8)
        {
            return new colorHDR(
                Convert.ToInt32(hex.Substring(0, 2), 16) / 255f,
                Convert.ToInt32(hex.Substring(2, 2), 16) / 255f,
                Convert.ToInt32(hex.Substring(4, 2), 16) / 255f,
                Convert.ToInt32(hex.Substring(6, 2), 16) / 255f
            );
        }

        throw new ArgumentException("Invalid hex color format");
    }

    /// <summary>
    /// Converts this HDR color to a hex string (e.g., "#RRGGBBAA").
    /// RGB values are clamped to 0-1 range for hex representation.
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
    /// Linear interpolation between two HDR colors.
    /// </summary>
    public static colorHDR Lerp(colorHDR a, colorHDR b, float t)
    {
        return new colorHDR(
            a.r + (b.r - a.r) * t,
            a.g + (b.g - a.g) * t,
            a.b + (b.b - a.b) * t,
            a.a + (b.a - a.a) * t
        );
    }

    /// <summary>
    /// Returns the grayscale value (luminance) of this HDR color.
    /// Can exceed 1.0 for bright HDR colors.
    /// </summary>
    public float Grayscale => 0.299f * r + 0.587f * g + 0.114f * b;

    /// <summary>
    /// Returns the maximum RGB component value.
    /// Useful for determining overall brightness in HDR.
    /// </summary>
    public float MaxRGB => MathF.Max(r, MathF.Max(g, b));

    /// <summary>
    /// Returns the minimum RGB component value.
    /// </summary>
    public float MinRGB => MathF.Min(r, MathF.Min(g, b));

    /// <summary>
    /// Returns the average RGB component value.
    /// </summary>
    public float AverageRGB => (r + g + b) / 3f;

    /// <summary>
    /// Clamps RGB components to a maximum value while preserving color ratios.
    /// Useful for preventing excessive brightness in HDR rendering.
    /// </summary>
    /// <param name="maxValue">Maximum allowed RGB value</param>
    public colorHDR ClampMax(float maxValue)
    {
        float max = MaxRGB;
        if (max <= maxValue)
            return this;

        float scale = maxValue / max;
        return new colorHDR(r * scale, g * scale, b * scale, a);
    }

    /// <summary>
    /// Clamps all components to valid ranges (RGB >= 0, Alpha 0-1).
    /// </summary>
    public colorHDR Clamp()
    {
        return new colorHDR(
            MathF.Max(0f, r),
            MathF.Max(0f, g),
            MathF.Max(0f, b),
            MathF.Max(0f, MathF.Min(1f, a))
        );
    }

    /// <summary>
    /// Applies Reinhard tone mapping to convert HDR to LDR color.
    /// Formula: color / (1 + color)
    /// </summary>
    public color ToneMapReinhard()
    {
        return new color(
            r / (1f + r),
            g / (1f + g),
            b / (1f + b),
            a
        );
    }

    /// <summary>
    /// Applies ACES Filmic tone mapping approximation to convert HDR to LDR color.
    /// Provides a more cinematic look than Reinhard.
    /// </summary>
    public color ToneMapACES()
    {
        float a = 2.51f;
        float b = 0.03f;
        float c = 2.43f;
        float d = 0.59f;
        float e = 0.14f;

        float toneMap(float x)
        {
            return MathF.Max(0f, MathF.Min(1f, (x * (a * x + b)) / (x * (c * x + d) + e)));
        }

        return new color(
            toneMap(r),
            toneMap(g),
            toneMap(b),
            this.a
        );
    }

    /// <summary>
    /// Applies exposure adjustment to the HDR color.
    /// </summary>
    /// <param name="exposure">Exposure value (typically -2 to +2). Positive values brighten, negative darken.</param>
    public colorHDR ApplyExposure(float exposure)
    {
        float multiplier = MathF.Pow(2f, exposure);
        return new colorHDR(r * multiplier, g * multiplier, b * multiplier, a);
    }

    /// <summary>
    /// Converts this HDR color to a standard LDR color by clamping RGB to 0-1.
    /// </summary>
    public color ToLDR()
    {
        return new color(
            MathF.Max(0f, MathF.Min(1f, r)),
            MathF.Max(0f, MathF.Min(1f, g)),
            MathF.Max(0f, MathF.Min(1f, b)),
            a
        );
    }

    /// <summary>
    /// Creates an HDR color from a standard LDR color.
    /// </summary>
    public static colorHDR FromLDR(color ldrColor)
    {
        return new colorHDR(ldrColor.r, ldrColor.g, ldrColor.b, ldrColor.a);
    }

    /// <summary>
    /// Implicit conversion from standard color to HDR color.
    /// </summary>
    public static implicit operator colorHDR(color ldrColor)
    {
        return FromLDR(ldrColor);
    }

    // Operators
    public static colorHDR operator +(colorHDR a, colorHDR b) => new colorHDR(a.r + b.r, a.g + b.g, a.b + b.b, a.a + b.a);
    public static colorHDR operator -(colorHDR a, colorHDR b) => new colorHDR(a.r - b.r, a.g - b.g, a.b - b.b, a.a - b.a);
    public static colorHDR operator *(colorHDR a, colorHDR b) => new colorHDR(a.r * b.r, a.g * b.g, a.b * b.b, a.a * b.a);
    public static colorHDR operator /(colorHDR a, colorHDR b) => new colorHDR(a.r / b.r, a.g / b.g, a.b / b.b, a.a / b.a);
    public static colorHDR operator *(colorHDR a, float b) => new colorHDR(a.r * b, a.g * b, a.b * b, a.a * b);
    public static colorHDR operator *(float a, colorHDR b) => new colorHDR(a * b.r, a * b.g, a * b.b, a * b.a);
    public static colorHDR operator /(colorHDR a, float b) => new colorHDR(a.r / b, a.g / b, a.b / b, a.a / b);

    public static bool operator ==(colorHDR a, colorHDR b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
    public static bool operator !=(colorHDR a, colorHDR b) => !(a == b);

    public bool Equals(colorHDR other) => r == other.r && g == other.g && b == other.b && a == other.a;
    public override bool Equals(object? obj) => obj is colorHDR other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(r, g, b, a);
    public override string ToString() => $"({r:F3}, {g:F3}, {b:F3}, {a:F3})";
}
