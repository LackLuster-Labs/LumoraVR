// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

[Node("Color/Packing/Combine Color", Description = "Combines red, green, blue and alpha values into a colour.")]
public sealed class CombineColor : ShaderNode
{
    [Glsl("vec4({r}, {g}, {b}, {a})")]
    [return: Port("color")]
    public static color Run(float r, float g, float b, float a = 1f) => throw GpuOnly;
}
