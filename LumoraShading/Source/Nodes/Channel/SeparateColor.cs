// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

[Node("Color/Packing/Separate Color", Description = "Splits a colour into its red, green, blue and alpha channels.")]
public sealed class SeparateColor : ShaderNode
{
    [Glsl("{r} = {color}.r; {g} = {color}.g; {b} = {color}.b; {a} = {color}.a;")]
    [return: Port("r")]
    public static float Run([Port("color")] color source, out float g, out float b, out float a) => throw GpuOnly;
}
