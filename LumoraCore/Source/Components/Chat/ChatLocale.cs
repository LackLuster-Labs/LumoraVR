// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Localization;

namespace Lumora.Core.Components.Chat;

// Every string the world's chat puts on screen.
public static class ChatLocale
{
    public static readonly LocaleText Hint = "Chat.Hint".AsLocale("Say something. Enter sends, Esc closes");

    // A pad has no Enter or Esc: its click brings Steam's keyboard up, whose Enter sends, and its back closes.
    public static LocaleText HintPad(string keyboard, string close)
        => "Chat.HintPad".AsLocale("Say something. {0} for the keyboard, {1} closes", keyboard, close);
}
