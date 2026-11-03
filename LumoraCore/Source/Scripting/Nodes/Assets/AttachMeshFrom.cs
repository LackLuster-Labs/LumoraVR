// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Puts a model file from an address on a slot as a mesh and carries on through next at once. attached runs
// with the mesh once it is on the slot; failed when the address or the slot cannot be used; denied when the
// person asked about the address's host said no or did not answer. With reuse on, a mesh already on the slot
// from the same address is handed back instead of a second one. Addresses, leave and places as Attach
// Texture: local:// freely, http and https with the leave web requests ask for, only where the script may
// already change things.
[Node("Assets/Meshes/Attach Mesh", Capability.WorldStructure, Description = "Attaches a mesh from a URL to a slot. Continues immediately and reports attachment or failure separately.",
    Keywords = "load model, model from url, mesh from url")]
[Answers("attached", "failed", "denied")]
public sealed class AttachMeshFrom : AwaitNode<MeshAttached>
{
    public static void Start(ScriptContext ctx, AwaitTicket<MeshAttached> ticket, Slot? slot, string? url, bool reuse = true)
        => ScriptAttach.Start(ctx, ticket, slot, url, (target, address) =>
        {
            if (reuse)
            {
                foreach (var component in target.Components)
                {
                    if (component is MeshProvider found && !found.IsDestroyed && found.URL.Value == address)
                        return new MeshAttached(found, string.Empty);
                }
            }
            var mesh = ScriptAttach.Attach(ctx, target, () =>
            {
                var made = target.AttachComponent<MeshProvider>();
                made.URL.Value = address;
                return made;
            }, made => made.URL.Value == address);
            return new MeshAttached(mesh, string.Empty);
        }, why => new MeshAttached(null, why));
}
