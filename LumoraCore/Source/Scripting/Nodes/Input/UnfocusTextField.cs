// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Takes the keys back from the text field on the slot when this machine is typing into it, keeping what was
// typed, the way clicking away does, and says whether it had them. One that did not is no failure, nor is a
// call someone else set off, which their own machine's copy answers; no field is.
[Node("UI/Text Input/Unfocus Field", Capability.Input, Description = "Takes the keys back from the text field on the slot when this machine is typing into it, keeping what was typed, the way clicking away does, and says whether it had them.")]
public sealed class UnfocusTextField : ActionNode
{
    [return: Port("unfocused")]
    public static bool Run(ScriptContext ctx, Slot? slot)
    {
        if (ScriptTextFocus.FieldOn(ctx, slot) is not { } field)
        {
            ScriptTextFocus.Missed(ctx, slot);
            return false;
        }
        if (!field.IsFocused || !ctx.CausedHere)
            return false;
        field.Unfocus();
        return true;
    }
}
