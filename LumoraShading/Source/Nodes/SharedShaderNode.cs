// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Shading;

// What a shader graph runs for one of Loom's value nodes: the [Gpu] code on its overload. Not a node of its own,
// so it never shows in a browser. -xlinka
internal sealed class SharedShaderNode : ShaderNode
{
    public static readonly SharedShaderNode Instance = new();
}
