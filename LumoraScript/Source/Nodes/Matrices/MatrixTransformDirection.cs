// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// A direction carried through the matrix's rotation and scale, translation left out. The scale stays in
// the length; Normalize gives a unit direction.
[Node("Math/Matrices/Transform Direction", Description = "A direction carried through the matrix's rotation and scale, translation left out.",
    Keywords = "multiply vector, rotate vector by matrix")]
public sealed class MatrixTransformDirection : ValueNode
{
    [return: Port("transformed")]
    public static float3 Run([Default(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1)] float4x4 matrix, float3 direction)
        => matrix.MultiplyVector(direction);

    [return: Port("transformed")]
    public static float3 Run([Default(1, 0, 0, 0, 1, 0, 0, 0, 1)] float3x3 matrix, float3 direction) => matrix * direction;

    [return: Port("transformed")]
    public static float2 Run([Default(1, 0, 0, 1)] float2x2 matrix, float2 direction) => matrix * direction;
}
