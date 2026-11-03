// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// A transform matrix: scaled, then turned, then moved, the way a slot is placed in its parent. The
// rotation is normalized first, and a zero rotation turns nothing.
[Node("Math/Matrices/Compose TRS", Description = "A transform matrix: scaled, then turned, then moved, the way a slot is placed in its parent.",
    Keywords = "transform matrix, position rotation scale, translation, local to parent")]
public sealed class ComposeTRS : ValueNode
{
    [return: Port("matrix")]
    public static float4x4 Run(float3 position, [Default(0, 0, 0, 1)] floatQ rotation, [Default(1)] float3 scale) => float4x4.TRS(position, rotation.Normalized, scale);
}
