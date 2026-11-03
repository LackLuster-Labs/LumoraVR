// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The matrix that undoes this one: the two multiplied give the identity. A 4x4 matrix is inverted whole,
// bottom row and all. A matrix that flattens something has no inverse and gives the zero matrix.
[Node("Math/Matrices/Inverse", Description = "The matrix that undoes this one: the two multiplied give the identity.",
    Keywords = "invert, inverse matrix, undo transform")]
public sealed class MatrixInverse : ValueNode
{
    [return: Port("inverse")] public static float2x2 Run(float2x2 matrix) => matrix.Inverse;
    [return: Port("inverse")] public static float3x3 Run(float3x3 matrix) => matrix.Inverse;
    [return: Port("inverse")] public static float4x4 Run(float4x4 matrix) => matrix.GeneralInverse;
}
