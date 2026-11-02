// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Shading;

// Seconds since this machine started, so it is not the same moment for everyone.
[Node("Input/Time", Description = "Seconds since this machine started, so it is not the same moment for everyone.")]
public sealed class Time : ShaderNode
{
    [Glsl("{time} = TIME; {sine} = sin(TIME); {cosine} = cos(TIME);")]
    [return: Port("time")]
    public static float Run(out float sine, out float cosine) => throw GpuOnly;
}
