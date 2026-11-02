// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Shading;

[Node("Input/Value", Description = "Provides a constant number for the shader.")]
public sealed class Value : ShaderNode
{
    [Glsl("{value}")]
    [return: Port("result")]
    public static float Run([Setting] float value) => throw GpuOnly;
}
