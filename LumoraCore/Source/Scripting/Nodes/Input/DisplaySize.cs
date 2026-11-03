// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// This machine's window and the screen it is on, in pixels, and the view's width over its height. known is
// false, and both sizes 0, until the platform has said; the aspect is the view's own and reads 16:9 before
// the first frame is drawn.
[Node("Input/Display/Display", Capability.Input, Description = "Reads the window and screen size in pixels, plus the view's aspect ratio.")]
public sealed class DisplaySize : ReadNode
{
    [return: Port("window")]
    public static int2 Run(ScriptContext ctx, out int2 screen, out float aspect, out bool known)
    {
        screen = default;
        aspect = 0f;
        known = false;
        var input = ScriptInput.Of(ctx);
        if (input == null)
            return default;
        aspect = input.DesktopViewportAspect;
        var display = input.Display;
        if (!display.Known)
            return default;
        known = true;
        screen = display.Screen;
        return display.Window;
    }
}
