// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Input;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The hand a user leads with, as they set it, told by their own machine. None for a user the script cannot see.
[Node("Users/Body/Primary Hand", Capability.UsersRead, Description = "The hand a user leads with, as they set it, told by their own machine.",
    Keywords = "dominant hand, handedness, left handed, main hand")]
public sealed class PrimaryHand : ReadNode
{
    [return: Port("hand")]
    public static Chirality Run(ScriptContext ctx, User? user)
        => ScriptReads.Visible(ctx, user) ? user!.PrimaryHand.Value : Chirality.None;
}
