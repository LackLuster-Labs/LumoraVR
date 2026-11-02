// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Reads a normal map texture's color as a tangent space normal for the Material Output's
// normal. Only its red and green are used, so maps stored in two channels work too.
[Node("Math/Vectors/Normal Map", Description = "Reads a normal map texture's color as a tangent space normal for the Material Output's normal.")]
public sealed class NormalMap : ShaderNode
{
    [Glsl("lm_unpack_normal({color}, {strength})", Needs = new[] { "lm_unpack_normal" })]
    public static float3 Run([Port("color")] color source, float strength = 1f) => throw GpuOnly;
}
