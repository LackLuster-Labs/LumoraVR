// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// Every coefficient of a band times that band's factor: band 0 is the average, the higher bands ever
// finer detail, so shrinking them blurs. An unset factor is 1 and leaves its band alone.
[Node("Math/Spherical Harmonics/Scale Orders", Description = "Every coefficient of a band times that band's factor: band 0 is the average, the higher bands ever finer detail, so shrinking them blurs.",
    Keywords = "SH, bands, blur, windowing, filter")]
public sealed class ScaleSHOrders : ValueNode
{
    [return: Port("scaled")]
    public static SphericalHarmonicsL1 Run(SphericalHarmonicsL1 sh, float band0 = 1f, float band1 = 1f)
    {
        ReadOnlySpan<float> factors = stackalloc float[] { band0, band1 };
        for (int i = 0; i < SphericalHarmonicsL1.Count; i++)
            sh[i] *= factors[SphericalHarmonics.BandOf(i)];
        return sh;
    }

    [return: Port("scaled")]
    public static SphericalHarmonicsL2 Run(SphericalHarmonicsL2 sh, float band0 = 1f, float band1 = 1f, float band2 = 1f)
    {
        ReadOnlySpan<float> factors = stackalloc float[] { band0, band1, band2 };
        for (int i = 0; i < SphericalHarmonicsL2.Count; i++)
            sh[i] *= factors[SphericalHarmonics.BandOf(i)];
        return sh;
    }

    [return: Port("scaled")]
    public static SphericalHarmonicsL3 Run(SphericalHarmonicsL3 sh, float band0 = 1f, float band1 = 1f, float band2 = 1f, float band3 = 1f)
    {
        ReadOnlySpan<float> factors = stackalloc float[] { band0, band1, band2, band3 };
        for (int i = 0; i < SphericalHarmonicsL3.Count; i++)
            sh[i] *= factors[SphericalHarmonics.BandOf(i)];
        return sh;
    }

    [return: Port("scaled")]
    public static SphericalHarmonicsL4 Run(SphericalHarmonicsL4 sh, float band0 = 1f, float band1 = 1f, float band2 = 1f, float band3 = 1f,
        float band4 = 1f)
    {
        ReadOnlySpan<float> factors = stackalloc float[] { band0, band1, band2, band3, band4 };
        for (int i = 0; i < SphericalHarmonicsL4.Count; i++)
            sh[i] *= factors[SphericalHarmonics.BandOf(i)];
        return sh;
    }

    [return: Port("scaled")]
    public static SphericalHarmonicsL1<float> Run(SphericalHarmonicsL1<float> sh, float band0 = 1f, float band1 = 1f)
    {
        ReadOnlySpan<float> factors = stackalloc float[] { band0, band1 };
        for (int i = 0; i < SphericalHarmonicsL1<float>.Count; i++)
            sh[i] *= factors[SphericalHarmonics.BandOf(i)];
        return sh;
    }

    [return: Port("scaled")]
    public static SphericalHarmonicsL2<float> Run(SphericalHarmonicsL2<float> sh, float band0 = 1f, float band1 = 1f,
        float band2 = 1f)
    {
        ReadOnlySpan<float> factors = stackalloc float[] { band0, band1, band2 };
        for (int i = 0; i < SphericalHarmonicsL2<float>.Count; i++)
            sh[i] *= factors[SphericalHarmonics.BandOf(i)];
        return sh;
    }

    [return: Port("scaled")]
    public static SphericalHarmonicsL3<float> Run(SphericalHarmonicsL3<float> sh, float band0 = 1f, float band1 = 1f,
        float band2 = 1f, float band3 = 1f)
    {
        ReadOnlySpan<float> factors = stackalloc float[] { band0, band1, band2, band3 };
        for (int i = 0; i < SphericalHarmonicsL3<float>.Count; i++)
            sh[i] *= factors[SphericalHarmonics.BandOf(i)];
        return sh;
    }

    [return: Port("scaled")]
    public static SphericalHarmonicsL4<float> Run(SphericalHarmonicsL4<float> sh, float band0 = 1f, float band1 = 1f,
        float band2 = 1f, float band3 = 1f, float band4 = 1f)
    {
        ReadOnlySpan<float> factors = stackalloc float[] { band0, band1, band2, band3, band4 };
        for (int i = 0; i < SphericalHarmonicsL4<float>.Count; i++)
            sh[i] *= factors[SphericalHarmonics.BandOf(i)];
        return sh;
    }

    [return: Port("scaled")]
    public static SphericalHarmonicsL1<color> Run(SphericalHarmonicsL1<color> sh, float band0 = 1f, float band1 = 1f)
    {
        ReadOnlySpan<float> factors = stackalloc float[] { band0, band1 };
        for (int i = 0; i < SphericalHarmonicsL1<color>.Count; i++)
            sh[i] *= factors[SphericalHarmonics.BandOf(i)];
        return sh;
    }

    [return: Port("scaled")]
    public static SphericalHarmonicsL2<color> Run(SphericalHarmonicsL2<color> sh, float band0 = 1f, float band1 = 1f,
        float band2 = 1f)
    {
        ReadOnlySpan<float> factors = stackalloc float[] { band0, band1, band2 };
        for (int i = 0; i < SphericalHarmonicsL2<color>.Count; i++)
            sh[i] *= factors[SphericalHarmonics.BandOf(i)];
        return sh;
    }

    [return: Port("scaled")]
    public static SphericalHarmonicsL3<color> Run(SphericalHarmonicsL3<color> sh, float band0 = 1f, float band1 = 1f,
        float band2 = 1f, float band3 = 1f)
    {
        ReadOnlySpan<float> factors = stackalloc float[] { band0, band1, band2, band3 };
        for (int i = 0; i < SphericalHarmonicsL3<color>.Count; i++)
            sh[i] *= factors[SphericalHarmonics.BandOf(i)];
        return sh;
    }

    [return: Port("scaled")]
    public static SphericalHarmonicsL4<color> Run(SphericalHarmonicsL4<color> sh, float band0 = 1f, float band1 = 1f,
        float band2 = 1f, float band3 = 1f, float band4 = 1f)
    {
        ReadOnlySpan<float> factors = stackalloc float[] { band0, band1, band2, band3, band4 };
        for (int i = 0; i < SphericalHarmonicsL4<color>.Count; i++)
            sh[i] *= factors[SphericalHarmonics.BandOf(i)];
        return sh;
    }
}
