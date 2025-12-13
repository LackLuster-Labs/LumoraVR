using System;

namespace Lumora.Core.Math;

/// <summary>
/// Math helper functions for Lumora.
/// Named after "Lumina" (light/luminosity) to match Lumora's theme.
/// </summary>
public static class LuminaMath
{
    public const float PI = (float)System.Math.PI;
    public const float Epsilon = 1e-6f;

    public static float Min(float a, float b) => System.Math.Min(a, b);
    public static int Min(int a, int b) => System.Math.Min(a, b);
    public static float Max(float a, float b) => System.Math.Max(a, b);
    public static int Max(int a, int b) => System.Math.Max(a, b);

    public static float Sin(float x) => (float)System.Math.Sin(x);
    public static float Cos(float x) => (float)System.Math.Cos(x);

    public static bool Approximately(float a, float b) => System.Math.Abs(a - b) < Epsilon;

    public static float Abs(float x) => System.Math.Abs(x);
    public static int Abs(int x) => System.Math.Abs(x);
    public static float Clamp(float value, float min, float max) => System.Math.Max(min, System.Math.Min(max, value));
    public static int Clamp(int value, int min, int max) => System.Math.Max(min, System.Math.Min(max, value));
    public static float Round(float x) => (float)System.Math.Round(x);
    public static int RoundToInt(float x) => (int)System.Math.Round(x);
    public static float Pow(float x, float y) => (float)System.Math.Pow(x, y);
    public static float Sqrt(float x) => (float)System.Math.Sqrt(x);

    public static float3 Abs(float3 v)
    {
        return new float3(
            System.Math.Abs(v.x),
            System.Math.Abs(v.y),
            System.Math.Abs(v.z)
        );
    }

    public static float Dot(float3 a, float3 b) => float3.Dot(a, b);
    public static float3 Cross(float3 a, float3 b) => float3.Cross(a, b);

    public static int Max(int a, int b, int c) => System.Math.Max(a, System.Math.Max(b, c));
    public static float Max(float a, float b, float c) => System.Math.Max(a, System.Math.Max(b, c));
}
