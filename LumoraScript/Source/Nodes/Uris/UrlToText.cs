// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// An address as it was written; empty for no url.
[Node("Network/URLs/URL To Text", Description = "An address as it was written; empty for no url.",
    Keywords = "uri, url to string, link text")]
public sealed class UrlToText : ValueNode
{
    [return: Port("text")]
    public static string Run(Uri? url) => url?.OriginalString ?? string.Empty;
}
