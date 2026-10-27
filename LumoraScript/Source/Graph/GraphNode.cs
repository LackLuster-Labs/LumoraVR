// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Script;

public sealed class GraphNode
{
    public const int MaxIdLength = 64;

    public string Id { get; }
    public string Type { get; }
    public int Version { get; set; } = 1;
    public string? Signature { get; set; }
    public float3 Position { get; set; }
    // In its graph's frame. A node square to its graph writes none.
    public floatQ Rotation { get; set; } = floatQ.Identity;
    public Dictionary<string, GraphValue> Values { get; } = new(StringComparer.Ordinal);

    public GraphNode(string id, string type)
    {
        if (!IsValidId(id))
            throw new ArgumentException($"'{id}' is not a usable node id");
        Id = id;
        Type = type;
    }

    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength)
            return false;
        foreach (char c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')
                return false;
        }
        return true;
    }

    public override string ToString() => $"{Id} ({Type})";
}
