// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The coefficients, band by band: sh0 is band 0, sh1 to sh3 band 1, sh4 to sh8 band 2 and on. Each is an RGB
// float3, a number or a color, whichever the harmonics hold.
[Node("Math/Spherical Harmonics/Unpack", Description = "The coefficients, band by band: sh0 is band 0, sh1 to sh3 band 1, sh4 to sh8 band 2 and on.")]
public sealed class UnpackSH : ValueNode
{
    public static void Run(SphericalHarmonicsL1 sh, out float3 sh0, out float3 sh1, out float3 sh2, out float3 sh3)
    {
        sh0 = sh.sh0;
        sh1 = sh.sh1;
        sh2 = sh.sh2;
        sh3 = sh.sh3;
    }

    public static void Run(SphericalHarmonicsL2 sh, out float3 sh0, out float3 sh1, out float3 sh2, out float3 sh3,
        out float3 sh4, out float3 sh5, out float3 sh6, out float3 sh7, out float3 sh8)
    {
        sh0 = sh.sh0;
        sh1 = sh.sh1;
        sh2 = sh.sh2;
        sh3 = sh.sh3;
        sh4 = sh.sh4;
        sh5 = sh.sh5;
        sh6 = sh.sh6;
        sh7 = sh.sh7;
        sh8 = sh.sh8;
    }

    public static void Run(SphericalHarmonicsL3 sh, out float3 sh0, out float3 sh1, out float3 sh2, out float3 sh3,
        out float3 sh4, out float3 sh5, out float3 sh6, out float3 sh7, out float3 sh8, out float3 sh9, out float3 sh10,
        out float3 sh11, out float3 sh12, out float3 sh13, out float3 sh14, out float3 sh15)
    {
        sh0 = sh.sh0;
        sh1 = sh.sh1;
        sh2 = sh.sh2;
        sh3 = sh.sh3;
        sh4 = sh.sh4;
        sh5 = sh.sh5;
        sh6 = sh.sh6;
        sh7 = sh.sh7;
        sh8 = sh.sh8;
        sh9 = sh.sh9;
        sh10 = sh.sh10;
        sh11 = sh.sh11;
        sh12 = sh.sh12;
        sh13 = sh.sh13;
        sh14 = sh.sh14;
        sh15 = sh.sh15;
    }

    public static void Run(SphericalHarmonicsL4 sh, out float3 sh0, out float3 sh1, out float3 sh2, out float3 sh3,
        out float3 sh4, out float3 sh5, out float3 sh6, out float3 sh7, out float3 sh8, out float3 sh9, out float3 sh10,
        out float3 sh11, out float3 sh12, out float3 sh13, out float3 sh14, out float3 sh15, out float3 sh16,
        out float3 sh17, out float3 sh18, out float3 sh19, out float3 sh20, out float3 sh21, out float3 sh22,
        out float3 sh23, out float3 sh24)
    {
        sh0 = sh.sh0;
        sh1 = sh.sh1;
        sh2 = sh.sh2;
        sh3 = sh.sh3;
        sh4 = sh.sh4;
        sh5 = sh.sh5;
        sh6 = sh.sh6;
        sh7 = sh.sh7;
        sh8 = sh.sh8;
        sh9 = sh.sh9;
        sh10 = sh.sh10;
        sh11 = sh.sh11;
        sh12 = sh.sh12;
        sh13 = sh.sh13;
        sh14 = sh.sh14;
        sh15 = sh.sh15;
        sh16 = sh.sh16;
        sh17 = sh.sh17;
        sh18 = sh.sh18;
        sh19 = sh.sh19;
        sh20 = sh.sh20;
        sh21 = sh.sh21;
        sh22 = sh.sh22;
        sh23 = sh.sh23;
        sh24 = sh.sh24;
    }

    public static void Run(SphericalHarmonicsL1<float> sh, out float sh0, out float sh1, out float sh2, out float sh3)
    {
        sh0 = sh.sh0;
        sh1 = sh.sh1;
        sh2 = sh.sh2;
        sh3 = sh.sh3;
    }

    public static void Run(SphericalHarmonicsL2<float> sh, out float sh0, out float sh1, out float sh2, out float sh3,
        out float sh4, out float sh5, out float sh6, out float sh7, out float sh8)
    {
        sh0 = sh.sh0;
        sh1 = sh.sh1;
        sh2 = sh.sh2;
        sh3 = sh.sh3;
        sh4 = sh.sh4;
        sh5 = sh.sh5;
        sh6 = sh.sh6;
        sh7 = sh.sh7;
        sh8 = sh.sh8;
    }

    public static void Run(SphericalHarmonicsL3<float> sh, out float sh0, out float sh1, out float sh2, out float sh3,
        out float sh4, out float sh5, out float sh6, out float sh7, out float sh8, out float sh9, out float sh10,
        out float sh11, out float sh12, out float sh13, out float sh14, out float sh15)
    {
        sh0 = sh.sh0;
        sh1 = sh.sh1;
        sh2 = sh.sh2;
        sh3 = sh.sh3;
        sh4 = sh.sh4;
        sh5 = sh.sh5;
        sh6 = sh.sh6;
        sh7 = sh.sh7;
        sh8 = sh.sh8;
        sh9 = sh.sh9;
        sh10 = sh.sh10;
        sh11 = sh.sh11;
        sh12 = sh.sh12;
        sh13 = sh.sh13;
        sh14 = sh.sh14;
        sh15 = sh.sh15;
    }

    public static void Run(SphericalHarmonicsL4<float> sh, out float sh0, out float sh1, out float sh2, out float sh3,
        out float sh4, out float sh5, out float sh6, out float sh7, out float sh8, out float sh9, out float sh10,
        out float sh11, out float sh12, out float sh13, out float sh14, out float sh15, out float sh16, out float sh17,
        out float sh18, out float sh19, out float sh20, out float sh21, out float sh22, out float sh23, out float sh24)
    {
        sh0 = sh.sh0;
        sh1 = sh.sh1;
        sh2 = sh.sh2;
        sh3 = sh.sh3;
        sh4 = sh.sh4;
        sh5 = sh.sh5;
        sh6 = sh.sh6;
        sh7 = sh.sh7;
        sh8 = sh.sh8;
        sh9 = sh.sh9;
        sh10 = sh.sh10;
        sh11 = sh.sh11;
        sh12 = sh.sh12;
        sh13 = sh.sh13;
        sh14 = sh.sh14;
        sh15 = sh.sh15;
        sh16 = sh.sh16;
        sh17 = sh.sh17;
        sh18 = sh.sh18;
        sh19 = sh.sh19;
        sh20 = sh.sh20;
        sh21 = sh.sh21;
        sh22 = sh.sh22;
        sh23 = sh.sh23;
        sh24 = sh.sh24;
    }

    public static void Run(SphericalHarmonicsL1<color> sh, out color sh0, out color sh1, out color sh2, out color sh3)
    {
        sh0 = sh.sh0;
        sh1 = sh.sh1;
        sh2 = sh.sh2;
        sh3 = sh.sh3;
    }

    public static void Run(SphericalHarmonicsL2<color> sh, out color sh0, out color sh1, out color sh2, out color sh3,
        out color sh4, out color sh5, out color sh6, out color sh7, out color sh8)
    {
        sh0 = sh.sh0;
        sh1 = sh.sh1;
        sh2 = sh.sh2;
        sh3 = sh.sh3;
        sh4 = sh.sh4;
        sh5 = sh.sh5;
        sh6 = sh.sh6;
        sh7 = sh.sh7;
        sh8 = sh.sh8;
    }

    public static void Run(SphericalHarmonicsL3<color> sh, out color sh0, out color sh1, out color sh2, out color sh3,
        out color sh4, out color sh5, out color sh6, out color sh7, out color sh8, out color sh9, out color sh10,
        out color sh11, out color sh12, out color sh13, out color sh14, out color sh15)
    {
        sh0 = sh.sh0;
        sh1 = sh.sh1;
        sh2 = sh.sh2;
        sh3 = sh.sh3;
        sh4 = sh.sh4;
        sh5 = sh.sh5;
        sh6 = sh.sh6;
        sh7 = sh.sh7;
        sh8 = sh.sh8;
        sh9 = sh.sh9;
        sh10 = sh.sh10;
        sh11 = sh.sh11;
        sh12 = sh.sh12;
        sh13 = sh.sh13;
        sh14 = sh.sh14;
        sh15 = sh.sh15;
    }

    public static void Run(SphericalHarmonicsL4<color> sh, out color sh0, out color sh1, out color sh2, out color sh3,
        out color sh4, out color sh5, out color sh6, out color sh7, out color sh8, out color sh9, out color sh10,
        out color sh11, out color sh12, out color sh13, out color sh14, out color sh15, out color sh16, out color sh17,
        out color sh18, out color sh19, out color sh20, out color sh21, out color sh22, out color sh23, out color sh24)
    {
        sh0 = sh.sh0;
        sh1 = sh.sh1;
        sh2 = sh.sh2;
        sh3 = sh.sh3;
        sh4 = sh.sh4;
        sh5 = sh.sh5;
        sh6 = sh.sh6;
        sh7 = sh.sh7;
        sh8 = sh.sh8;
        sh9 = sh.sh9;
        sh10 = sh.sh10;
        sh11 = sh.sh11;
        sh12 = sh.sh12;
        sh13 = sh.sh13;
        sh14 = sh.sh14;
        sh15 = sh.sh15;
        sh16 = sh.sh16;
        sh17 = sh.sh17;
        sh18 = sh.sh18;
        sh19 = sh.sh19;
        sh20 = sh.sh20;
        sh21 = sh.sh21;
        sh22 = sh.sh22;
        sh23 = sh.sh23;
        sh24 = sh.sh24;
    }
}
