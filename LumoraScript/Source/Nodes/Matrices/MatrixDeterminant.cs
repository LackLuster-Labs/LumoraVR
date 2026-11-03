// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// How much the matrix scales area or volume: negative when it mirrors, zero when it flattens something
// and has no inverse.
[Node("Math/Matrices/Determinant", Description = "How much the matrix scales area or volume: negative when it mirrors, zero when it flattens something and has no inverse.")]
public sealed class MatrixDeterminant : ValueNode
{
    [return: Port("determinant")] public static float Run(float2x2 matrix) => matrix.Determinant;
    [return: Port("determinant")] public static float Run(float3x3 matrix) => matrix.Determinant;
    [return: Port("determinant")] public static float Run(float4x4 matrix) => matrix.Determinant;
}
