// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Helio.UI;
using Lumora.Core.Math;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Social;

// A bio's markdown, turned into the rich text Helio's Text draws. The subset: paragraphs (a blank line
// between), **bold** / __bold__, *italic* / _italic_, ~~strike~~, `code`, lists (- * + and 1. or 1)), and
// links, [words](https://...) or a bare https:// address. A link shows its address beside its words and opens
// nothing on its own: the profile puts a pill under the bio per address, and the pill asks first.
//
// Everything the person typed is text. The only character that starts markup in the rich text is '<', and
// every one they typed goes out as <noparse><</noparse>, so no tag they wrote ever reaches the parser: not a
// colour, a size, a sprite, a font, nothing. Characters that change how the text around them reads without
// showing themselves (direction overrides, zero widths, the sprite slot) are dropped first, so an address
// cannot be turned round or hidden in. Images, headings, quotes and raw HTML stay as typed. -xlinka
public static class BioMarkdown
{
    // The service holds a bio to BioMax; anything past twice that is not a bio, and is not read.
    public const int SourceMax = SocialLimits.BioMax * 2;
    public const int LinesMax = 60;
    public const int LinksMax = 4;
    public const int SpansMax = 200;
    public const int AddressMax = 512;

    private const char Nbsp = (char)0x00A0;
    private const char Bullet = (char)0x2022;
    private const char SubBullet = (char)0x25E6;
    private const char Ellipsis = (char)0x2026;
    private const string LessThan = "<noparse><</noparse>";

    public static BioRender Render(string? source, in BioInks inks)
    {
        var state = new State(inks);
        var lines = Clean(source).Split('\n');
        var rich = new StringBuilder();
        int written = 0;
        bool gap = false;
        bool cut = false;
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                gap = written > 0;
                continue;
            }
            if (written >= LinesMax)
            {
                cut = true;
                break;
            }
            if (written > 0)
                rich.Append('\n');
            if (gap)
                rich.Append('\n');
            gap = false;
            Block(line, rich, state);
            written++;
        }
        if (cut)
            rich.Append('\n').Append(Ellipsis);

        string text = rich.ToString();
        return new BioRender { Rich = text, Plain = RichTextParser.Plain(text), Links = state.Links };
    }

    // The text with what cannot be trusted on screen taken out. Line breaks of every kind become '\n', a tab
    // four spaces.
    public static string Clean(string? source)
    {
        if (string.IsNullOrEmpty(source))
            return string.Empty;
        int length = System.Math.Min(source.Length, SourceMax);
        var built = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            char c = source[i];
            if (c == '\r')
            {
                if (i + 1 < length && source[i + 1] == '\n')
                    continue;
                built.Append('\n');
                continue;
            }
            if (c == '\n' || c == (char)0x2028 || c == (char)0x2029)
            {
                built.Append('\n');
                continue;
            }
            if (c == '\t')
            {
                built.Append("    ");
                continue;
            }
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < length && char.IsLowSurrogate(source[i + 1]))
                {
                    built.Append(c).Append(source[i + 1]);
                    i++;
                }
                continue;
            }
            if (char.IsLowSurrogate(c) || char.IsControl(c) || Hidden(c))
                continue;
            built.Append(c);
        }
        return built.ToString();
    }

    // Shown as nothing, but each changes the text beside it: marks and overrides of direction, zero widths
    // (the joiner stays, emoji are built with it), the soft hyphen, the byte order mark, and the object mark
    // the text shaper keeps for sprites.
    private static bool Hidden(char c)
        => c is (char)0x00AD or (char)0x200B or (char)0x200C or (char)0x200E or (char)0x200F or (char)0xFEFF or (char)0xFFFC
            || (c >= (char)0x202A && c <= (char)0x202E)
            || (c >= (char)0x2060 && c <= (char)0x2064)
            || (c >= (char)0x2066 && c <= (char)0x206F);

    // BLOCKS

    private static void Block(string line, StringBuilder rich, State state)
    {
        int indent = 0;
        while (indent < line.Length && line[indent] == ' ')
            indent++;
        string rest = line.Substring(indent).TrimEnd();
        int level = indent >= 2 ? 1 : 0;
        if (TryItem(rest, level, out string marker, out string body))
        {
            rich.Append(Nbsp, level == 0 ? 2 : 6);
            rich.Append(marker);
            rich.Append(Nbsp, 2);
            Inline(body, rich, state);
            return;
        }
        Inline(rest, rich, state);
    }

    private static bool TryItem(string text, int level, out string marker, out string body)
    {
        marker = body = string.Empty;
        if (text.Length >= 2 && text[0] is '-' or '*' or '+' && text[1] == ' ')
        {
            marker = (level == 0 ? Bullet : SubBullet).ToString();
            body = text.Substring(2).Trim();
            return true;
        }
        int digits = 0;
        while (digits < text.Length && digits < 4 && text[digits] >= '0' && text[digits] <= '9')
            digits++;
        if (digits is >= 1 and <= 3 && digits + 1 < text.Length && text[digits] is '.' or ')' && text[digits + 1] == ' ')
        {
            marker = text.Substring(0, digits) + ".";
            body = text.Substring(digits + 2).Trim();
            return true;
        }
        return false;
    }

    // INLINE

    private enum Kind { Literal, Markup, Delimiter }

    private sealed class Node
    {
        public Kind Kind;
        public string Text = string.Empty;
        public char Char;
        public int Count;
        public bool CanOpen;
        public bool CanClose;
        public List<string>? Opens;
        public List<string>? Closes;
    }

    private static void Inline(string text, StringBuilder rich, State state)
    {
        var nodes = Tokenize(text, state);
        Emphasis(nodes, state);
        foreach (var node in nodes)
        {
            switch (node.Kind)
            {
                case Kind.Literal:
                    Escape(node.Text, rich);
                    break;
                case Kind.Markup:
                    rich.Append(node.Text);
                    break;
                default:
                    if (node.Closes != null)
                        foreach (var tag in node.Closes)
                            rich.Append(tag);
                    if (node.Count > 0)
                        rich.Append(node.Char, node.Count);
                    if (node.Opens != null)
                        foreach (var tag in node.Opens)
                            rich.Append(tag);
                    break;
            }
        }
    }

    private static List<Node> Tokenize(string text, State state)
    {
        var nodes = new List<Node>();
        var literal = new StringBuilder();
        void Flush()
        {
            if (literal.Length == 0)
                return;
            nodes.Add(new Node { Kind = Kind.Literal, Text = literal.ToString() });
            literal.Clear();
        }
        void Markup(string markup)
        {
            Flush();
            nodes.Add(new Node { Kind = Kind.Markup, Text = markup });
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && IsAsciiPunctuation(text[i + 1]))
            {
                literal.Append(text[i + 1]);
                i += 2;
                continue;
            }
            if (c == '`')
            {
                int run = Run(text, i, '`');
                int close = FindRun(text, i + run, '`', run);
                if (close < 0)
                {
                    literal.Append('`', run);
                    i += run;
                    continue;
                }
                string code = text.Substring(i + run, close - i - run);
                if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0)
                    code = code.Substring(1, code.Length - 2);
                Markup(state.Code(code));
                i = close + run;
                continue;
            }
            if (c == '[' && TryLink(text, i, out int linkEnd, out string label, out Uri? linked))
            {
                Markup(state.Link(label, linked!));
                i = linkEnd;
                continue;
            }
            if (c is 'h' or 'H' && (i == 0 || !char.IsLetterOrDigit(text[i - 1])) && TryBare(text, i, out int bareEnd, out Uri? bare))
            {
                Markup(state.Link(string.Empty, bare!));
                i = bareEnd;
                continue;
            }
            if (c is '*' or '_' or '~')
            {
                int run = Run(text, i, c);
                if (c == '~' && run != 2)
                {
                    literal.Append(c, run);
                    i += run;
                    continue;
                }
                char before = i > 0 ? text[i - 1] : ' ';
                char after = i + run < text.Length ? text[i + run] : ' ';
                bool left = !char.IsWhiteSpace(after) && (!IsPunctuation(after) || char.IsWhiteSpace(before) || IsPunctuation(before));
                bool right = !char.IsWhiteSpace(before) && (!IsPunctuation(before) || char.IsWhiteSpace(after) || IsPunctuation(after));
                Flush();
                nodes.Add(new Node
                {
                    Kind = Kind.Delimiter,
                    Char = c,
                    Count = run,
                    // an underscore inside a word is part of the word: snake_case stays as typed
                    CanOpen = c == '_' ? left && (!right || IsPunctuation(before)) : left,
                    CanClose = c == '_' ? right && (!left || IsPunctuation(after)) : right,
                });
                i += run;
                continue;
            }
            literal.Append(c);
            i++;
        }
        Flush();
        return nodes;
    }

    // Left to right, each closing run takes the nearest opening run of its own character. What lies between
    // the two can no longer pair with anything outside them, so it stays as typed, and every span closes
    // inside the one around it: the tags always nest. A run of two on both sides is bold, otherwise italic;
    // tildes only ever pair in twos.
    private static void Emphasis(List<Node> nodes, State state)
    {
        var openers = new List<Node>();
        foreach (var node in nodes)
        {
            if (node.Kind != Kind.Delimiter)
                continue;
            while (node.CanClose && node.Count > 0 && state.Spans < SpansMax)
            {
                int found = -1;
                for (int k = openers.Count - 1; k >= 0; k--)
                {
                    if (openers[k].Char == node.Char && openers[k].Count > 0)
                    {
                        found = k;
                        break;
                    }
                }
                if (found < 0)
                    break;
                var opener = openers[found];
                if (node.Char == '~' && (opener.Count < 2 || node.Count < 2))
                    break;
                int use = node.Char == '~' || opener.Count >= 2 && node.Count >= 2 ? 2 : 1;
                string tag = node.Char == '~' ? "s" : use == 2 ? "b" : "i";
                (opener.Opens ??= new List<string>()).Insert(0, "<" + tag + ">");
                (node.Closes ??= new List<string>()).Add("</" + tag + ">");
                opener.Count -= use;
                node.Count -= use;
                state.Spans++;
                openers.RemoveRange(found + 1, openers.Count - found - 1);
                if (opener.Count == 0)
                    openers.RemoveAt(found);
            }
            if (node.CanOpen && node.Count > 0)
                openers.Add(node);
        }
    }

    // [words](address): no brackets inside the words, the address one piece with no spaces, parentheses
    // in it balanced.
    private static bool TryLink(string text, int start, out int end, out string label, out Uri? uri)
    {
        end = start;
        label = string.Empty;
        uri = null;
        int close = -1;
        for (int k = start + 1; k < text.Length && k - start <= 300; k++)
        {
            char c = text[k];
            if (c == '\\')
            {
                k++;
                continue;
            }
            if (c == '[')
                return false;
            if (c == ']')
            {
                close = k;
                break;
            }
        }
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(')
            return false;

        int depth = 0;
        int paren = -1;
        for (int k = close + 2; k < text.Length && k - close <= AddressMax + 4; k++)
        {
            char c = text[k];
            if (char.IsWhiteSpace(c))
                return false;
            if (c == '(')
                depth++;
            else if (c == ')')
            {
                if (depth == 0)
                {
                    paren = k;
                    break;
                }
                depth--;
            }
        }
        if (paren < 0 || !TryAddress(text.Substring(close + 2, paren - close - 2), out uri))
            return false;
        label = Unescape(text.Substring(start + 1, close - start - 1)).Trim();
        end = paren + 1;
        return true;
    }

    // An address written out in the text. The punctuation that ends a sentence, and a closing bracket the
    // address never opened, belong to the sentence.
    private static bool TryBare(string text, int start, out int end, out Uri? uri)
    {
        end = start;
        uri = null;
        if (string.Compare(text, start, "https://", 0, 8, StringComparison.OrdinalIgnoreCase) != 0
            && string.Compare(text, start, "http://", 0, 7, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        int k = start;
        while (k < text.Length && !char.IsWhiteSpace(text[k]) && text[k] is not ('<' or '>' or '"' or '`'))
            k++;
        // too long to be an address: as text, rather than a link to the first part of it
        if (k - start > AddressMax + 8)
            return false;
        while (k > start)
        {
            char last = text[k - 1];
            if (".,;:!?'*_~".IndexOf(last) >= 0)
            {
                k--;
                continue;
            }
            if (last == ')' && Count(text, start, k, '(') < Count(text, start, k, ')'))
            {
                k--;
                continue;
            }
            break;
        }
        if (!TryAddress(text.Substring(start, k - start), out uri))
            return false;
        end = k;
        return true;
    }

    // Only http and https, with a host and no name or password in front of it (https://trusted@elsewhere
    // goes to elsewhere).
    public static bool TryAddress(string address, out Uri? uri)
    {
        uri = null;
        if (address.Length == 0 || address.Length > AddressMax)
            return false;
        if (!address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (char c in address)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>' or '"' or '\\')
                return false;
        }
        if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed))
            return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return false;
        if (string.IsNullOrEmpty(parsed.Host) || !string.IsNullOrEmpty(parsed.UserInfo))
            return false;
        uri = parsed;
        return true;
    }

    // What the address says, the way it goes: the host in its ASCII form, so a name built from lookalike
    // letters shows as the xn-- name it really is.
    public static string Display(Uri uri)
    {
        var shown = new StringBuilder();
        shown.Append(uri.Scheme).Append("://").Append(uri.IdnHost);
        if (!uri.IsDefaultPort)
            shown.Append(':').Append(uri.Port.ToString(CultureInfo.InvariantCulture));
        string path = uri.PathAndQuery;
        if (path != "/" || uri.Fragment.Length > 0)
            shown.Append(path);
        shown.Append(uri.Fragment);
        return shown.ToString();
    }

    private static void Escape(string text, StringBuilder rich)
    {
        foreach (char c in text)
        {
            if (c == '<')
                rich.Append(LessThan);
            else
                rich.Append(c);
        }
    }

    private static string Escaped(string text)
    {
        var built = new StringBuilder(text.Length + 8);
        Escape(text, built);
        return built.ToString();
    }

    private static string Unescape(string text)
    {
        if (text.IndexOf('\\') < 0)
            return text;
        var built = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && IsAsciiPunctuation(text[i + 1]))
                i++;
            built.Append(text[i]);
        }
        return built.ToString();
    }

    private static int Run(string text, int start, char c)
    {
        int k = start;
        while (k < text.Length && text[k] == c)
            k++;
        return k - start;
    }

    // The start of the next run of exactly length characters c at or after from, or -1.
    private static int FindRun(string text, int from, char c, int length)
    {
        int k = from;
        while (k < text.Length)
        {
            if (text[k] != c)
            {
                k++;
                continue;
            }
            int run = Run(text, k, c);
            if (run == length)
                return k;
            k += run;
        }
        return -1;
    }

    private static int Count(string text, int start, int end, char c)
    {
        int count = 0;
        for (int k = start; k < end; k++)
        {
            if (text[k] == c)
                count++;
        }
        return count;
    }

    private static bool IsPunctuation(char c) => char.IsPunctuation(c) || char.IsSymbol(c);

    private static bool IsAsciiPunctuation(char c) => c < 128 && (char.IsPunctuation(c) || char.IsSymbol(c));

    private static string Hex(in color c)
    {
        static int Byte(float v) => (int)MathF.Round(System.Math.Clamp(v, 0f, 1f) * 255f);
        return "#" + Byte(c.r).ToString("X2", CultureInfo.InvariantCulture) + Byte(c.g).ToString("X2", CultureInfo.InvariantCulture)
            + Byte(c.b).ToString("X2", CultureInfo.InvariantCulture) + Byte(c.a).ToString("X2", CultureInfo.InvariantCulture);
    }

    private sealed class State
    {
        private readonly string _link;
        private readonly string _code;
        private readonly string _mark;
        public readonly List<Uri> Links = new();
        public int Spans;

        public State(in BioInks inks)
        {
            _link = Hex(inks.Link);
            _code = Hex(inks.Code);
            _mark = Hex(inks.CodeMark);
        }

        public string Code(string code) => "<mark=" + _mark + "><color=" + _code + ">" + Escaped(code) + "</color></mark>";

        // The words underlined in the link ink, then the address they go to, dimmer, unless the words are it.
        public string Link(string label, Uri uri)
        {
            string shown = Display(uri);
            Remember(uri);
            string words = label.Length == 0 ? shown : label;
            string line = "<u><color=" + _link + ">" + Escaped(words) + "</color></u>";
            if (label.Length == 0 || string.Equals(label, shown, StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, uri.OriginalString, StringComparison.OrdinalIgnoreCase))
                return line;
            return line + " <alpha=#A0>(" + Escaped(shown) + ")</alpha>";
        }

        private void Remember(Uri uri)
        {
            if (Links.Count >= LinksMax)
                return;
            foreach (var known in Links)
            {
                if (string.Equals(known.AbsoluteUri, uri.AbsoluteUri, StringComparison.Ordinal))
                    return;
            }
            Links.Add(uri);
        }
    }
}
