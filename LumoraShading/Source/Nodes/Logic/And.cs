// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Shading;

[Node("Logic/And", Description = "Returns true when both inputs are true.")]
public sealed class And : ShaderNode
{
    [Glsl("({a} && {b})")]
    public static bool Run(bool a, bool b) => throw GpuOnly;
}
