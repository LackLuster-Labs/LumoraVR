// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;

namespace Lumora.Core.Scripting;

// The one text field this machine types into, as a Local script may touch it: only fields in the script's
// own world, only while that world is the one in front of the person, and a field is only given the keys
// in answer to something the person at this machine did. Typing holds their walking keys, so a script that
// could focus a field whenever it liked could pin them in place; the dash's fields are in another world and
// never seen here. -xlinka
internal static class ScriptTextFocus
{
    public static bool InFront(ScriptContext ctx) => ScriptInput.InFront(ctx);

    // The text field on a slot, when a Local script in front of the person may see it.
    public static TextInput? FieldOn(ScriptContext ctx, Slot? slot)
        => InFront(ctx) && ScriptReads.Visible(ctx, slot) && slot!.GetComponent<TextInput>() is { IsDestroyed: false } field ? field : null;

    // Why FieldOn found nothing, for the action that asked.
    public static void Missed(ScriptContext ctx, Slot? slot)
    {
        if (!InFront(ctx))
            ctx.Fail("this world is not the one in front of the person");
        else if (ScriptReads.Reached(ctx, slot))
            ctx.Fail($"{ScriptReads.Named(slot)} has no text field");
    }

    // The field this machine is typing into, when it is one the script can see.
    public static TextInput? Focused(ScriptContext ctx)
        => InFront(ctx) && TextInput.Focused is { IsDestroyed: false } field && ScriptReads.Visible(ctx, field.Slot) ? field : null;

    // What this call answers: an event this machine's person set off.
    public static bool ByThisPerson(ScriptContext ctx)
        => ctx.Cause is { IsDestroyed: false } cause && ReferenceEquals(cause, ctx.World.LocalUser);
}
