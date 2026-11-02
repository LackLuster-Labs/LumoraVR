// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// A tangent space normal from a height, from how it changes between pixels; feeds the Material
// Output's normal. Per pixel only.
[Node("Math/Vectors/Bump", Description = "A tangent space normal from a height, from how it changes between pixels; feeds the Material Output's normal.")]
public sealed class Bump : ShaderNode
{
    [Glsl("lm_bump({height}, {strength}, VERTEX, NORMAL, TANGENT, BINORMAL)", Needs = new[] { "lm_bump" }, Stages = ShaderStage.Fragment)]
    public static float3 Run(float height, float strength = 1f) => throw GpuOnly;
}
