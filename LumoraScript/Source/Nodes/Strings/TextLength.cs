// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

[Node("Strings/Length", Description = "Returns the number of UTF-16 characters in the text.",
    Keywords = "text length, character count, size, string")]
public sealed class TextLength : ValueNode
{
    public static int Run(string? text) => text?.Length ?? 0;
}
