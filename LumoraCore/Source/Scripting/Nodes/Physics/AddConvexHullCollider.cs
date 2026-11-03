// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components;
using Lumora.Core.Components.Meshes;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// A solid collider shaped as the convex hull of a mesh, on slot: cheap to collide with, and a shape a rigid body
// can carry. mesh is a mesh or a renderer, whose mesh is taken; left empty, the renderer on slot is. A hull
// collider already on slot for the same mesh is handed back instead of a second. The hull is worked out once the
// mesh has loaded, the same on every machine. Only where the script may already change things, as Attach Mesh.
[Node("Physics/Add Convex Hull Collider", Capability.WorldStructure, Description = "A solid collider shaped as the convex hull of a mesh, on slot: cheap to collide with, and a shape a rigid body can carry. mesh is a mesh or a renderer, whose mesh is taken; left empty, the renderer on slot is.",
    Keywords = "collider from mesh, add collision, physics shape")]
public sealed class AddConvexHullCollider : ActionNode
{
    [return: Port("collider")]
    public static Component? Run(ScriptContext ctx, Slot? slot, Component? mesh)
    {
        if (!ScriptReads.Reached(ctx, slot))
            return null;
        var from = mesh ?? slot.GetComponent<MeshRenderer>();
        if (from != null && !ScriptReads.Reached(ctx, from, "mesh"))
            return null;
        if (ConvexHullPointSource.Resolve(from) is not { } source)
        {
            ctx.Fail(mesh == null ? $"{ScriptReads.Named(slot)} draws no mesh to take a hull of" : $"{ScriptReads.Named(mesh)} is not a mesh");
            return null;
        }
        ScriptAttach.CheckPlace(ctx, slot);
        using (ctx.IsLocal ? null : ScriptWrites.RangeOf(ctx.World, ctx.Principal))
            return ConvexHullCollider.Ensure(slot, source);
    }
}
