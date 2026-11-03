// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Text;

namespace Lumora.Script;

// Text of length characters, each drawn from characters with every one as likely. A character made of
// two UTF-16 units, an emoji say, is drawn whole and never split into halves that are not text. No
// characters given draws from a to z, A to Z and 0 to 9. A length of 0 or less gives empty text.
[Node("Math/Random/Random Text", Description = "Text of length characters, each drawn from characters with every one as likely.",
    Keywords = "password, random characters, string")]
public sealed class RandomText : ReadNode
{
    private const string LettersAndDigits = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    // The most UTF-16 units one text can hold.
    private const int MostUnits = 0x3FFFFFDF;

    [return: Port("text")]
    public static string Run(ScriptContextBase ctx, int length = 8, string? characters = null)
    {
        if (length <= 0)
            return string.Empty;

        var set = new List<Rune>();
        bool wide = false;
        foreach (var rune in (string.IsNullOrEmpty(characters) ? LettersAndDigits : characters).EnumerateRunes())
        {
            set.Add(rune);
            wide |= rune.Utf16SequenceLength == 2;
        }
        if ((long)length * (wide ? 2 : 1) > MostUnits)
            throw new InvalidOperationException($"{length:N0} characters is longer than a text can be");

        var random = ctx.Random;
        if (!wide)
        {
            return string.Create(length, (set, random), static (span, state) =>
            {
                for (int i = 0; i < span.Length; i++)
                    span[i] = (char)state.set[state.random.Next(state.set.Count)].Value;
            });
        }

        var text = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            var rune = set[random.Next(set.Count)];
            if (rune.Utf16SequenceLength == 1)
                text.Append((char)rune.Value);
            else
                text.Append(rune.ToString());
        }
        return text.ToString();
    }
}
