// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// A matrix from its columns, left first. In a transform matrix the first three columns are where x, y
// and z point and the last is the position.
[Node("Math/Matrices/Pack Columns", Description = "A matrix from its columns, left first.",
    Keywords = "make matrix, build matrix, compose matrix, matrix from vectors")]
public sealed class PackColumns : ValueNode
{
    [return: Port("matrix")]
    public static float2x2 Run(float2 column0, float2 column1) => new(column0, column1);

    [return: Port("matrix")]
    public static float3x3 Run(float3 column0, float3 column1, float3 column2) => new(column0, column1, column2);

    [return: Port("matrix")]
    public static float4x4 Run(float4 column0, float4 column1, float4 column2, float4 column3) => new(column0, column1, column2, column3);
}
