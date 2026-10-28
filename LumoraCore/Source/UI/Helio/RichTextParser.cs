// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Helio.UI;

// Parses inline rich-text markup into stripped visible text plus per-character color, style flags, size
// multiplier and highlight color, so Text can render styled spans.
// Supported: <color>, <alpha>, <b>/<i>/<u>/<s>, <size=N|N%|Nem>, <sub>/<sup>, <mark>/<mark=color>, <nobr>,
// <gradient=a,b>, <lowercase>/<uppercase>/<smallcaps>/<allcaps>, <align>, <line-height>, <font>, <sprite>, <br>,
// <noparse>...</noparse>, <noparse=N>, <closeallblock> and </closeall>.
//
// A closing tag closes the latest open tag of its own name, whatever was opened after it: those stay open and
// apply again on top. A closing tag that matches nothing open, a tag nobody knows and a known tag with a value
// it cannot read are all drawn as written. A colour without its own alpha keeps the alpha it lands on; one
// with an alpha, and <alpha>, multiply it. -xlinka
public static class RichTextParser
{
    public const byte StyleBold = 1;
    public const byte StyleItalic = 2;
    public const byte StyleUnderline = 4;
    public const byte StyleStrike = 8;
    public const byte StyleSub = 16;
    public const byte StyleSup = 32;
    public const byte StyleNoBreak = 64;

    // Case transform applied to a run: uppercase/lowercase change the visible char; smallcaps uppercases and
    // renders originally-lowercase letters smaller. -xlinka
    public const byte CaseNone = 0;
    public const byte CaseLower = 1;
    public const byte CaseUpper = 2;
    public const byte CaseSmallCaps = 3;

    // Subscript and superscript draw at half the size of the text around them; the renderer shifts them off
    // the baseline by a share of that size (SupRise, SubDrop). Shared so parser and renderers agree.
    public const float SubSupScale = 0.5f;
    public const float SupRise = 0.5f;
    public const float SubDrop = 0.2f;
    // Smallcaps renders originally-lowercase letters at this fraction of the run size.
    public const float SmallCapsScale = 0.75f;

    // Default highlight when <mark> carries no color: soft translucent yellow behind the text. A mark color
    // given without an alpha of its own takes MarkAlpha. -xlinka
    private static readonly color DefaultMark = new(1f, 0.92f, 0.35f, 0.4f);
    private const float MarkAlpha = 0.25f;
    private static readonly color NoMark = new(0f, 0f, 0f, 0f);

    private struct State
    {
        public color Color;
        public byte Style;
        public float Size;
        public color Mark;
        public byte Case;
        public byte Align;
        public float LineHeight;
        public string? Font;
    }

    // An open tag: its name for matching a close, its body for applying it again after a tag under it
    // closes, and the state it opened on.
    private struct Open
    {
        public string Name;
        public string Body;
        public State Before;
    }

    // Per-line block attributes recorded as SPARSE position markers (align/line-height change per line, not per
    // char, so a marker at the change position is enough - the renderer picks the one active at each line). -xlinka
    public readonly struct AlignMark
    {
        public readonly int Index;
        public readonly byte Align; // 0 = inherit the Text default; else (TextHorizontalAlignment + 1)
        public AlignMark(int index, byte align) { Index = index; Align = align; }
    }

    public readonly struct LineHeightMark
    {
        public readonly int Index;
        public readonly float Height; // 0 = inherit; else multiplier of the base line height
        public LineHeightMark(int index, float height) { Index = index; Height = height; }
    }

    // <font=name> switches the shaping font for the run. Marker holds the font name active from Index onward
    // (null = the Text's default font); the renderer resolves the name to a FontSet and the shaper uses it. -xlinka
    public readonly struct FontMark
    {
        public readonly int Index;
        public readonly string? Name;
        public FontMark(int index, string? name) { Index = index; Name = name; }
    }

    // The base font size the <size> tag resolves against. A bare number in the tag is an absolute size in
    // the same units as the Text's own Size, so <size=26> on 18-unit text is 1.44x, not 26x; a caller that
    // passes no base keeps the old bare-number-is-a-multiplier reading. Thread-static because Parse is
    // static and the shaping threads must not read each other's base.
    [System.ThreadStatic]
    private static float t_baseSize;

    [System.ThreadStatic]
    private static List<Open>? t_stack;

    public static void Parse(string? input, in color baseColor, StringBuilder text, List<color> colors, List<byte> styles, List<float> sizes, List<color> marks, List<string?> sprites,
        List<AlignMark> alignMarks, List<LineHeightMark> lineHeightMarks, List<FontMark> fontMarks, float baseSize = 0f)
    {
        t_baseSize = baseSize;
        text.Clear();
        colors.Clear();
        styles.Clear();
        sizes.Clear();
        marks.Clear();
        sprites.Clear();
        alignMarks.Clear();
        lineHeightMarks.Clear();
        fontMarks.Clear();
        if (string.IsNullOrEmpty(input))
            return;

        var stack = t_stack ??= new List<Open>();
        stack.Clear();
        var current = new State { Color = baseColor, Size = 1f, Mark = NoMark };
        // Gradient runs outside the attribute stack: it back-fills the color of the whole run on close, so it
        // just needs the run's start index and endpoints, not a push/pop. -xlinka
        int gradStart = -1;
        color gradA = color.White, gradB = color.White;

        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];
            if (c == '<')
            {
                int close = input.IndexOf('>', i + 1);
                if (close > i)
                {
                    string body = input.Substring(i + 1, close - i - 1);
                    string trimmed = body.Trim();

                    // <br> emits a newline and <noparse> copies its content literally: both ADD characters, so
                    // they are read before the attribute tags. -xlinka
                    if (trimmed.Equals("br", StringComparison.OrdinalIgnoreCase))
                    {
                        Append('\n', text, colors, styles, sizes, marks, sprites, current, CaseNone);
                        i = close + 1;
                        continue;
                    }
                    if (TryNoParse(trimmed, input, close, out int literalEnd, out int resume))
                    {
                        // Inside the run only a line break still counts.
                        for (int k = close + 1; k < literalEnd; k++)
                        {
                            if (input[k] == '<' && string.Compare(input, k, "<br>", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                            {
                                Append('\n', text, colors, styles, sizes, marks, sprites, current, CaseNone);
                                k += 3;
                                continue;
                            }
                            Append(input[k], text, colors, styles, sizes, marks, sprites, current, current.Case);
                        }
                        i = resume;
                        continue;
                    }
                    // <sprite=name> / <sprite name=name> emits one placeholder char that the renderer draws as a
                    // sprite; it ADDS a character. -xlinka
                    string? spriteName = ParseSpriteName(trimmed);
                    if (spriteName != null)
                    {
                        Append((char)TextShaper.SpriteGlyph, text, colors, styles, sizes, marks, sprites, current, CaseNone, spriteName);
                        i = close + 1;
                        continue;
                    }

                    if (TryHandleGradient(trimmed, colors, ref gradStart, ref gradA, ref gradB))
                    {
                        i = close + 1;
                        continue;
                    }

                    var before = current;
                    bool handled;
                    if (trimmed.Length > 1 && trimmed[0] == '/')
                        handled = TryClose(trimmed.Substring(1).Trim(), stack, ref current);
                    else if (trimmed.Equals("closeall", StringComparison.OrdinalIgnoreCase))
                        handled = TryClose("closeall", stack, ref current);
                    else if (TryApply(trimmed, ref current))
                    {
                        stack.Add(new Open { Name = TagName(trimmed), Body = trimmed, Before = before });
                        handled = true;
                    }
                    else
                        handled = false;

                    if (handled)
                    {
                        MarkBlocks(before, current, text.Length, alignMarks, lineHeightMarks, fontMarks);
                        i = close + 1;
                        continue;
                    }
                }
            }

            Append(c, text, colors, styles, sizes, marks, sprites, current, current.Case);
            i++;
        }
        stack.Clear();
    }

    // The characters a text shows for this markup and nothing else: the tags it reads are gone, <br> is a
    // line break, a sprite is nothing, and a tag it does not read stays as written, as it would on screen.
    public static string Plain(string? input)
    {
        if (string.IsNullOrEmpty(input) || input.IndexOf('<') < 0)
            return input ?? string.Empty;
        var text = new StringBuilder(input.Length);
        var sprites = new List<string?>();
        Parse(input, color.White, text, new List<color>(), new List<byte>(), new List<float>(), new List<color>(), sprites,
            new List<AlignMark>(), new List<LineHeightMark>(), new List<FontMark>());
        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (sprites[i] != null)
                text.Remove(i, 1);
        }
        return text.ToString();
    }

    // <noparse> runs to its </noparse> (or the end); <noparse=N> takes the next N characters as written.
    private static bool TryNoParse(string trimmed, string input, int close, out int literalEnd, out int resume)
    {
        literalEnd = resume = 0;
        if (trimmed.Equals("noparse", StringComparison.OrdinalIgnoreCase))
        {
            int closeTag = input.IndexOf("</noparse>", close + 1, StringComparison.OrdinalIgnoreCase);
            literalEnd = closeTag >= 0 ? closeTag : input.Length;
            resume = closeTag >= 0 ? closeTag + "</noparse>".Length : input.Length;
            return true;
        }
        if (trimmed.Length > 8 && trimmed.StartsWith("noparse", StringComparison.OrdinalIgnoreCase) && trimmed[7] == '='
            && int.TryParse(trimmed.AsSpan(8).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int length) && length >= 0)
        {
            literalEnd = resume = (int)System.Math.Min((long)close + 1 + length, input.Length);
            return true;
        }
        return false;
    }

    private static readonly char[] TagNameEnd = { '=', ' ' };

    private static string TagName(string trimmed)
    {
        int stop = trimmed.IndexOfAny(TagNameEnd);
        return (stop >= 0 ? trimmed.Substring(0, stop) : trimmed).Trim().ToLowerInvariant();
    }

    // Closes the latest open tag called name; the tags opened after it apply again on the state it opened on.
    // </closeall> closes everything opened since the latest <closeallblock>, or everything. Nothing to close
    // means the closing tag is text.
    private static bool TryClose(string name, List<Open> stack, ref State current)
    {
        if (name.Equals("closeall", StringComparison.OrdinalIgnoreCase))
        {
            int block = -1;
            for (int k = stack.Count - 1; k >= 0; k--)
            {
                if (stack[k].Name == "closeallblock")
                {
                    block = k;
                    break;
                }
            }
            int from = block + 1;
            if (from >= stack.Count)
                return false;
            current = stack[from].Before;
            stack.RemoveRange(from, stack.Count - from);
            return true;
        }

        for (int k = stack.Count - 1; k >= 0; k--)
        {
            if (!stack[k].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            current = stack[k].Before;
            stack.RemoveAt(k);
            for (int j = k; j < stack.Count; j++)
            {
                var reopened = stack[j];
                reopened.Before = current;
                TryApply(reopened.Body, ref current);
                stack[j] = reopened;
            }
            return true;
        }
        return false;
    }

    // An opening tag's effect on the state. False for a tag nobody knows or a value it cannot read.
    private static bool TryApply(string tag, ref State state)
    {
        if (tag.Length == 0)
            return false;
        int eq = tag.IndexOf('=');
        string key = (eq >= 0 ? tag.Substring(0, eq) : tag).Trim().ToLowerInvariant();
        string value = eq >= 0 ? tag.Substring(eq + 1).Trim() : string.Empty;

        switch (key)
        {
            case "b":
            case "i":
            case "u":
            case "s":
                if (value.Length > 0)
                    return false;
                state.Style |= key switch { "b" => StyleBold, "i" => StyleItalic, "u" => StyleUnderline, _ => StyleStrike };
                return true;
            case "sub":
            case "sup":
                if (value.Length > 0)
                    return false;
                state.Style |= key == "sub" ? StyleSub : StyleSup;
                state.Size *= SubSupScale;
                return true;
            case "nobr":
                if (value.Length > 0)
                    return false;
                state.Style |= StyleNoBreak;
                return true;
            case "lowercase":
            case "uppercase":
            case "smallcaps":
            case "allcaps":
                if (value.Length > 0)
                    return false;
                state.Case = key switch { "lowercase" => CaseLower, "uppercase" => CaseUpper, _ => CaseSmallCaps };
                return true;
            case "color":
                if (!TryParseColor(value, 1f, out var col, out bool hasAlpha))
                    return false;
                state.Color = new color(col.r, col.g, col.b, hasAlpha ? state.Color.a * col.a : state.Color.a);
                return true;
            case "alpha":
                if (!TryParseAlpha(value, out float alpha))
                    return false;
                state.Color = new color(state.Color.r, state.Color.g, state.Color.b, state.Color.a * alpha);
                return true;
            case "size":
                if (!TryParseSize(value, out float factor, out bool relative))
                    return false;
                state.Size = relative ? state.Size * factor : factor;
                return true;
            case "mark":
                if (value.Length == 0)
                {
                    state.Mark = DefaultMark;
                    return true;
                }
                if (!TryParseColor(value, MarkAlpha, out var mark, out _))
                    return false;
                state.Mark = mark;
                return true;
            case "align":
                byte align = ParseAlign(value.Trim('"', '\''));
                if (align == 0)
                    return false;
                state.Align = align;
                return true;
            case "line-height":
                float height = ParseLineHeight(value);
                if (height <= 0f)
                    return false;
                state.LineHeight = height;
                return true;
            case "font":
                string name = value.Trim('"', '\'');
                state.Font = name.Length > 0 ? name : null;
                return true;
            case "closeallblock":
                return true;
            default:
                return false;
        }
    }

    // A marker wherever a tag moved the per-line attributes or the font.
    private static void MarkBlocks(in State before, in State after, int pos, List<AlignMark> alignMarks, List<LineHeightMark> lineHeightMarks, List<FontMark> fontMarks)
    {
        if (before.Align != after.Align)
            alignMarks.Add(new AlignMark(pos, after.Align));
        if (before.LineHeight != after.LineHeight)
            lineHeightMarks.Add(new LineHeightMark(pos, after.LineHeight));
        if (!string.Equals(before.Font, after.Font, StringComparison.Ordinal))
            fontMarks.Add(new FontMark(pos, after.Font));
    }

    // <sprite=name>, <sprite name=name>, or the <glyph...> aliases -> the sprite name; null for any other tag.
    private static string? ParseSpriteName(string trimmed)
    {
        int eq = trimmed.IndexOf('=');
        if (eq < 0)
            return null;
        string key = trimmed.Substring(0, eq).Trim().ToLowerInvariant();
        if (key is not ("sprite" or "glyph" or "sprite name" or "glyph name"))
            return null;
        string val = trimmed.Substring(eq + 1).Trim().Trim('"', '\'');
        return val.Length > 0 ? val : null;
    }

    // Append one visible char with the given attributes, applying the case transform (and the smallcaps
    // size shrink for originally-lowercase letters). -xlinka
    private static void Append(char c, StringBuilder text, List<color> colors, List<byte> styles, List<float> sizes, List<color> marks,
        List<string?> sprites, in State state, byte caseMode, string? spriteName = null)
    {
        char visible = caseMode switch
        {
            CaseLower => char.ToLowerInvariant(c),
            CaseUpper or CaseSmallCaps => char.ToUpperInvariant(c),
            _ => c,
        };
        float charSize = state.Size;
        if (caseMode == CaseSmallCaps && char.IsLower(c))
            charSize *= SmallCapsScale;

        text.Append(visible);
        colors.Add(state.Color);
        styles.Add(state.Style);
        sizes.Add(charSize);
        marks.Add(state.Mark);
        sprites.Add(spriteName);
    }

    // <gradient=a,b> ... </gradient> : lerp each char's color from a (run start) to b (run end). Interpolated by
    // char index, which is spatially exact for a monospace font and close enough otherwise. -xlinka
    private static bool TryHandleGradient(string trimmed, List<color> colors, ref int gradStart, ref color gradA, ref color gradB)
    {
        if (trimmed.Equals("/gradient", StringComparison.OrdinalIgnoreCase))
        {
            if (gradStart < 0)
                return false;
            int end = colors.Count - 1;
            int span = end - gradStart;
            for (int k = gradStart; k <= end; k++)
            {
                float t = span > 0 ? (float)(k - gradStart) / span : 0f;
                colors[k] = LerpColor(gradA, gradB, t);
            }
            gradStart = -1;
            return true;
        }

        int eq = trimmed.IndexOf('=');
        if (eq < 0 || !trimmed.Substring(0, eq).Trim().Equals("gradient", StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = trimmed.Substring(eq + 1).Split(',');
        if (parts.Length < 2 || !TryParseColor(parts[0], 1f, out gradA, out _) || !TryParseColor(parts[1], 1f, out gradB, out _))
            return false;

        gradStart = colors.Count;
        return true;
    }

    private static color LerpColor(in color a, in color b, float t)
        => new(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, a.a + (b.a - a.a) * t);

    // 0 = not an alignment; otherwise (TextHorizontalAlignment + 1) so the renderer can tell "no override" apart from Left.
    private static byte ParseAlign(string value) => value.ToLowerInvariant() switch
    {
        "left" => 1,
        "center" or "centre" => 2,
        "right" => 3,
        "justify" or "justified" => 4,
        _ => 0,
    };

    // Line height is a percent (150% -> 1.5). A bare number reads as a multiplier below 3 and a percent from 3 up.
    private static float ParseLineHeight(string value)
    {
        bool percent = value.EndsWith("%");
        if (percent)
            value = value.Substring(0, value.Length - 1);
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float n) || n <= 0f)
            return 0f;
        return percent || n >= 3f ? n * 0.01f : n;
    }

    private static bool TryParseSize(string value, out float factor, out bool relative)
    {
        factor = 1f;
        relative = true;
        if (string.IsNullOrEmpty(value))
            return false;

        value = value.Trim();
        if (value.EndsWith("%"))
        {
            if (float.TryParse(value.Substring(0, value.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float p))
            {
                factor = p / 100f;
                relative = true;
                return true;
            }
            return false;
        }

        if (value.EndsWith("em"))
        {
            if (float.TryParse(value.Substring(0, value.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out float em))
            {
                factor = em;
                relative = true;
                return true;
            }
            return false;
        }

        float baseSize = t_baseSize;
        bool offset = value.StartsWith("+") || value.StartsWith("-");
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float a))
        {
            if (baseSize > 0f)
            {
                // Font units: an offset adds to the base, a bare number replaces it.
                float target = offset ? baseSize + a : a;
                factor = MathF.Max(target, 0.01f) / baseSize;
            }
            else
            {
                factor = offset ? 1f + a : a;
            }
            relative = false;
            return true;
        }
        return false;
    }

    // #rgb, #rgba, #rrggbb, #rrggbbaa, or a name. hasAlpha says the value carried an alpha of its own;
    // otherwise the alpha is defaultAlpha. Names never carry one.
    private static bool TryParseColor(string value, float defaultAlpha, out color result, out bool hasAlpha)
    {
        result = color.White;
        hasAlpha = false;
        if (string.IsNullOrEmpty(value))
            return false;

        value = value.Trim().Trim('"', '\'');
        if (value.StartsWith("#"))
            return TryParseHex(value.AsSpan(1), defaultAlpha, out result, out hasAlpha);

        // The first word of the value is the name.
        int space = value.IndexOf(' ');
        if (space > 0)
            value = value.Substring(0, space);
        return TryNamedColor(value.ToLowerInvariant(), defaultAlpha, out result);
    }

    private static bool TryNamedColor(string name, float alpha, out color result)
    {
        switch (name)
        {
            case "clear": result = new color(0f, 0f, 0f, alpha); return true;
            case "white": result = new color(1f, 1f, 1f, alpha); return true;
            case "black": result = new color(0f, 0f, 0f, alpha); return true;
            case "red": result = new color(1f, 0f, 0f, alpha); return true;
            case "green": result = new color(0f, 1f, 0f, alpha); return true;
            case "blue": result = new color(0f, 0f, 1f, alpha); return true;
            case "yellow": result = new color(1f, 1f, 0f, alpha); return true;
            case "cyan": result = new color(0f, 1f, 1f, alpha); return true;
            case "magenta": result = new color(1f, 0f, 1f, alpha); return true;
            case "gray":
            case "grey": result = new color(0.5f, 0.5f, 0.5f, alpha); return true;
            case "orange": result = new color(1f, 0.5f, 0f, alpha); return true;
            case "purple": result = new color(0.5f, 0f, 1f, alpha); return true;
            case "lime": result = new color(0.75f, 1f, 0f, alpha); return true;
            case "pink": result = new color(1f, 0f, 0.5f, alpha); return true;
            case "brown": result = new color(0.25f, 0f, 0f, alpha); return true;
        }
        // A palette-style "family.hue" name falls back to its hue.
        int dot = name.LastIndexOf('.');
        if (dot > 0 && dot < name.Length - 1)
            return TryNamedColor(name.Substring(dot + 1), alpha, out result);
        result = color.White;
        return false;
    }

    private static bool TryParseHex(ReadOnlySpan<char> hex, float defaultAlpha, out color result, out bool hasAlpha)
    {
        result = color.White;
        hasAlpha = hex.Length is 4 or 8;
        int digits = hex.Length is 3 or 4 ? 1 : hex.Length is 6 or 8 ? 2 : 0;
        if (digits == 0)
            return false;
        Span<float> channel = stackalloc float[4];
        channel[3] = defaultAlpha;
        int count = hex.Length / digits;
        for (int k = 0; k < count; k++)
        {
            if (!int.TryParse(hex.Slice(k * digits, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
                return false;
            channel[k] = digits == 1 ? v * 17 / 255f : v / 255f;
        }
        result = new color(channel[0], channel[1], channel[2], channel[3]);
        return true;
    }

    // #aa (two hex digits) or a number: up to 1 a share, above it out of 255.
    private static bool TryParseAlpha(string value, out float alpha)
    {
        alpha = 1f;
        if (string.IsNullOrEmpty(value))
            return false;

        value = value.Trim().Trim('"', '\'');
        if (value.StartsWith("#"))
        {
            if (value.Length != 3 || !int.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hv))
                return false;
            alpha = hv / 255f;
            return true;
        }

        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) || f < 0f)
            return false;
        alpha = f <= 1f ? f : f / 255f;
        return true;
    }
}
