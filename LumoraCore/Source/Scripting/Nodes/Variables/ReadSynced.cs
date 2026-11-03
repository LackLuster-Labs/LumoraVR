// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Components.Scripting;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// A value a Shared script on target keeps, by name, as this machine last heard it. Found is false when no
// script there keeps that name, or keeps it as something this type cannot read: text reads any value,
// numbers read across number types, whole ones only into int and long.
[Node("Variables/Synced/Read Synced", Capability.WorldRead, Description = "A value a Shared script on target keeps, by name, as this machine last heard it.",
    Keywords = "replicated value, synced variable, shared value, sync, multiplayer")]
public sealed class ReadSynced<T> : ReadNode where T : IEquatable<T>
{
    [return: Port("value")]
    public static T Run(ScriptContext ctx, Slot? target, string? name, out bool found)
    {
        found = false;
        if (!ScriptReads.Visible(ctx, target) || string.IsNullOrEmpty(name))
            return default!;
        var components = target!.Components;
        for (int i = 0; i < components.Count; i++)
        {
            if (components[i] is ScriptHost { IsDestroyed: false } host && host.TryReadSynced(name, out T value))
            {
                found = true;
                return value;
            }
        }
        return default!;
    }
}
