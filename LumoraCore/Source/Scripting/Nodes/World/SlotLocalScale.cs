// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Relative to the parent's scale.
[Node("Transform/Local/Local Scale", Capability.WorldRead, Description = "Reads a slot's scale relative to its parent's scale.")]
public sealed class SlotLocalScale : ReadNode
{
    [return: Port("scale")]
    public static float3 Run(ScriptContext ctx, Slot? slot)
        => ScriptReads.Visible(ctx, slot) ? slot!.LocalScale.Value : default;
}
