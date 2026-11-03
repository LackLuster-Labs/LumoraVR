// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// A matrix's columns, left first. In a transform matrix the first three are where x, y and z point and
// the last is the position.
[Node("Math/Matrices/Unpack Columns", Description = "A matrix's columns, left first.",
    Keywords = "split matrix, matrix to vectors, basis vectors")]
public sealed class UnpackColumns : ValueNode
{
    public static void Run(float2x2 matrix, out float2 column0, out float2 column1)
    {
        column0 = matrix.c0;
        column1 = matrix.c1;
    }

    public static void Run(float3x3 matrix, out float3 column0, out float3 column1, out float3 column2)
    {
        column0 = matrix.c0;
        column1 = matrix.c1;
        column2 = matrix.c2;
    }

    public static void Run(float4x4 matrix, out float4 column0, out float4 column1, out float4 column2, out float4 column3)
    {
        column0 = matrix.c0;
        column1 = matrix.c1;
        column2 = matrix.c2;
        column3 = matrix.c3;
    }
}
