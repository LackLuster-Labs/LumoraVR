// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// 0 where the surface faces the eye, toward 1 at its edges. Normal and view must be in one
// space; by default both are the surface's own. Per pixel only.
[Node("Input/Fresnel", Description = "0 where the surface faces the eye, toward 1 at its edges.")]
public sealed class Fresnel : ShaderNode
{
    [Glsl("lm_fresnel({normal}, {view}, {power})", Needs = new[] { "lm_fresnel" }, Stages = ShaderStage.Fragment)]
    [return: Port("factor")]
    public static float Run([GlslDefault("NORMAL")] float3 normal, [GlslDefault("VIEW")] float3 view, float power = 5f) => throw GpuOnly;
}
