// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The slot of the text field this machine is typing into, when it is one in this world the script can see,
// and whether there is one. Nothing while this world is not the one in front of the person.
[Node("UI/Text Input/Focused Field", Capability.Input, Description = "The slot of the text field this machine is typing into, when it is one in this world the script can see, and whether there is one.")]
public sealed class FocusedTextField : ReadNode
{
    [return: Port("field")]
    public static Slot? Run(ScriptContext ctx, out bool typing)
    {
        var slot = ScriptTextFocus.Focused(ctx)?.Slot;
        typing = slot != null;
        return slot;
    }
}
