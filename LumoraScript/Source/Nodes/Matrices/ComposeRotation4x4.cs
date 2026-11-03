// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The rotation as a 4x4 matrix that moves nothing. The rotation is normalized first, and a zero
// rotation turns nothing.
[Node("Math/Matrices/Compose Rotation 4x4", Description = "The rotation as a 4x4 matrix that moves nothing.")]
public sealed class ComposeRotation4x4 : ValueNode
{
    [return: Port("matrix")]
    public static float4x4 Run([Default(0, 0, 0, 1)] floatQ rotation) => float4x4.Rotate(rotation.Normalized);
}
