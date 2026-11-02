// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// A rectangle filling width and height of the UV square, soft by one pixel. Per pixel only.
[Node("Procedural/Shapes/Rectangle", Description = "A rectangle filling width and height of the UV square, soft by one pixel.")]
public sealed class Rectangle : ShaderNode
{
    [Glsl("lm_rectangle({uv}, {width}, {height})", Needs = new[] { "lm_rectangle" }, Stages = ShaderStage.Fragment)]
    [return: Port("shape")]
    public static float Run([GlslDefault("UV")] float2 uv, float width = 0.5f, float height = 0.5f) => throw GpuOnly;
}
