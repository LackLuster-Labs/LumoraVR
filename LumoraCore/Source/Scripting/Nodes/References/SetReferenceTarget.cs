// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Points a reference field at target, as the script's author, judged by the gate like Set Field. A
// target the script cannot see counts as nothing and clears it. One the reference cannot hold is a
// fault; a drive, a hook, an action or a reference with rules of its own for who sets it is refused.
// written is false when there was no reference or the set was refused.
[Node("World/References/Set Reference Target", Capability.WorldWrite, Description = "Points a reference field at target, as the script's author, judged by the gate like Set Field.",
    Keywords = "pointer, assign reference, point at")]
public sealed class SetReferenceTarget<T> : ActionNode where T : class, IWorldElement
{
    public static void Run(ScriptContext ctx, ISyncRef? reference, T? target, out bool written)
    {
        written = false;
        if (!ScriptReads.ReachedElement(ctx, reference as IWorldElement, "reference"))
            return;
        ScriptWrites.WriteReference(ctx, reference, ScriptReads.VisibleElement(ctx, target) ? target : null);
        written = true;
    }
}
