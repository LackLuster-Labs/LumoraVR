// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// What a reference field points at, as T: a slot, a user, a component, a field or an asset. Nothing when
// it points at nothing, at something of another kind, or at something the script cannot see, and nothing
// from a host-only reference.
[Node("World/References/Reference Target", Capability.WorldRead, Description = "What a reference field points at, as T: a slot, a user, a component, a field or an asset.",
    Keywords = "pointer, get reference, read reference")]
public sealed class ReferenceTarget<T> : ReadNode where T : class, IWorldElement
{
    [return: Port("target")]
    public static T? Run(ScriptContext ctx, ISyncRef? reference)
        => ScriptReads.Readable(ctx, reference) && reference!.Target is T target && ScriptReads.VisibleElement(ctx, target) ? target : null;
}
