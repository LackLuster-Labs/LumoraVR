// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// A matrix's rows, top first.
[Node("Math/Matrices/Unpack Rows", Description = "A matrix's rows, top first.",
    Keywords = "split matrix, matrix to vectors, basis vectors")]
public sealed class UnpackRows : ValueNode
{
    public static void Run(float2x2 matrix, out float2 row0, out float2 row1)
    {
        row0 = matrix.Row0;
        row1 = matrix.Row1;
    }

    public static void Run(float3x3 matrix, out float3 row0, out float3 row1, out float3 row2)
    {
        row0 = matrix.Row0;
        row1 = matrix.Row1;
        row2 = matrix.Row2;
    }

    public static void Run(float4x4 matrix, out float4 row0, out float4 row1, out float4 row2, out float4 row3)
    {
        var rows = matrix.Transposed;
        row0 = rows.c0;
        row1 = rows.c1;
        row2 = rows.c2;
        row3 = rows.c3;
    }
}
