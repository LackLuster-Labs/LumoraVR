// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// In the parent's space.
[Node("Transform/Local/Local Position", Capability.WorldRead, Description = "Reads a slot's position relative to its parent.")]
public sealed class SlotLocalPosition : ReadNode
{
    [return: Port("position")]
    public static float3 Run(ScriptContext ctx, Slot? slot)
        => ScriptReads.Visible(ctx, slot) ? slot!.LocalPosition.Value : default;
}
