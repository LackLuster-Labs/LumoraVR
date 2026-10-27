// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lumora.Core.Assets;

namespace Lumora.Core.Components.Chat;

// What a chat line may hold, and how a user's record carries their last few.
//
// A line is one row of plain text: no control characters, no direction overrides, nothing a text renderer
// reads as a tag, at most MaxLength UTF-16 units. Cleaned where it is typed AND wherever it is read back,
// because the record is written by the other machine and a modified client sends whatever it likes.
//
// The record is the last LogDepth lines, oldest first, "number space text", one per line. A newline can
// never be inside a line, the clean takes it out, so the split is unambiguous. -xlinka
public static class ChatText
{
    public const int MaxLength = 300;

    // Lines one record carries. A burst this deep between two reads of the record still all arrives.
    public const int LogDepth = 4;

    // What a record may run to before the rest of it is not read at all.
    private const int MaxLogChars = LogDepth * (MaxLength + 16);
    private const int MaxSeqDigits = 9;

    private const string Ellipsis = "...";

    public static string Clean(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var builder = new StringBuilder(System.Math.Min(raw.Length, MaxLength * 2));
        bool space = false;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            int codepoint = c;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= raw.Length || !char.IsLowSurrogate(raw[i + 1]))
                    continue;
                codepoint = char.ConvertToUtf32(c, raw[i + 1]);
            }
            else if (char.IsLowSurrogate(c))
            {
                continue;
            }

            if (IsBreak(codepoint))
            {
                space = builder.Length > 0;
                continue;
            }
            if (Dropped(codepoint))
            {
                if (codepoint > 0xFFFF)
                    i++;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }
            builder.Append(c);
            if (codepoint > 0xFFFF)
                builder.Append(raw[++i]);
        }

        string text = StripMarkup(builder.ToString()).Trim();
        // A tag taken out from between two words leaves both their spaces behind.
        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        if (text.Length > MaxLength)
        {
            int cut = MaxLength;
            if (char.IsHighSurrogate(text[cut - 1]))
                cut--;
            text = text.Substring(0, cut).TrimEnd();
        }
        return text;
    }

    // Spaces, tabs and every kind of line break all become one space.
    private static bool IsBreak(int codepoint)
        => codepoint is ' ' or '\t' or '\n' or '\r' or '\v' or '\f' or 0x85 or 0x2028 or 0x2029 or 0x3000;

    // Control characters, the marks that turn text round or hide it, and the placeholder the shaper draws an
    // inline picture in place of.
    private static bool Dropped(int codepoint)
    {
        if (codepoint < 0x20 || codepoint is >= 0x7F and <= 0x9F)
            return true;
        return codepoint is 0x200E or 0x200F or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069)
            or 0xFEFF or 0xFFFC or 0xFFFD;
    }

    // Anything between angle brackets that opens with a word, a slash and a word, or a '#' is a tag to the rich
    // text reader, known or not, and goes whole. Run again until nothing goes, so "<<b>b>" cannot leave a tag
    // behind once its inside is taken out. A '<' that starts no tag ("<3", "a < b") stays. -xlinka
    private static string StripMarkup(string text)
    {
        if (text.IndexOf('<') < 0)
            return text;

        for (int pass = 0; pass < 8; pass++)
        {
            var builder = new StringBuilder(text.Length);
            bool removed = false;
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '<')
                {
                    int close = text.IndexOf('>', i + 1);
                    if (close > i && IsTag(text, i + 1, close))
                    {
                        removed = true;
                        i = close + 1;
                        continue;
                    }
                }
                builder.Append(c);
                i++;
            }
            text = builder.ToString();
            if (!removed || text.IndexOf('<') < 0)
                break;
        }
        return text;
    }

    private static bool IsTag(string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start]))
            start++;
        if (start < end && text[start] == '/')
        {
            start++;
            while (start < end && char.IsWhiteSpace(text[start]))
                start++;
        }
        if (start >= end)
            return false;
        char first = text[start];
        return first == '#' || char.IsLetter(first);
    }

    // THE RECORD

    public static string Pack(IReadOnlyList<(int Seq, string Text)> lines)
    {
        if (lines == null || lines.Count == 0)
            return string.Empty;
        var builder = new StringBuilder();
        int start = System.Math.Max(0, lines.Count - LogDepth);
        for (int i = start; i < lines.Count; i++)
        {
            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append(lines[i].Seq.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Clean(lines[i].Text));
        }
        return builder.ToString();
    }

    // The lines a record holds, oldest first, each cleaned again. Returns the highest number in it, 0 for none.
    // A record line that does not start with a number and a space is skipped, not guessed at.
    public static int Parse(string? log, List<(int Seq, string Text)> into)
    {
        into.Clear();
        if (string.IsNullOrEmpty(log))
            return 0;

        int length = System.Math.Min(log.Length, MaxLogChars);
        int highest = 0;
        int start = 0;
        while (start < length && into.Count < LogDepth * 2)
        {
            int end = log.IndexOf('\n', start, length - start);
            if (end < 0)
                end = length;
            int digits = 0;
            int seq = 0;
            while (start + digits < end && digits < MaxSeqDigits && log[start + digits] is >= '0' and <= '9')
            {
                seq = seq * 10 + (log[start + digits] - '0');
                digits++;
            }
            if (digits > 0 && start + digits < end && log[start + digits] == ' ' && seq > 0)
            {
                string text = Clean(log.Substring(start + digits + 1, end - start - digits - 1));
                into.Add((seq, text));
                if (seq > highest)
                    highest = seq;
            }
            start = end + 1;
        }
        into.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
        while (into.Count > LogDepth)
            into.RemoveAt(0);
        return highest;
    }

    // WRAPPING

    // Breaks a line into at most maxLines rows no wider than maxWidth, the last one cut with dots when it
    // does not all fit. With a font the rows are the shaper's own; without one (still loading) the widths
    // are guessed from a typical glyph so a bubble can be laid out before its face arrives.
    public static string Wrap(TextShaper shaper, FontSet? font, string text, float size, float maxWidth, int maxLines,
        out float width, out int lines)
    {
        width = 0f;
        lines = 0;
        if (string.IsNullOrEmpty(text) || maxLines <= 0)
            return string.Empty;

        var rows = new List<string>();
        var widths = new List<float>();
        if (font == null || !font.IsValid || !ShapedRows(shaper, font, text, size, maxWidth, maxLines, rows, widths))
            GuessedRows(text, size, maxWidth, maxLines, rows, widths);

        for (int i = 0; i < widths.Count; i++)
            width = System.MathF.Max(width, widths[i]);
        lines = rows.Count;
        return string.Join('\n', rows);
    }

    private static bool ShapedRows(TextShaper shaper, FontSet font, string text, float size, float maxWidth, int maxLines,
        List<string> rows, List<float> widths)
    {
        if (!ShapesAny(font, text, size))
            return false;
        TextShaper.RequestGlyphs(font, text, size);
        TextShaper.RequestGlyphs(font, Ellipsis, size);
        shaper.Shape(font, text, size, wrap: true, maxWidth: maxWidth);
        var shaped = shaper.Lines;
        for (int i = 0; i < shaped.Count; i++)
        {
            var line = shaped[i];
            if (line.Glyphs.Count == 0)
                continue;
            int from = line.Glyphs[0].SourceIndex;
            int last = line.Glyphs[line.Glyphs.Count - 1].SourceIndex;
            int to = last + (char.IsHighSurrogate(text[last]) && last + 1 < text.Length ? 2 : 1);
            rows.Add(text.Substring(from, to - from).Trim());
            widths.Add(line.Width);
        }
        if (rows.Count == 0)
            return false;
        if (rows.Count <= maxLines)
            return true;

        rows.RemoveRange(maxLines, rows.Count - maxLines);
        widths.RemoveRange(maxLines, widths.Count - maxLines);
        string tail = rows[maxLines - 1];
        float tailWidth = 0f;
        for (int attempt = 0; attempt < 64; attempt++)
        {
            string candidate = tail.TrimEnd() + Ellipsis;
            shaper.Shape(font, candidate, size, wrap: false, maxWidth: 0f);
            tailWidth = shaper.MaxLineWidth;
            if (tailWidth <= maxWidth || tail.Length == 0)
            {
                tail = candidate;
                break;
            }
            int drop = char.IsLowSurrogate(tail[tail.Length - 1]) && tail.Length > 1 ? 2 : 1;
            tail = tail.Substring(0, tail.Length - drop);
        }
        rows[maxLines - 1] = tail;
        widths[maxLines - 1] = tailWidth;
        return true;
    }

    private static void GuessedRows(string text, float size, float maxWidth, int maxLines, List<string> rows, List<float> widths)
    {
        rows.Clear();
        widths.Clear();
        float glyph = size * 0.5f;
        int perRow = System.Math.Max(4, (int)(maxWidth / glyph));
        int index = 0;
        while (index < text.Length)
        {
            int take = System.Math.Min(perRow, text.Length - index);
            if (index + take < text.Length)
            {
                int space = text.LastIndexOf(' ', index + take, take);
                if (space > index)
                    take = space - index;
            }
            string row = text.Substring(index, take).Trim();
            if (row.Length > 0)
            {
                rows.Add(row);
                widths.Add(row.Length * glyph);
            }
            index += take;
            while (index < text.Length && text[index] == ' ')
                index++;
        }
        if (rows.Count <= maxLines)
            return;
        rows.RemoveRange(maxLines, rows.Count - maxLines);
        widths.RemoveRange(maxLines, widths.Count - maxLines);
        string tail = rows[maxLines - 1];
        int keep = System.Math.Max(0, System.Math.Min(tail.Length, perRow - Ellipsis.Length));
        rows[maxLines - 1] = tail.Substring(0, keep).TrimEnd() + Ellipsis;
        widths[maxLines - 1] = rows[maxLines - 1].Length * glyph;
    }

    // A face that has loaded but holds none of the line's glyphs yet shapes it to nothing, and the rows have to
    // come from somewhere until they arrive.
    private static bool ShapesAny(FontSet font, string text, float size)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]) || char.IsLowSurrogate(text[i]))
                continue;
            int codepoint = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
            TextShaper.RequestGlyphs(font, char.ConvertFromUtf32(codepoint), size);
            return font.TryGetGlyph(codepoint, size, out _, out _, out _);
        }
        return false;
    }
}
