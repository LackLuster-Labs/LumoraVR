// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// e to the power: the growth that undoes Log. Vectors and colors go lane by lane.
[Node("Math/Exp", Description = "E to the power: the growth that undoes Log.",
    Keywords = "exponential, e to the power")]
public sealed class Exp : ValueNode
{
    public static float Run(float power) => MathF.Exp(power);
    public static double Run(double power) => Math.Exp(power);
    public static float2 Run(float2 power) => new(Run(power.x), Run(power.y));
    public static float3 Run(float3 power) => new(Run(power.x), Run(power.y), Run(power.z));
    public static float4 Run(float4 power) => new(Run(power.x), Run(power.y), Run(power.z), Run(power.w));
    public static color Run(color power) => new(Run(power.r), Run(power.g), Run(power.b), Run(power.a));
    public static colorHDR Run(colorHDR power) => new(Run(power.r), Run(power.g), Run(power.b), Run(power.a));
}
