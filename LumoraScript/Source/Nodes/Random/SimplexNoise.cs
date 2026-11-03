// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Smooth noise at a position in one to four dimensions, about -1 to 1: nearby positions give nearby
// values, with features about one unit across. The same position gives the same value every time and on
// every machine, so it needs no seed. Scale the position to change the size of the features; add to it
// to sample somewhere else.
[Node("Math/Random/Simplex Noise", Description = "Smooth noise at a position in one to four dimensions, about -1 to 1: nearby positions give nearby values, with features about one unit across.",
    Keywords = "perlin, noise, procedural, smooth random")]
public sealed class SimplexNoise : ValueNode
{
    [return: Port("noise")] public static float Run(float position) => Simplex.Noise1D(position);
    [return: Port("noise")] public static float Run(float2 position) => Simplex.Noise2D(position.x, position.y);
    [return: Port("noise")] public static float Run(float3 position) => Simplex.Noise3D(position.x, position.y, position.z);
    [return: Port("noise")] public static float Run(float4 position) => Simplex.Noise4D(position.x, position.y, position.z, position.w);
}
