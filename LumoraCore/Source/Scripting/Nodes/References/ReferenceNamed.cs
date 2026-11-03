// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Networking.Sync;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// A component's reference field by its name, exact and case sensitive: Clip on a clip player, Source on
// an audio output, Texture on a material. Nothing when the component has no reference by that name, or
// it is host-only.
[Node("World/References/Reference Named", Capability.WorldRead, Description = "A component's reference field by its name, exact and case sensitive: Clip on a clip player, Source on an audio output, Texture on a material.",
    Keywords = "pointer, reference by name")]
public sealed class ReferenceNamed : ReadNode
{
    [return: Port("reference")]
    public static ISyncRef? Run(ScriptContext ctx, Component? component, string? name)
    {
        if (!ScriptReads.Visible(ctx, component) || string.IsNullOrEmpty(name))
            return null;
        for (int i = 0; i < component!.SyncMemberCount; i++)
        {
            if (component.GetSyncMemberName(i) == name)
                return component.GetSyncMember(i) is ISyncRef reference and not ConflictingSyncElement { IsHostOnly: true } ? reference : null;
        }
        return null;
    }
}
