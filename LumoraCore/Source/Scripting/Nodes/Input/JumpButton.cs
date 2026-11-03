// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The jump action, as the bindings have it. Held, and whether it went down or up this frame.
[Node("Input/Actions/Jump", Capability.Input, Description = "The jump action, as the bindings have it.",
    Keywords = "action, binding, jump input")]
public sealed class JumpButton : ReadNode
{
    [return: Port("held")]
    public static bool Run(ScriptContext ctx, out bool pressed, out bool released)
    {
        pressed = false;
        released = false;
        var action = ScriptInput.Of(ctx)?.Actions?.Locomotion.Jump;
        if (action == null)
            return false;
        pressed = action.Pressed;
        released = action.Released;
        return action.Held;
    }
}
