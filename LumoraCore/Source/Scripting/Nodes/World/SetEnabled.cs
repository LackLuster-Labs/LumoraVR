// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Turns a bound component on or off.
[Node("World/Components/Set Enabled", Capability.WorldWrite, Description = "Turns a bound component on or off.",
    Keywords = "enable component, disable component, turn on, turn off")]
public sealed class SetEnabled : ActionNode
{
    public static void Run(ScriptContext ctx, Component? component, bool enabled)
    {
        if (ScriptReads.Reached(ctx, component))
            ScriptWrites.Set(ctx, component!.Enabled, enabled);
    }
}
