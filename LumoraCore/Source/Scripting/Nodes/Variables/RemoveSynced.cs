// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Drops a synced value this script keeps, freeing its place under the object's limit. Shared scripts only.
[Node("Variables/Synced/Remove Synced", Capability.Events, Description = "Drops a synced value this script keeps, freeing its place under the object's limit.",
    Keywords = "synced variable, shared value, delete synced value")]
public sealed class RemoveSynced : ActionNode
{
    public static void Run(ScriptContext ctx, string? name)
    {
        if (ctx.OffHost)
            throw new UnauthorizedAccessException("a Local script reads synced values; a Shared script keeps them");
        if (string.IsNullOrEmpty(name))
        {
            ctx.Fail("no name");
            return;
        }
        if (ctx.Host is not { IsDestroyed: false } host || !ctx.World.IsAuthority)
        {
            ctx.Fail("this script keeps no synced values here");
            return;
        }
        host.RemoveSynced(name);
    }
}
