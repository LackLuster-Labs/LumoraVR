// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

[Node("Math/Add", Description = "Adds the input values together.")]
public sealed class Add : ShaderNode
{
    [Glsl("{a} + {b}")]
    public static float Run(float a, float b) => throw GpuOnly;

    [Glsl("{a} + {b}")]
    public static float2 Run(float2 a, float2 b) => throw GpuOnly;

    [Glsl("{a} + {b}")]
    public static float3 Run(float3 a, float3 b) => throw GpuOnly;

    [Glsl("{a} + {b}")]
    public static float4 Run(float4 a, float4 b) => throw GpuOnly;

    [Glsl("{a} + {b}")]
    public static color Run(color a, color b) => throw GpuOnly;
}
