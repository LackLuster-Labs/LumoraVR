// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Takes the keys back from whichever field in this world this machine is typing into, keeping what was
// typed, and says whether there was one. A field in another world, the dash's included, is left alone, and so
// is this machine's for a call someone else set off, which their own machine's copy answers.
[Node("UI/Text Input/Clear Focus", Capability.Input, Description = "Takes the keys back from whichever field in this world this machine is typing into, keeping what was typed, and says whether there was one.")]
public sealed class ClearTextFocus : ActionNode
{
    [return: Port("cleared")]
    public static bool Run(ScriptContext ctx)
    {
        if (!ctx.CausedHere || ScriptTextFocus.Focused(ctx) is not { } field)
            return false;
        field.Unfocus();
        return true;
    }
}
