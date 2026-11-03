// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// A point on this machine's desktop view as a ray into the world: (0, 0) is the middle of the view,
// (-1, -1) its bottom left and (1, 1) its top right. Origin is where the view looks from, direction the unit
// direction out through the point, and position the spot distance metres along it. Valid is false and the
// rest zero in a headset, while this world is not the one in front of the person, and before the view is
// known.
[Node("Input/Display/Screen Point To World", Capability.Input, Description = "A point on this machine's desktop view as a ray into the world: (0, 0) is the middle of the view, (-1, -1) its bottom left and (1, 1) its top right.",
    Keywords = "mouse ray, unproject, raycast from screen, cursor ray")]
public sealed class ScreenPointToWorld : ReadNode
{
    public static void Run(ScriptContext ctx, float2 point, out float3 direction, out float3 position, out float3 origin,
        out bool valid, float distance = 1f)
    {
        direction = default;
        position = default;
        origin = default;
        valid = false;
        if (ScriptInput.Of(ctx) is not { IsVRActive: false } input || !float.IsFinite(point.x) || !float.IsFinite(point.y))
            return;
        if (!input.TryGetDesktopViewRay(point, out var from, out var through))
            return;
        origin = from;
        direction = through;
        position = from + through * (float.IsFinite(distance) ? distance : 0f);
        valid = true;
    }
}
