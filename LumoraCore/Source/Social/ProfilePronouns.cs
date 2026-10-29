// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Social;

// Pronouns as a profile shows them: two or three words of letters split by '/', like she/her or xe/xem/xyr.
// The editor offers the common sets and takes a typed one through the same rule the accounts service holds,
// so Save never sends something it would refuse. Spaces around a slash go, the text is NFC so an accent typed
// two ways is one letter, and nothing but letters (at most two accents on one) gets through: no markup,
// links, digits or invisible characters riding along beside a name. -xlinka
public static class ProfilePronouns
{
    public static readonly string[] Common =
    {
        "she/her", "he/him", "they/them", "she/they", "he/they", "they/she", "they/he", "any/all", "xe/xem", "ze/zir", "it/its",
    };

    // The stored form, "" for none, or null when it breaks the rule.
    public static string? Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
            return string.Empty;
        if (text.Length > SocialLimits.PronounsMax * 4)
            return null;

        text = text.Normalize(NormalizationForm.FormC);
        var words = text.Split('/');
        if (words.Length is < 2 or > 3)
            return null;

        var built = new StringBuilder(text.Length);
        foreach (var raw in words)
        {
            var word = raw.Trim();
            if (word.Length == 0)
                return null;

            int letters = 0;
            int marks = 0;
            foreach (var rune in word.EnumerateRunes())
            {
                if (Rune.IsLetter(rune))
                {
                    letters++;
                    marks = 0;
                    continue;
                }
                var category = Rune.GetUnicodeCategory(rune);
                bool mark = category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
                if (!mark || letters == 0 || ++marks > 2)
                    return null;
            }
            if (letters > SocialLimits.PronounWordMax)
                return null;

            if (built.Length > 0)
                built.Append('/');
            built.Append(word);
        }

        var result = built.ToString();
        return result.Length <= SocialLimits.PronounsMax ? result : null;
    }

    public static bool IsCommon(string? value)
        => value != null && System.Array.IndexOf(Common, value) >= 0;
}
