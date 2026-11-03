// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whether a user is here, as their own machine reports it. Focused is this world being the one in front of
// them; vr is being in a headset rather than at a screen; inHeadset is wearing it with the session in front;
// present is focused and, in VR, wearing it. DashOpen is their dash being open. The headset flags and the
// dash follow within a second. All false for a user the script cannot see.
[Node("Users/Status/Presence", Capability.UsersRead, Description = "Whether a user is here, as their own machine reports it.",
    Keywords = "afk, away, idle, in headset, vr active, dash open")]
public sealed class UserPresence : ReadNode
{
    [return: Port("present")]
    public static bool Run(ScriptContext ctx, User? user, out bool focused, out bool vr, out bool inHeadset, out bool dashOpen)
    {
        focused = vr = inHeadset = dashOpen = false;
        if (!ScriptReads.Visible(ctx, user))
            return false;
        focused = user!.IsPresent.Value;
        vr = user.VRActive.Value;
        inHeadset = user.PresentInHeadset.Value;
        dashOpen = user.DashOpen.Value;
        return ScriptBodyReads.IsPresent(user);
    }
}
