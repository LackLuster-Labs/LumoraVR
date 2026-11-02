// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Distance to the nearest of a field of points, and a random value per point's cell. Angle offset
// moves the points.
[Node("Procedural/Voronoi", Description = "Distance to the nearest of a field of points, and a random value per point's cell.")]
public sealed class Voronoi : ShaderNode
{
    [Glsl("lm_voronoi({uv}, {angleOffset}, {cellDensity}, {distance}, {cells});", Needs = new[] { "lm_voronoi" })]
    [return: Port("distance")]
    public static float Run([GlslDefault("UV")] float2 uv, out float cells, float angleOffset = 2f, float cellDensity = 5f) => throw GpuOnly;
}
