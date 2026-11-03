// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// How far this machine's scroll wheel turned this frame, in notches, up positive. both adds sideways
// scrolling as x, right positive, with the same up and down as y; x stays 0 where the platform reports no
// sideways scrolling.
[Node("Input/Mouse/Mouse Scroll", Capability.Input, Description = "Reads this frame's vertical and horizontal mouse-wheel movement in notches.",
    Keywords = "wheel, scroll, scroll wheel")]
public sealed class MouseWheel : ReadNode
{
    [return: Port("scroll")]
    public static float Run(ScriptContext ctx, out float2 both)
    {
        var mouse = ScriptInput.Of(ctx)?.Mouse;
        float vertical = mouse?.ScrollWheelDelta.Value ?? 0f;
        both = new float2(mouse?.HorizontalScrollDelta.Value ?? 0f, vertical);
        return vertical;
    }
}
