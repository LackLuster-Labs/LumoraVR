// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Input;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The grab action of a hand, as its bindings have it: the grip, or the right mouse button. On the right hand unless right is off; on a desktop the right hand is the mouse and keyboard.
// Held, and whether it went down or up this frame.
[Node("Input/Actions/Grab", Capability.Input, Description = "The grab action of a hand, as its bindings have it: the grip, or the right mouse button.",
    Keywords = "action, binding, grab input, grip")]
public sealed class GrabButton : ReadNode
{
    [return: Port("held")]
    public static bool Run(ScriptContext ctx, out bool pressed, out bool released, bool right = true)
    {
        pressed = false;
        released = false;
        var action = ScriptInput.Of(ctx)?.Actions?.Interaction(right ? Chirality.Right : Chirality.Left).Grab;
        if (action == null)
            return false;
        pressed = action.Pressed;
        released = action.Released;
        return action.Held;
    }
}
