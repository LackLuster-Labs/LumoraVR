// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Points a reference field at nothing, judged by the gate like Set Field.
[Node("World/References/Clear Reference", Capability.WorldWrite, Description = "Points a reference field at nothing, judged by the gate like Set Field.",
    Keywords = "pointer, null reference, empty reference")]
public sealed class ClearReference : ActionNode
{
    public static void Run(ScriptContext ctx, ISyncRef? reference)
    {
        if (ScriptReads.ReachedElement(ctx, reference as IWorldElement, "reference"))
            ScriptWrites.WriteReference(ctx, reference, null);
    }
}
