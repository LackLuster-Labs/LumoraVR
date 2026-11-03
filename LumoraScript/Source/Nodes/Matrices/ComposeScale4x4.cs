// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Stretches x, y and z, each by its own amount or all by one, as a 4x4 matrix that moves nothing.
[Node("Math/Matrices/Compose Scale 4x4", Description = "Stretches x, y and z, each by its own amount or all by one, as a 4x4 matrix that moves nothing.")]
public sealed class ComposeScale4x4 : ValueNode
{
    [return: Port("matrix")] public static float4x4 Run([Default(1)] float3 scale) => float4x4.Scale(scale);
    [return: Port("matrix")] public static float4x4 Run(float scale = 1f) => float4x4.Scale(scale);
}
