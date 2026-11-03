// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// A mouse button on this machine: 0 left, 1 right, 2 middle. Held, and whether it went down or up this
// frame.
[Node("Input/Mouse/Mouse Button", Capability.Input, Description = "A mouse button on this machine: 0 left, 1 right, 2 middle.",
    Keywords = "click, left click, right click, middle click")]
public sealed class MouseButtonState : ReadNode
{
    [return: Port("held")]
    public static bool Run(ScriptContext ctx, out bool pressed, out bool released, int button = 0)
    {
        pressed = false;
        released = false;
        var mouse = ScriptInput.Of(ctx)?.Mouse;
        var digital = button switch
        {
            0 => mouse?.LeftButton,
            1 => mouse?.RightButton,
            2 => mouse?.MiddleButton,
            _ => null,
        };
        if (digital == null)
            return false;
        pressed = digital.Pressed;
        released = digital.Released;
        return digital.Held;
    }
}
