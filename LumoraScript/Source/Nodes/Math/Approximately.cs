// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// Whether two values are the same within a tolerance. Numbers and vectors compare lane by lane: a lane passes
// when its difference is below epsilon, or below a millionth of the larger of the two where that is more. Two
// rotations are the same when the four-number dot product of them is at least 1 - epsilon; a rotation and its
// negation turn alike but do not count as the same here. Two equal values always pass, infinities included,
// and a double's own share is a trillionth rather than a millionth. -xlinka
[Node("Logic/Comparison/Approximately", Description = "Whether two values are the same within a tolerance.",
    Keywords = "nearly equal, almost equal, close, tolerance, epsilon")]
public sealed class Approximately : ValueNode
{
    public static bool Run(float a, float b, float epsilon = 0.000001f)
        => a == b || MathF.Abs(a - b) < MathF.Max(0.000001f * MathF.Max(MathF.Abs(a), MathF.Abs(b)), epsilon);

    public static bool Run(float3 a, float3 b, float epsilon = 0.000001f)
        => Run(a.x, b.x, epsilon) && Run(a.y, b.y, epsilon) && Run(a.z, b.z, epsilon);

    public static bool Run(floatQ a, floatQ b, float epsilon = 0.000001f)
        => a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w >= 1f - epsilon;

    public static bool Run(double a, double b, double epsilon = 0.000001d)
        => a == b || Math.Abs(a - b) < Math.Max(1e-12d * Math.Max(Math.Abs(a), Math.Abs(b)), epsilon);

    public static bool Run(float2 a, float2 b, float epsilon = 0.000001f) => Run(a.x, b.x, epsilon) && Run(a.y, b.y, epsilon);

    public static bool Run(float4 a, float4 b, float epsilon = 0.000001f)
        => Run(a.x, b.x, epsilon) && Run(a.y, b.y, epsilon) && Run(a.z, b.z, epsilon) && Run(a.w, b.w, epsilon);

    public static bool Run(color a, color b, float epsilon = 0.000001f)
        => Run(a.r, b.r, epsilon) && Run(a.g, b.g, epsilon) && Run(a.b, b.b, epsilon) && Run(a.a, b.a, epsilon);

    public static bool Run(colorHDR a, colorHDR b, float epsilon = 0.000001f)
        => Run(a.r, b.r, epsilon) && Run(a.g, b.g, epsilon) && Run(a.b, b.b, epsilon) && Run(a.a, b.a, epsilon);
}
