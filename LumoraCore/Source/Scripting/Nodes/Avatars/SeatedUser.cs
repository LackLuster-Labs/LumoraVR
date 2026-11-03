// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Avatar;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Who is in the seat on a slot, as the seat itself says: whoever's body hangs from it, else whom the host
// last ruled it for while that settles. Taken is whether anyone is. No user and false for a slot with no
// seat on it.
[Node("Interaction/Seats/Seated User", Capability.UsersRead, Description = "Who is in the seat on a slot, as the seat itself says: whoever's body hangs from it, else whom the host last ruled it for while that settles.",
    Keywords = "who is sitting, occupant, seat occupied")]
public sealed class SeatedUser : ReadNode
{
    [return: Port("user")]
    public static User? Run(ScriptContext ctx, Slot? seat, out bool taken)
    {
        taken = false;
        if (!ScriptReads.Visible(ctx, seat))
            return null;
        IAvatarAnchor? anchor = null;
        foreach (var component in seat!.Components)
        {
            if (component is IAvatarAnchor found && !component.IsDestroyed)
            {
                anchor = found;
                break;
            }
        }
        var user = anchor?.AnchoredUser;
        if (user == null || user.IsDestroyed)
            return null;
        taken = true;
        return ScriptReads.Visible(ctx, user) ? user : null;
    }
}
