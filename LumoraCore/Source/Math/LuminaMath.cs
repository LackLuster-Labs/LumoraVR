using System;

namespace Lumora.Core.Math;

/// <summary>
/// Math helper functions for float3 vectors.
/// Named after "Lumina" (light/luminosity) to match Lumora's theme.
/// </summary>
public static class LuminaMath
{
    /// <summary>
    /// Returns absolute value of a float3 vector (component-wise).
    /// </summary>
    public static float3 Abs(float3 v)
    {
        return new float3(
            System.Math.Abs(v.x),
            System.Math.Abs(v.y),
            System.Math.Abs(v.z)
        );
    }

    /// <summary>
    /// Dot product of two float3 vectors.
    /// </summary>
    public static float Dot(float3 a, float3 b)
    {
        return float3.Dot(a, b);
    }

    /// <summary>
    /// Cross product of two float3 vectors.
    /// </summary>
    public static float3 Cross(float3 a, float3 b)
    {
        return float3.Cross(a, b);
    }

    /// <summary>
    /// Returns the maximum of three values.
    /// </summary>
    public static int Max(int a, int b, int c)
    {
        return System.Math.Max(a, System.Math.Max(b, c));
    }

    /// <summary>
    /// Returns the maximum of three values.
    /// </summary>
    public static float Max(float a, float b, float c)
    {
        return System.Math.Max(a, System.Math.Max(b, c));
    }
}
