// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// A matrix from its rows, top first: the way a matrix is written down.
[Node("Math/Matrices/Pack Rows", Description = "A matrix from its rows, top first: the way a matrix is written down.",
    Keywords = "make matrix, build matrix, compose matrix, matrix from vectors")]
public sealed class PackRows : ValueNode
{
    [return: Port("matrix")]
    public static float2x2 Run(float2 row0, float2 row1) => new float2x2(row0, row1).Transposed;

    [return: Port("matrix")]
    public static float3x3 Run(float3 row0, float3 row1, float3 row2) => new float3x3(row0, row1, row2).Transposed;

    [return: Port("matrix")]
    public static float4x4 Run(float4 row0, float4 row1, float4 row2, float4 row3) => new float4x4(row0, row1, row2, row3).Transposed;
}
