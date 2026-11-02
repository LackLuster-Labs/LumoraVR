// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Where the pixel is on the screen, 0 to 1. Per pixel only.
[Node("Input/Screen Position", Description = "Where the pixel is on the screen, 0 to 1.")]
public sealed class ScreenPosition : ShaderNode
{
    [Glsl("SCREEN_UV", Stages = ShaderStage.Fragment)]
    [return: Port("uv")]
    public static float2 Run() => throw GpuOnly;
}
