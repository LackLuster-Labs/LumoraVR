// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Shading;

// The angle of the point (x, y) in radians, from -pi to pi.
[Node("Math/Trigonometry/Arctangent2", Description = "The angle of the point (x, y) in radians, from -pi to pi.")]
public sealed class Arctangent2 : ShaderNode
{
    [Glsl("atan({y}, {x})")]
    public static float Run(float y, float x) => throw GpuOnly;
}
