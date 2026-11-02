// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Shading;

// Where value sits between a and b: 0 at a, 1 at b.
[Node("Math/Interpolation/Inverse Lerp", Description = "Where value sits between a and b: 0 at a, 1 at b.")]
public sealed class InverseLerp : ShaderNode
{
    [Glsl("(({value} - {a}) / ({b} - {a}))")]
    public static float Run(float value, float a = 0f, float b = 1f) => throw GpuOnly;
}
