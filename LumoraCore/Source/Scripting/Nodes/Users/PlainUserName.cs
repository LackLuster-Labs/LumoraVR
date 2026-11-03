// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Text;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// A user's name as text shows it, as plain text: the markup tags read and taken out the way a text component
// reads them, sprites dropped, spaces at the ends trimmed. Users/Name gives the name as written.
[Node("Users/Info/Plain Name", Capability.UsersRead, Description = "A user's name as text shows it, as plain text: the markup tags read and taken out the way a text component reads them, sprites dropped, spaces at the ends trimmed.",
    Keywords = "sanitized name, name without formatting, strip tags, clean name")]
public sealed class PlainUserName : ReadNode
{
    [ThreadStatic]
    private static Scratch? _scratch;

    private sealed class Scratch
    {
        public readonly StringBuilder Text = new();
        public readonly List<color> Colors = new();
        public readonly List<byte> Styles = new();
        public readonly List<float> Sizes = new();
        public readonly List<color> Marks = new();
        public readonly List<string?> Sprites = new();
        public readonly List<RichTextParser.AlignMark> Aligns = new();
        public readonly List<RichTextParser.LineHeightMark> LineHeights = new();
        public readonly List<RichTextParser.FontMark> Fonts = new();
    }

    [return: Port("name")]
    public static string Run(ScriptContext ctx, User? user)
    {
        if (!ScriptReads.Visible(ctx, user))
            return string.Empty;
        var name = user!.UserName.Value;
        if (string.IsNullOrEmpty(name))
            return string.Empty;
        var s = _scratch ??= new Scratch();
        RichTextParser.Parse(name, color.White, s.Text, s.Colors, s.Styles, s.Sizes, s.Marks, s.Sprites, s.Aligns, s.LineHeights, s.Fonts);
        for (int i = s.Text.Length - 1; i >= 0; i--)
        {
            if (s.Text[i] == (char)TextShaper.SpriteGlyph)
                s.Text.Remove(i, 1);
        }
        return s.Text.ToString().Trim();
    }
}
