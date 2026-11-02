// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// One tile of a sheet of columns by rows, counted from the top left across.
[Node("UV/Flipbook", Description = "One tile of a sheet of columns by rows, counted from the top left across.")]
public sealed class Flipbook : ShaderNode
{
    [Glsl("lm_flipbook({uv}, {columns}, {rows}, {tile})", Needs = new[] { "lm_flipbook" })]
    public static float2 Run([GlslDefault("UV")] float2 uv, float columns = 4f, float rows = 4f, float tile = 0f) => throw GpuOnly;
}
