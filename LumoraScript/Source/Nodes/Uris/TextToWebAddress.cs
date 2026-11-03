// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// A web address from text: a whole http or https address as written, or text with no scheme, such as
// example.com/page, read as http://example.com/page. valid is false, and the url nothing, for anything else,
// another scheme included. example.com:8080 is a host and a port here, never an address whose scheme is
// "example.com", and text that starts with a scheme of its own, like mailto: or data:, is not made into a
// web address. Spaces at either end are dropped.
[Node("Network/URLs/Text To Web Address", Description = "Reads an HTTP or HTTPS address from text. Adds http:// when the scheme is missing.",
    Keywords = "uri, website, link, http, parse url")]
public sealed class TextToWebAddress : ValueNode
{
    [return: Port("url")]
    public static Uri? Run(string? text, out bool valid)
    {
        valid = false;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            if (NamesOtherScheme(text))
                return null;
            text = "http://" + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(url.Host))
            return null;
        valid = true;
        return url;
    }

    // "name:" followed by anything but a port is a scheme, not a host.
    private static bool NamesOtherScheme(string text)
    {
        if (text[0] == '[')
            return false;
        int colon = text.IndexOf(':');
        if (colon < 0)
            return false;
        int slash = text.IndexOfAny(new[] { '/', '?', '#' });
        if (slash >= 0 && slash < colon)
            return false;
        int end = colon + 1;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
            end++;
        bool port = end > colon + 1 && (end == text.Length || text[end] is '/' or '?' or '#');
        return !port;
    }
}
