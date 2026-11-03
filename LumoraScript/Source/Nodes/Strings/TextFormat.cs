// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;
using System.Text;
using Lumora.Core.Math;

namespace Lumora.Script;

// What the string nodes share. Format fills {0} and nothing else: string.Format would take an
// alignment like {0,1000000000} from a graph and try to build it. -xlinka
internal static class TextFormat
{
    public static string Of(object? value) => value switch
    {
        null => string.Empty,
        bool flag => flag ? "true" : "false",
        // The vectors' own ToString writes the machine's decimal comma; text a graph makes must read the
        // same everywhere. -xlinka
        float2 v => string.Create(CultureInfo.InvariantCulture, $"({v.x}, {v.y})"),
        float3 v => string.Create(CultureInfo.InvariantCulture, $"({v.x}, {v.y}, {v.z})"),
        float4 v => string.Create(CultureInfo.InvariantCulture, $"({v.x}, {v.y}, {v.z}, {v.w})"),
        color c => string.Create(CultureInfo.InvariantCulture, $"({c.r:F3}, {c.g:F3}, {c.b:F3}, {c.a:F3})"),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    public static string Fill(string? format, object? argument)
    {
        if (string.IsNullOrEmpty(format))
            return string.Empty;

        var text = Of(argument);
        var output = new StringBuilder(format.Length + text.Length);
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c == '{' && i + 1 < format.Length && format[i + 1] == '{')
            {
                output.Append('{');
                i++;
            }
            else if (c == '}' && i + 1 < format.Length && format[i + 1] == '}')
            {
                output.Append('}');
                i++;
            }
            else if (c == '{' && i + 2 < format.Length && format[i + 1] == '0' && format[i + 2] == '}')
            {
                output.Append(text);
                i += 2;
            }
            else
            {
                output.Append(c);
            }
        }
        return output.ToString();
    }

    public static string Join(string? a, string? b) => string.Concat(a, b);
}
