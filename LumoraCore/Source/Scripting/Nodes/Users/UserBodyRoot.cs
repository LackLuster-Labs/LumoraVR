// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The slot the user's body hangs from, for the slot nodes to read. Null for a user with no body yet.
[Node("Users/Body/Root Slot", Capability.UsersRead, Description = "The slot the user's body hangs from, for the slot nodes to read.",
    Keywords = "player slot, user slot")]
public sealed class UserBodyRoot : ReadNode
{
    [return: Port("slot")]
    public static Slot? Run(ScriptContext ctx, User? user)
    {
        if (!ScriptReads.Visible(ctx, user))
            return null;
        var slot = ScriptReads.RootOf(user)?.Slot;
        return ScriptReads.Visible(ctx, slot) ? slot : null;
    }
}
