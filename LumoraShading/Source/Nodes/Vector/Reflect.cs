// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Bounces incident off a surface facing normal; normal should be normalized.
[Node("Math/Vectors/Reflect", Description = "Bounces incident off a surface facing normal; normal should be normalized.")]
public sealed class Reflect : ShaderNode
{
    [Glsl("reflect({incident}, {normal})")]
    public static float3 Run(float3 incident, float3 normal) => throw GpuOnly;
}
