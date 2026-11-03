// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Gives this machine's keys to the text field on the slot, the way clicking it does, and says whether it
// did. Only in answer to something the person here did, such as their own press or grab, while this world
// is in front of them, on a field they could type into themselves. Anywhere else it does nothing. A call
// someone else set off is no failure, since their own machine's copy answers it; the rest are.
[Node("UI/Text Input/Focus Field", Capability.Input, Description = "Gives this machine's keys to the text field on the slot, the way clicking it does, and says whether it did.")]
public sealed class FocusTextField : ActionNode
{
    [return: Port("focused")]
    public static bool Run(ScriptContext ctx, Slot? slot)
    {
        if (ScriptTextFocus.FieldOn(ctx, slot) is not { } field)
        {
            ScriptTextFocus.Missed(ctx, slot);
            return false;
        }
        if (!ScriptTextFocus.ByThisPerson(ctx))
        {
            if (ctx.Cause == null)
                ctx.Fail("a field is focused only in answer to something a person did");
            return false;
        }
        if (field.ReadOnly.Value || !field.CanInteract)
        {
            ctx.Fail($"the field on {ScriptReads.Named(slot)} cannot be typed into");
            return false;
        }
        if (ctx.World.DataModelPermissions?.AllowsDomain(ctx.World.LocalUser, DataModelPermissionDomain.Touch) == false)
        {
            ctx.Fail("their role may not touch things here");
            return false;
        }
        field.Focus();
        if (field.IsFocused)
            return true;
        ctx.Fail($"the field on {ScriptReads.Named(slot)} did not take the keys");
        return false;
    }
}
