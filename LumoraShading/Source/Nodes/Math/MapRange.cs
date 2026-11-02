// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Shading;

// Moves value from one range to another, unclamped.
[Node("Math/Interpolation/Map Range", Description = "Moves value from one range to another, unclamped.")]
public sealed class MapRange : ShaderNode
{
    [Glsl("lm_map_range({value}, {fromMin}, {fromMax}, {toMin}, {toMax})", Needs = new[] { "lm_map_range" })]
    public static float Run(float value, float fromMin = 0f, float fromMax = 1f, float toMin = 0f, float toMax = 1f) => throw GpuOnly;
}
