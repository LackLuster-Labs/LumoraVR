// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;
using System.Text;

namespace Lumora.Script;

// The date as text by a pattern such as "yyyy-MM-dd HH:mm" or a one-letter form such as "D", with
// invariant names and digits. Empty is "yyyy-MM-dd HH:mm:ss"; a pattern that does not read gives empty
// text.
//
// The machine's time zone is never read, so the same date gives the same text on every machine and a
// Shared script cannot learn the host's zone through it. z, zz, zzz and K show a UTC date's zone and
// nothing for a local or unspecified date, which carries no offset of its own; U and o do not convert
// either, U reading as F. -xlinka
[Node("Time/Dates/Format Date", Description = "The date as text by a pattern such as \"yyyy-MM-dd HH:mm\" or a one-letter form such as \"D\", with invariant names and digits.",
    Keywords = "date to text, date string, datetime format")]
public sealed class FormatDate : ValueNode
{
    private const string Plain = "yyyy-MM-dd HH:mm:ss";
    private const string RoundTrip = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffffK";

    [return: Port("text")]
    public static string Run(DateTime date, string? format)
    {
        if (string.IsNullOrEmpty(format))
            format = Plain;

        string? pattern = format switch
        {
            "o" or "O" => Zoned(RoundTrip, date.Kind),
            "U" => "F",
            { Length: 1 } => format,
            _ => Zoned(format, date.Kind),
        };
        if (pattern == null)
            return string.Empty;
        try
        {
            return date.ToString(pattern, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    // The zone letters swapped for the date's own zone as quoted text. The pattern is walked the way the
    // runtime reads it, so a z or K inside quotes or after a backslash stays a letter and one after a %
    // is still a zone. Null when a quote or an escape is left open.
    private static string? Zoned(string pattern, DateTimeKind kind)
    {
        if (pattern.AsSpan().IndexOfAny('z', 'K') < 0)
            return pattern;

        var output = new StringBuilder(pattern.Length + 16);
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c is '\'' or '"')
            {
                int end = i + 1;
                while (end < pattern.Length && pattern[end] != c)
                    end += pattern[end] == '\\' ? 2 : 1;
                if (end >= pattern.Length)
                    return null;
                output.Append(pattern, i, end - i + 1);
                i = end;
            }
            else if (c == '\\')
            {
                if (i + 1 >= pattern.Length)
                    return null;
                output.Append(pattern, i, 2);
                i++;
            }
            else if (c == '%' && i + 1 < pattern.Length && pattern[i + 1] is 'z' or 'K')
            {
                output.Append(Zone(pattern[i + 1], 1, kind));
                i++;
            }
            else if (c == 'z')
            {
                int count = 1;
                while (i + count < pattern.Length && pattern[i + count] == 'z')
                    count++;
                output.Append(Zone('z', count, kind));
                i += count - 1;
            }
            else if (c == 'K')
            {
                output.Append(Zone('K', 1, kind));
            }
            else
            {
                output.Append(c);
            }
        }
        return output.ToString();
    }

    private static string Zone(char letter, int count, DateTimeKind kind)
    {
        if (kind != DateTimeKind.Utc)
            return "''";
        if (letter == 'K')
            return "'Z'";
        return count switch
        {
            1 => "'+0'",
            2 => "'+00'",
            _ => "'+00:00'",
        };
    }
}
