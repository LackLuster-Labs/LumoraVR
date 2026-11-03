// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Scripting;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Flashes a highlight over root and everything under it for seconds, fading out: each mesh drawn over in
// colour, each collider and each skinned mesh outlined by its shape's bounds (a sphere collider by its
// rings), all seen through whatever stands in front, dimmer there. With follow it stays on things as they
// move; without, it stays where they stood. Inactive slots and disabled parts only when hidden is on. Drawn
// on the machine the script runs on and nowhere else: the host for a Shared script, each machine for a Local
// one; nothing in the world changes. shown says how many things flashed, and a script flashing more than
// WarnParts at once is warned. An unset colour flashes white.
[Node("Rendering/Flash Highlight", Capability.WorldRead, Description = "Briefly highlights an object and its children, then fades the highlight away.",
    Keywords = "outline, glow, indicate")]
public sealed class FlashHighlight : ActionNode
{
    public const int WarnParts = 512;

    [return: Port("shown")]
    public static int Run(ScriptContext ctx, Slot? root, [Default(1)] color color, float seconds = 0.5f, bool meshes = true, bool colliders = true,
        bool hidden = false, bool follow = true)
    {
        if (!ScriptReads.Reached(ctx, root, "root"))
            return 0;
        int shown = ScriptDrawingLayer.For(ctx.World).AddFlash(root!, ScriptDrawings.Shade(color), ScriptDrawings.Seconds(seconds),
            meshes, colliders, hidden, follow);
        if (shown > WarnParts)
            ctx.Warn(ScriptWarning.Drawings, $"one flash of {shown:N0} things");
        return shown;
    }
}
