// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The rotation as a 3x3 matrix. The rotation is normalized first, and a zero rotation turns nothing.
[Node("Math/Matrices/Compose Rotation 3x3", Description = "The rotation as a 3x3 matrix.")]
public sealed class ComposeRotation3x3 : ValueNode
{
    [return: Port("matrix")]
    public static float3x3 Run([Default(0, 0, 0, 1)] floatQ rotation) => float3x3.FromRotation(rotation.Normalized);
}
