// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Spherical harmonics from the coefficients, band by band: sh0 is band 0, sh1 to sh3 band 1, sh4 to sh8
// band 2 and on. Four coefficients make sh1, nine sh2, sixteen sh3 and twenty-five sh4; numbers make the
// harmonics of a number, sh1f to sh4f, and colors those of a color, sh1c to sh4c.
[Node("Math/Spherical Harmonics/Pack", Description = "Spherical harmonics from the coefficients, band by band: sh0 is band 0, sh1 to sh3 band 1, sh4 to sh8 band 2 and on.")]
public sealed class PackSH : ValueNode
{
    [return: Port("sh")]
    public static SphericalHarmonicsL1 Run(float3 sh0, float3 sh1, float3 sh2, float3 sh3)
        => new(sh0, sh1, sh2, sh3);

    [return: Port("sh")]
    public static SphericalHarmonicsL2 Run(float3 sh0, float3 sh1, float3 sh2, float3 sh3, float3 sh4, float3 sh5,
        float3 sh6, float3 sh7, float3 sh8)
        => new(sh0, sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8);

    [return: Port("sh")]
    public static SphericalHarmonicsL3 Run(float3 sh0, float3 sh1, float3 sh2, float3 sh3, float3 sh4, float3 sh5,
        float3 sh6, float3 sh7, float3 sh8, float3 sh9, float3 sh10, float3 sh11, float3 sh12, float3 sh13, float3 sh14,
        float3 sh15)
        => new(sh0, sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, sh10, sh11, sh12, sh13, sh14, sh15);

    [return: Port("sh")]
    public static SphericalHarmonicsL4 Run(float3 sh0, float3 sh1, float3 sh2, float3 sh3, float3 sh4, float3 sh5,
        float3 sh6, float3 sh7, float3 sh8, float3 sh9, float3 sh10, float3 sh11, float3 sh12, float3 sh13, float3 sh14,
        float3 sh15, float3 sh16, float3 sh17, float3 sh18, float3 sh19, float3 sh20, float3 sh21, float3 sh22, float3 sh23,
        float3 sh24)
        => new(sh0, sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, sh10, sh11, sh12, sh13, sh14, sh15, sh16, sh17, sh18, sh19,
            sh20, sh21, sh22, sh23, sh24);

    [return: Port("sh")]
    public static SphericalHarmonicsL1<float> Run(float sh0, float sh1, float sh2, float sh3)
        => new(sh0, sh1, sh2, sh3);

    [return: Port("sh")]
    public static SphericalHarmonicsL2<float> Run(float sh0, float sh1, float sh2, float sh3, float sh4, float sh5,
        float sh6, float sh7, float sh8)
        => new(sh0, sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8);

    [return: Port("sh")]
    public static SphericalHarmonicsL3<float> Run(float sh0, float sh1, float sh2, float sh3, float sh4, float sh5,
        float sh6, float sh7, float sh8, float sh9, float sh10, float sh11, float sh12, float sh13, float sh14,
        float sh15)
        => new(sh0, sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, sh10, sh11, sh12, sh13, sh14, sh15);

    [return: Port("sh")]
    public static SphericalHarmonicsL4<float> Run(float sh0, float sh1, float sh2, float sh3, float sh4, float sh5,
        float sh6, float sh7, float sh8, float sh9, float sh10, float sh11, float sh12, float sh13, float sh14,
        float sh15, float sh16, float sh17, float sh18, float sh19, float sh20, float sh21, float sh22, float sh23,
        float sh24)
        => new(sh0, sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, sh10, sh11, sh12, sh13, sh14, sh15, sh16, sh17, sh18,
            sh19, sh20, sh21, sh22, sh23, sh24);

    [return: Port("sh")]
    public static SphericalHarmonicsL1<color> Run(color sh0, color sh1, color sh2, color sh3)
        => new(sh0, sh1, sh2, sh3);

    [return: Port("sh")]
    public static SphericalHarmonicsL2<color> Run(color sh0, color sh1, color sh2, color sh3, color sh4, color sh5,
        color sh6, color sh7, color sh8)
        => new(sh0, sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8);

    [return: Port("sh")]
    public static SphericalHarmonicsL3<color> Run(color sh0, color sh1, color sh2, color sh3, color sh4, color sh5,
        color sh6, color sh7, color sh8, color sh9, color sh10, color sh11, color sh12, color sh13, color sh14,
        color sh15)
        => new(sh0, sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, sh10, sh11, sh12, sh13, sh14, sh15);

    [return: Port("sh")]
    public static SphericalHarmonicsL4<color> Run(color sh0, color sh1, color sh2, color sh3, color sh4, color sh5,
        color sh6, color sh7, color sh8, color sh9, color sh10, color sh11, color sh12, color sh13, color sh14,
        color sh15, color sh16, color sh17, color sh18, color sh19, color sh20, color sh21, color sh22, color sh23,
        color sh24)
        => new(sh0, sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, sh10, sh11, sh12, sh13, sh14, sh15, sh16, sh17, sh18,
            sh19, sh20, sh21, sh22, sh23, sh24);
}
