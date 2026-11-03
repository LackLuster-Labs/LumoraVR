// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// One number by row and column, 0 first; 0 for a row or column outside the matrix.
[Node("Math/Matrices/Get Entry", Description = "One number by row and column, 0 first; 0 for a row or column outside the matrix.",
    Keywords = "cell, row column, element")]
public sealed class MatrixEntry : ValueNode
{
    [return: Port("value")]
    public static float Run(float2x2 matrix, int row, int column) => (uint)row < 2u && (uint)column < 2u ? matrix[row, column] : 0f;

    [return: Port("value")]
    public static float Run(float3x3 matrix, int row, int column) => (uint)row < 3u && (uint)column < 3u ? matrix[row, column] : 0f;

    [return: Port("value")]
    public static float Run(float4x4 matrix, int row, int column) => (uint)row < 4u && (uint)column < 4u ? matrix[row, column] : 0f;
}
