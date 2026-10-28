// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// The shader code a value node's overload stands for, so one node is the same node in scripts and in shader
// graphs: scripts run its C# body, a shader graph its code. Written the way a shader node's own code is: ports
// as {name}, and code with no semicolon is an expression that becomes the one output. Only overloads over
// floats, vectors and colours can carry it, and the shader browser offers only the overloads that do. A node a
// shader graph already has under the same name keeps the shader one. -xlinka
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class GpuAttribute : Attribute
{
    public string Code { get; }

    // Functions from the shader library the code calls.
    public string[] Needs { get; init; } = Array.Empty<string>();

    public GpuAttribute(string code)
    {
        Code = code;
    }
}
