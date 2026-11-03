// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The user's account id, which the host sets only once the user has proved the account is theirs: the
// same user has it in every session, unlike a name they can change. Empty for a guest.
[Node("Users/Info/User ID", Capability.UsersRead, Description = "The user's account id, which the host sets only once the user has proved the account is theirs: the same user has it in every session, unlike a name they can change.")]
public sealed class UserIdentity : ReadNode
{
    [return: Port("id")]
    public static string Run(ScriptContext ctx, User? user)
        => ScriptReads.Visible(ctx, user) ? user!.AccountId.Value ?? string.Empty : string.Empty;
}
