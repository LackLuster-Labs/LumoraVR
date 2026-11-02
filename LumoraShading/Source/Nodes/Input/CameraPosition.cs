// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// The viewing eye, in world space.
[Node("Input/Camera Position", Description = "The viewing eye, in world space.")]
public sealed class CameraPosition : ShaderNode
{
    [Glsl("CAMERA_POSITION_WORLD")]
    [return: Port("position")]
    public static float3 Run() => throw GpuOnly;
}
