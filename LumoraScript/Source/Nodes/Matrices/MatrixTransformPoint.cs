// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// A point carried through the matrix, translation included. A projective matrix divides by the w it
// gives, except a w of zero, which leaves the point undivided rather than off at infinity.
[Node("Math/Matrices/Transform Point", Description = "A point carried through the matrix, translation included.",
    Keywords = "multiply point, apply matrix")]
public sealed class MatrixTransformPoint : ValueNode
{
    [return: Port("transformed")]
    public static float3 Run([Default(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1)] float4x4 matrix, float3 point)
    {
        var moved = matrix * new float4(point, 1f);
        return moved.w == 1f || moved.w == 0f ? moved.xyz : moved.xyz / moved.w;
    }
}
