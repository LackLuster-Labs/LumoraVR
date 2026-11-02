// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

[Node("UV/Tiling And Offset", Description = "Scales and shifts texture coordinates to control texture repetition and placement.")]
public sealed class TilingAndOffset : ShaderNode
{
    [Glsl("{uv} * {tiling} + {offset}")]
    public static float2 Run([GlslDefault("UV")] float2 uv, [GlslDefault("vec2(1.0)")] float2 tiling = default, float2 offset = default) => throw GpuOnly;
}
