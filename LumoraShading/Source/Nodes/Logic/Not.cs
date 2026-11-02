// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Shading;

[Node("Logic/Not", Description = "Returns the opposite boolean value.")]
public sealed class Not : ShaderNode
{
    [Glsl("(!{value})")]
    public static bool Run(bool value) => throw GpuOnly;
}
