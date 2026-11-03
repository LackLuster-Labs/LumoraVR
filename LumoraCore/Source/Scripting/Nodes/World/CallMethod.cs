// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Reflection;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Calls a method a component's type opens to scripts, by its name, the way its inspector button does: Play,
// Pause or Stop on a player, Rebake on a probe, FitToMesh on a collider. Method Names lists them. Judged first
// as a change to the component by the script's author, and every write the method makes is the author's own. A
// component the script cannot change, or no such method, fails; so does a method that stops part way, saying why.
[Node("World/Components/Call Method", Capability.WorldWrite, Description = "Calls a method a component's type opens to scripts, by its name, the way its inspector button does: Play, Pause or Stop on a player, Rebake on a probe, FitToMesh on a collider.",
    Keywords = "invoke, press button, trigger, run action, call function")]
public sealed class CallMethod : ActionNode
{
    public static void Run(ScriptContext ctx, Component? component, string? method)
    {
        if (!ScriptReads.Reached(ctx, component))
            return;
        if (string.IsNullOrEmpty(method))
        {
            ctx.Fail("no method");
            return;
        }
        if (ScriptComponents.Method(component!, method) is not { } found)
        {
            ctx.Fail($"{ScriptComponents.TypeName(component!)} has no method '{method}' scripts may call");
            return;
        }
        ScriptWrites.Check(ctx, component!.Enabled);
        try
        {
            found.Invoke(component, null);
        }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is UnauthorizedAccessException refusal)
        {
            throw refusal;
        }
        catch (TargetInvocationException wrapped)
        {
            ctx.Fail($"{method} stopped part way: {wrapped.InnerException?.Message ?? wrapped.Message}");
        }
    }
}
