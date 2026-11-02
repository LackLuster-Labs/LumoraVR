// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

[Node("Input/Vertex Color", Description = "Reads the colour stored on the mesh's vertices.")]
public sealed class VertexColor : ShaderNode
{
    [Glsl("COLOR")]
    [return: Port("color")]
    public static color Run() => throw GpuOnly;
}
