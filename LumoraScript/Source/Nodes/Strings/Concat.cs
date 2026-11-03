// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

[Variadic]
[Node("Strings/Concat", Description = "Joins text values together in order.",
    Keywords = "concatenate, join, combine, append, plus, merge text")]
public sealed class Concat : ValueNode
{
    public static string Run(string? a, string? b) => TextFormat.Join(a, b);
}
