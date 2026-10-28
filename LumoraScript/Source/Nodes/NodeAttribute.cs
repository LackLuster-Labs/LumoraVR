// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// Makes a class a node. The path is where the browser files it; the class name is what a graph calls
// it. Bump Version when the ports change, and give the class a static Migrate for older graphs.
// Follow WRITING_NODES.md for categories; moving a path does not rename the node. -xlinka
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NodeAttribute : Attribute
{
    public string Path { get; }
    public string Description { get; init; } = string.Empty;

    // More words the browser's search finds the node by, comma-separated: what people type looking for it
    // when that is not its title, such as "random float3" for Random Vector.
    public string Keywords { get; init; } = string.Empty;
    public Capability Capability { get; }
    public int Version { get; init; } = 1;

    public NodeAttribute(string path, Capability capability = Capability.Core)
    {
        Path = path;
        Capability = capability;
    }
}
