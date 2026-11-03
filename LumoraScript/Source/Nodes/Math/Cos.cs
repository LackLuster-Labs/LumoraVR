// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// Radians. Vectors and colors go lane by lane.
[Node("Math/Trigonometry/Cos", Description = "Returns the cosine of an angle in radians.",
    Keywords = "cosine, radians")]
public sealed class Cos : ValueNode
{
    public static float Run(float radians) => MathF.Cos(radians);
    public static double Run(double radians) => Math.Cos(radians);
    public static float2 Run(float2 radians) => new(Run(radians.x), Run(radians.y));
    public static float3 Run(float3 radians) => new(Run(radians.x), Run(radians.y), Run(radians.z));
    public static float4 Run(float4 radians) => new(Run(radians.x), Run(radians.y), Run(radians.z), Run(radians.w));
    public static color Run(color radians) => new(Run(radians.r), Run(radians.g), Run(radians.b), Run(radians.a));
    public static colorHDR Run(colorHDR radians) => new(Run(radians.r), Run(radians.g), Run(radians.b), Run(radians.a));
}
