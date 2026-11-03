// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// Text made safe to put inside an address: every character but letters, digits and - _ . ~ written as
// the %XX codes of its UTF-8 bytes, so a space is %20 and a slash %2F.
[Node("Network/URLs/Escape URL Text", Description = "Text made safe to put inside an address: every character but letters, digits and - _ . ~ written as the %XX codes of its UTF-8 bytes, so a space is %20 and a slash %2F.",
    Keywords = "uri, url encode, urlencode, percent encode, escape uri")]
public sealed class EscapeUrlText : ValueNode
{
    public static string Run(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return Uri.EscapeDataString(text);
    }
}
