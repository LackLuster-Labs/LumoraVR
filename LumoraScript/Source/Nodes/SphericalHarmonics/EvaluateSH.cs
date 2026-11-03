// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Script;

// The value the harmonics hold toward a direction: each coefficient times its real basis function there,
// summed. The direction is normalized first; a zero direction leaves only band 0. Harmonics of a number give
// a number, and of a color a color, alpha summed like the rest.
[Node("Math/Spherical Harmonics/Evaluate", Description = "The value the harmonics hold toward a direction: each coefficient times its real basis function there, summed.",
    Keywords = "SH, sample sh, ambient light, irradiance, lighting")]
public sealed class EvaluateSH : ValueNode
{
    // Normalized, a zero direction is zero, and there the zonal terms of bands 2 and 4 do not vanish; band 0
    // is all that has a meaning without a direction. -xlinka
    private const float Band0 = 0.2820948f;

    [return: Port("value")] public static float3 Run(SphericalHarmonicsL1 sh, float3 direction) => Toward(direction) ? sh.Evaluate(direction) : sh.sh0 * Band0;
    [return: Port("value")] public static float3 Run(SphericalHarmonicsL2 sh, float3 direction) => Toward(direction) ? sh.Evaluate(direction) : sh.sh0 * Band0;
    [return: Port("value")] public static float3 Run(SphericalHarmonicsL3 sh, float3 direction) => Toward(direction) ? sh.Evaluate(direction) : sh.sh0 * Band0;
    [return: Port("value")] public static float3 Run(SphericalHarmonicsL4 sh, float3 direction) => Toward(direction) ? sh.Evaluate(direction) : sh.sh0 * Band0;

    [return: Port("value")]
    public static float Run(SphericalHarmonicsL1<float> sh, float3 direction)
        => Sum(direction, Coefficients(ref sh.sh0, SphericalHarmonicsL1<float>.Count));

    [return: Port("value")]
    public static float Run(SphericalHarmonicsL2<float> sh, float3 direction)
        => Sum(direction, Coefficients(ref sh.sh0, SphericalHarmonicsL2<float>.Count));

    [return: Port("value")]
    public static float Run(SphericalHarmonicsL3<float> sh, float3 direction)
        => Sum(direction, Coefficients(ref sh.sh0, SphericalHarmonicsL3<float>.Count));

    [return: Port("value")]
    public static float Run(SphericalHarmonicsL4<float> sh, float3 direction)
        => Sum(direction, Coefficients(ref sh.sh0, SphericalHarmonicsL4<float>.Count));

    [return: Port("value")]
    public static color Run(SphericalHarmonicsL1<color> sh, float3 direction)
        => Sum(direction, Coefficients(ref sh.sh0, SphericalHarmonicsL1<color>.Count));

    [return: Port("value")]
    public static color Run(SphericalHarmonicsL2<color> sh, float3 direction)
        => Sum(direction, Coefficients(ref sh.sh0, SphericalHarmonicsL2<color>.Count));

    [return: Port("value")]
    public static color Run(SphericalHarmonicsL3<color> sh, float3 direction)
        => Sum(direction, Coefficients(ref sh.sh0, SphericalHarmonicsL3<color>.Count));

    [return: Port("value")]
    public static color Run(SphericalHarmonicsL4<color> sh, float3 direction)
        => Sum(direction, Coefficients(ref sh.sh0, SphericalHarmonicsL4<color>.Count));

    // The coefficients of a harmonics value as a run, first to last; every field of one is the same type.
    private static ReadOnlySpan<T> Coefficients<T>(ref T first, int count) where T : unmanaged
        => MemoryMarshal.CreateReadOnlySpan(ref first, count);

    private static bool Toward(float3 direction) => direction.Length > 0f;

    private static float Sum(float3 direction, ReadOnlySpan<float> coefficients)
    {
        if (!Toward(direction))
            return coefficients[0] * Band0;
        Span<float> basis = stackalloc float[coefficients.Length];
        SphericalHarmonics.Basis(direction, basis);
        float sum = 0f;
        for (int i = 0; i < coefficients.Length; i++)
            sum += coefficients[i] * basis[i];
        return sum;
    }

    private static color Sum(float3 direction, ReadOnlySpan<color> coefficients)
    {
        if (!Toward(direction))
            return coefficients[0] * Band0;
        Span<float> basis = stackalloc float[coefficients.Length];
        SphericalHarmonics.Basis(direction, basis);
        var sum = new color(0f, 0f, 0f, 0f);
        for (int i = 0; i < coefficients.Length; i++)
            sum += coefficients[i] * basis[i];
        return sum;
    }
}
