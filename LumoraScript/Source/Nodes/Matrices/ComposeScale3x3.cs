// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Stretches x, y and z, each by its own amount or all by one.
[Node("Math/Matrices/Compose Scale 3x3", Description = "Stretches x, y and z, each by its own amount or all by one.")]
public sealed class ComposeScale3x3 : ValueNode
{
    [return: Port("matrix")] public static float3x3 Run([Default(1)] float3 scale) => float3x3.Scale(scale);
    [return: Port("matrix")] public static float3x3 Run(float scale = 1f) => float3x3.Scale(new float3(scale, scale, scale));
}
