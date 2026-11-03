// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The UTF-16 unit with that number, 0 to 65535; the null character for any other number.
[Node("Strings/Characters/Code To Char", Description = "The UTF-16 unit with that number, 0 to 65535; the null character for any other number.",
    Keywords = "chr, ascii, from code, utf16, number to character")]
public sealed class CodeToChar : ValueNode
{
    [return: Port("character")]
    public static char Run(int code) => (uint)code <= char.MaxValue ? (char)code : '\0';
}
