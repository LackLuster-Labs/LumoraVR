// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Where this machine's mouse pointer is. desktop is in screen pixels across the whole desktop; window is in
// pixels from the window's top left corner, and fraction runs 0 to 1 across the window, top left at 0,0.
// Both are -1 on each axis while the platform has not said where the window is, and inWindow says whether
// the pointer is over the window.
[Node("Input/Mouse/Mouse Position", Capability.Input, Description = "Where this machine's mouse pointer is. desktop is in screen pixels across the whole desktop; window is in pixels from the window's top left corner, and fraction runs 0 to 1 across the window, top left at 0,0.",
    Keywords = "cursor, pointer, cursor position")]
public sealed class MousePointer : ReadNode
{
    [return: Port("desktop")]
    public static float2 Run(ScriptContext ctx, out float2 window, out float2 fraction, out bool inWindow)
    {
        window = new float2(-1f, -1f);
        fraction = new float2(-1f, -1f);
        inWindow = false;
        var input = ScriptInput.Of(ctx);
        var mouse = input?.Mouse;
        if (mouse == null)
            return default;
        var desktop = mouse.DesktopPosition.Value;
        var display = input!.Display;
        if (display.Known)
        {
            window = new float2(desktop.x - display.WindowPosition.x, desktop.y - display.WindowPosition.y);
            fraction = new float2(window.x / display.Window.x, window.y / display.Window.y);
            inWindow = fraction.x >= 0f && fraction.x < 1f && fraction.y >= 0f && fraction.y < 1f;
        }
        return desktop;
    }
}
