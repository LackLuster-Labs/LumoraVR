// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Alpha stays as it is.
[Node("Color/Invert", Description = "Alpha stays as it is.")]
public sealed class Invert : ShaderNode
{
    [Glsl("vec4(mix({color}.rgb, 1.0 - {color}.rgb, {factor}), {color}.a)")]
    [return: Port("result")]
    public static color Run([GlslDefault("vec4(1.0)")] [Port("color")] color source, float factor = 1f) => throw GpuOnly;
}
