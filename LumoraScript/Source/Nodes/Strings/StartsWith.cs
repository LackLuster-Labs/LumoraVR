// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// Exact and case sensitive.
[Node("Strings/Starts With", Description = "Checks whether text starts with the given prefix. Matching is case-sensitive.")]
public sealed class StartsWith : ValueNode
{
    public static bool Run(string? text, string? start) => (text ?? string.Empty).StartsWith(start ?? string.Empty, StringComparison.Ordinal);
}
