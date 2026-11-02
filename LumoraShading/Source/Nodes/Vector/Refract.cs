// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Bends incident through a surface facing normal; eta is the ratio of the two indices of refraction.
[Node("Math/Vectors/Refract", Description = "Bends incident through a surface facing normal; eta is the ratio of the two indices of refraction.")]
public sealed class Refract : ShaderNode
{
    [Glsl("refract({incident}, {normal}, {eta})")]
    public static float3 Run(float3 incident, float3 normal, float eta = 0.75f) => throw GpuOnly;
}
