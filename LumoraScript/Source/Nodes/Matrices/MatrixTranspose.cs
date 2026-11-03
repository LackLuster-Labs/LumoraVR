// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Rows become columns. For a pure rotation matrix this is also its inverse.
[Node("Math/Matrices/Transpose", Description = "Rows become columns.")]
public sealed class MatrixTranspose : ValueNode
{
    [return: Port("transposed")] public static float2x2 Run(float2x2 matrix) => matrix.Transposed;
    [return: Port("transposed")] public static float3x3 Run(float3x3 matrix) => matrix.Transposed;
    [return: Port("transposed")] public static float4x4 Run(float4x4 matrix) => matrix.Transposed;
}
