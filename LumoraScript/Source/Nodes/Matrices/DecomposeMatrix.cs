// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// The parts a matrix was composed from: position, rotation and scale from a 4x4, rotation and scale from
// a 3x3, a turn in degrees and a scale from a 2x2. Scale is each axis's length, x negative when the
// matrix mirrors; rotation comes from the axes squared up, z kept and y made square to it. A matrix with
// shear, or a 4x4 with a projective bottom row, has no exact parts, and what it gives does not rebuild it.
[Node("Math/Matrices/Decompose", Description = "The parts a matrix was composed from: position, rotation and scale from a 4x4, rotation and scale from a 3x3, a turn in degrees and a scale from a 2x2.",
    Keywords = "matrix to position rotation scale, extract, transform matrix")]
public sealed class DecomposeMatrix : ValueNode
{
    public static void Run(float4x4 matrix, out float3 position, out floatQ rotation, out float3 scale)
        => matrix.Decompose(out position, out rotation, out scale);

    public static void Run(float3x3 matrix, out floatQ rotation, out float3 scale)
        => matrix.Decompose(out rotation, out scale);

    public static void Run(float2x2 matrix, out float degrees, out float2 scale)
    {
        matrix.Decompose(out float radians, out scale);
        degrees = radians * (180f / MathF.PI);
    }
}
