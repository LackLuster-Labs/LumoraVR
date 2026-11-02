// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

[Node("Color/Gamma", Description = "Applies a gamma adjustment to the colour.")]
public sealed class Gamma : ShaderNode
{
    [Glsl("vec4(pow(max({color}.rgb, vec3(0.0)), vec3({gamma})), {color}.a)")]
    [return: Port("result")]
    public static color Run([GlslDefault("vec4(1.0)")] [Port("color")] color source, float gamma = 1f) => throw GpuOnly;
}
