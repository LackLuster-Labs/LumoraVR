// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

[Node("Math/Rotations/Rotate Vector", Description = "Rotates a vector by the given rotation.",
    Keywords = "rotate direction, rotate point, quaternion times vector")]
public sealed class RotateVector : ValueNode
{
    public static float3 Run([Default(0, 0, 0, 1)] floatQ rotation, float3 vector) => rotation * vector;
}
