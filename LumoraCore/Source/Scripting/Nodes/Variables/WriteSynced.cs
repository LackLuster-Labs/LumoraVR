// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Keeps a value under a name on this script's object, for every machine to read with Read Synced. Only a
// Shared script keeps them, on the host.
[Node("Variables/Synced/Write Synced", Capability.Events, Description = "Keeps a value under a name on this script's object, for every machine to read with Read Synced.",
    Keywords = "replicated value, synced variable, shared value, sync, multiplayer")]
public sealed class WriteSynced<T> : ActionNode where T : IEquatable<T>
{
    public static void Run(ScriptContext ctx, string? name, T value)
    {
        if (ctx.OffHost)
            throw new UnauthorizedAccessException("a Local script reads synced values; a Shared script keeps them");
        if (!ScriptSynced.CheckName(name))
        {
            ctx.Fail("no name");
            return;
        }
        if (ctx.Host is not { IsDestroyed: false } host || !ctx.World.IsAuthority)
        {
            ctx.Fail("this script keeps no synced values here");
            return;
        }
        if (host.WriteSynced(name!, ScriptSynced.TypeName<T>(), ScriptSynced.Format(value)) && host.Synced.Count == ScriptSynced.WarnValues + 1)
            ctx.Warn(ScriptWarning.SyncedValues, $"more than {ScriptSynced.WarnValues} synced values on one object");
    }
}
