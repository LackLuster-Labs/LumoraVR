// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// Radians. A value outside -1 to 1 has no angle and gives NaN. Vectors and colors go lane by lane.
[Node("Math/Trigonometry/Acos", Description = "Returns the angle whose cosine is the input, in radians.",
    Keywords = "arccosine, inverse cosine")]
public sealed class Acos : ValueNode
{
    [return: Port("radians")] public static float Run(float value) => MathF.Acos(value);
    [return: Port("radians")] public static double Run(double value) => Math.Acos(value);
    [return: Port("radians")] public static float2 Run(float2 value) => new(Run(value.x), Run(value.y));
    [return: Port("radians")] public static float3 Run(float3 value) => new(Run(value.x), Run(value.y), Run(value.z));
    [return: Port("radians")] public static float4 Run(float4 value) => new(Run(value.x), Run(value.y), Run(value.z), Run(value.w));
    [return: Port("radians")] public static color Run(color value) => new(Run(value.r), Run(value.g), Run(value.b), Run(value.a));
    [return: Port("radians")] public static colorHDR Run(colorHDR value) => new(Run(value.r), Run(value.g), Run(value.b), Run(value.a));
}
