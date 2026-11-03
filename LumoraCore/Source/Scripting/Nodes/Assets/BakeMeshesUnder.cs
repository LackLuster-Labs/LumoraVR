// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Merges the plain meshes under root into one mesh, one part per material, and carries on through next at
// once while the merge runs on a worker. baked runs with the "Baked Meshes" slot it made under root, how many
// meshes went into it and how many were left as they were; failed with why. Inactive slots and switched off
// renderers only when hidden is on. Skinned meshes, meshes not loaded on the machine running the script and
// renderers the script may not switch off are left as they were, as are colliders and everything else. A
// world script's or a Local script's originals are switched off, not removed, so switching them back on
// undoes the bake; a person's script makes an unsaved copy that goes when they leave and leaves their
// originals on. Only world content from a world script, the author's own things from a person's script and
// what a Local script made on its machine; a script baking more than a million vertices at once is warned.
[Node("Assets/Meshes/Bake Meshes", Capability.WorldStructure, Description = "Combines meshes under a slot into one mesh, keeping separate material parts. Reports the result when finished.",
    Keywords = "merge meshes, optimize, batch meshes")]
[Answers("baked", "failed")]
public sealed class BakeMeshesUnder : AwaitNode<MeshesBaked>
{
    public static void Start(ScriptContext ctx, AwaitTicket<MeshesBaked> ticket, Slot? root, bool hidden = false)
        => ScriptBake.Start(ctx, ticket, root, hidden);
}
