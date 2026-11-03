// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Text;

namespace Lumora.Script;

// One header's value out of headers text, one "Name: value" per line as HTTP Request gives them, the
// name matched whatever its case. A header given on several lines comes back joined by ", ", the way HTTP
// folds them. found is false when there is none.
[Node("Network/HTTP/Header Value", Description = "One header's value out of headers text, one \"Name: value\" per line as HTTP Request gives them, the name matched whatever its case.")]
public sealed class HeaderValue : ValueNode
{
    [return: Port("value")]
    public static string Run(string? headers, string? name, out bool found)
    {
        found = false;
        name = name?.Trim();
        if (string.IsNullOrEmpty(headers) || string.IsNullOrEmpty(name))
            return string.Empty;
        var value = new StringBuilder();
        foreach (var line in headers.AsSpan().EnumerateLines())
        {
            int colon = line.IndexOf(':');
            if (colon <= 0 || !line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (found)
                value.Append(", ");
            value.Append(line[(colon + 1)..].Trim());
            found = true;
        }
        return value.ToString();
    }
}
