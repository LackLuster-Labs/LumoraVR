// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// How far this machine's mouse moved this frame. pixels is the pointer's move across the desktop. look is the
// move mouse look turns by, in screen heights at this person's mouse sensitivity and smoothing, 0 while the
// dash is open; it is the one that carries the motion while the view holds the pointer for mouse look. y
// grows downward in both.
[Node("Input/Mouse/Mouse Movement", Capability.Input, Description = "Reads mouse movement this frame as screen pixels and the adjusted movement used for mouse look.",
    Keywords = "cursor, pointer, mouse delta")]
public sealed class MouseMotion : ReadNode
{
    [return: Port("pixels")]
    public static float2 Run(ScriptContext ctx, out float2 look)
    {
        var mouse = ScriptInput.Of(ctx)?.Mouse;
        look = mouse?.DirectDelta.Value ?? default;
        return mouse?.DesktopPosition.Delta ?? default;
    }
}
