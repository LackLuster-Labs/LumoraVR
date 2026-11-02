// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

[Node("Input/RGB", Description = "Provides a constant colour for the shader.")]
public sealed class RGB : ShaderNode
{
    [Glsl("{value}")]
    [return: Port("color")]
    public static color Run([Setting, GlslDefault("vec4(1.0)")] color value) => throw GpuOnly;
}
