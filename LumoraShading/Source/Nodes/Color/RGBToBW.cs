// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Brightness as people see it.
[Node("Color/RGB to BW", Description = "Brightness as people see it.")]
public sealed class RGBToBW : ShaderNode
{
    [Glsl("dot({color}.rgb, vec3(0.2126, 0.7152, 0.0722))")]
    [return: Port("value")]
    public static float Run([GlslDefault("vec4(1.0)")] [Port("color")] color source) => throw GpuOnly;
}
