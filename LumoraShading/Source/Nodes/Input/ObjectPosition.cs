// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// The object's origin, in world space.
[Node("Input/Object Position", Description = "The object's origin, in world space.")]
public sealed class ObjectPosition : ShaderNode
{
    [Glsl("NODE_POSITION_WORLD")]
    [return: Port("position")]
    public static float3 Run() => throw GpuOnly;
}
