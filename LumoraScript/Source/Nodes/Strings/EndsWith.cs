// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// Exact and case sensitive.
[Node("Strings/Ends With", Description = "Checks whether text ends with the given suffix. Matching is case-sensitive.")]
public sealed class EndsWith : ValueNode
{
    public static bool Run(string? text, string? end) => (text ?? string.Empty).EndsWith(end ?? string.Empty, StringComparison.Ordinal);
}
