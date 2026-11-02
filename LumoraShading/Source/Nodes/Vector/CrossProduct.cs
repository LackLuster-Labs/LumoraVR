// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

[Node("Math/Vectors/Cross Product", Description = "Returns a vector perpendicular to both input vectors, with direction determined by their order.")]
public sealed class CrossProduct : ShaderNode
{
    [Glsl("cross({a}, {b})")]
    public static float3 Run(float3 a, float3 b) => throw GpuOnly;
}
