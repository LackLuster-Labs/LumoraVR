// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components.Interaction;

// Nothing taken out from under this slot goes back under it when let go: it lands at the world root instead.
// Depth is how many levels below this slot that holds for, 0 for the slot alone. A panel built of grabbable
// parts uses it so a part carried off does not snap back into the panel. -xlinka
[ComponentCategory("Interaction/Grabbables")]
[SingleInstancePerSlot]
public sealed class ReleaseReturnBlock : Component, IReleaseReturnBlock
{
    public readonly Sync<bool> Blocks;
    public readonly Sync<int> Depth;

    public ReleaseReturnBlock()
    {
        Blocks = new Sync<bool>(this, true);
        Depth = new Sync<int>(this, int.MaxValue);
    }

    bool IReleaseReturnBlock.BlocksReturn => Enabled.Value && Blocks.Value;

    int IReleaseReturnBlock.ReturnBlockDepth => Depth.Value;
}
