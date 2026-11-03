// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The user whose body or hands a slot is in: themselves for their user root, and anything they wear or
// hold; nobody otherwise. A component asks for the slot it is on.
[Node("Users/Searching/User Of", Capability.UsersRead, Description = "The user whose body or hands a slot is in: themselves for their user root, and anything they wear or hold; nobody otherwise.",
    Keywords = "owner, who is holding, holder, wearer, user from slot")]
public sealed class UserOf : ReadNode
{
    [return: Port("user")]
    public static User? Run(ScriptContext ctx, Slot? slot)
    {
        if (!ScriptReads.Visible(ctx, slot))
            return null;
        User? user = slot!.ActiveUser;
        return ScriptReads.Visible(ctx, user) ? user : null;
    }

    [return: Port("user")]
    public static User? Run(ScriptContext ctx, Component? component)
        => ScriptReads.Visible(ctx, component) ? Run(ctx, component!.Slot) : null;
}
