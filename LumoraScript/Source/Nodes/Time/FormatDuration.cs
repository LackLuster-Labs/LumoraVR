// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;

namespace Lumora.Script;

// A span as "1:05:09", hours then minutes and seconds, the hours running on past a day; with that many
// decimals of a second, 0 to 7. Cut rather than rounded, so a clock never shows a second it has not
// reached. A negative span leads with a minus, unless what shows is all zeros.
[Node("Time/Spans/Format Duration", Description = "A span as \"1:05:09\", hours then minutes and seconds, the hours running on past a day; with that many decimals of a second, 0 to 7.",
    Keywords = "timer text, mm:ss, countdown text, time to text, timespan format")]
public sealed class FormatDuration : ValueNode
{
    private static readonly ulong[] PerDigit = { 10_000_000, 1_000_000, 100_000, 10_000, 1_000, 100, 10, 1 };

    [return: Port("text")]
    public static string Run(TimeSpan span, int decimals)
    {
        decimals = Math.Clamp(decimals, 0, 7);
        long ticks = span.Ticks;
        // The shortest span has no positive twin as a long, so the size is taken unsigned.
        ulong size = ticks < 0 ? (ulong)-(ticks + 1) + 1 : (ulong)ticks;
        ulong seconds = size / TimeSpan.TicksPerSecond;
        ulong fraction = size % TimeSpan.TicksPerSecond / PerDigit[decimals];
        bool negative = ticks < 0 && (seconds > 0 || fraction > 0);

        var text = string.Create(CultureInfo.InvariantCulture,
            $"{(negative ? "-" : "")}{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}");
        return decimals == 0 ? text : text + "." + fraction.ToString(CultureInfo.InvariantCulture).PadLeft(decimals, '0');
    }

    // By a pattern such as "mm\:ss" or "hh\:mm\:ss" (colons escaped), or a one-letter form such as "g", with
    // invariant digits. Empty is "c", "1.02:03:04"; a pattern that does not read gives empty text.
    [return: Port("text")]
    public static string Run(TimeSpan span, string? format)
    {
        try
        {
            return span.ToString(string.IsNullOrEmpty(format) ? "c" : format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }
}
