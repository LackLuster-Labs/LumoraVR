// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The characters typed on this machine's keyboard this frame, in the order they came. Empty in a frame
// nothing was typed, and while a text field has the keys.
[Node("Input/Keyboard/Typed Text", Capability.Input, Description = "The characters typed on this machine's keyboard this frame, in the order they came.",
    Keywords = "keyboard, typing, text input")]
public sealed class TypedText : ReadNode
{
    [return: Port("text")]
    public static string Run(ScriptContext ctx)
        => ScriptInput.Of(ctx) is { } input && ScriptInput.KeysOpen(input) ? input.Keyboard.GetTypedText() : string.Empty;
}
