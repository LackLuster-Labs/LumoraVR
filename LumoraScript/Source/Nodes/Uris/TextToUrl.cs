// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// The address text writes out, when it is a whole one that names its scheme, like
// https://example.com/page; valid is false, and the url nothing, for anything else: a partial address,
// a bare file path, or text that is no address at all. Spaces at either end are dropped.
[Node("Network/URLs/Text To URL", Description = "Reads an absolute URL from text, including its scheme, and reports whether it is valid.",
    Keywords = "uri, parse url, link, string to uri")]
public sealed class TextToUrl : ValueNode
{
    [return: Port("url")]
    public static Uri? Run(string? text, out bool valid)
    {
        valid = false;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        // A bare path reads as a file address on some machines and not on others; an address that names
        // its scheme reads the same everywhere.
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || !text.StartsWith(url.Scheme + ":", StringComparison.OrdinalIgnoreCase))
            return null;
        valid = true;
        return url;
    }
}
