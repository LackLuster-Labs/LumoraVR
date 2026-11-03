// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Assets;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Puts a picture from an address on a slot as a texture and carries on through next at once. attached runs
// with the texture once it is on the slot; failed when the address or the slot cannot be used, with error
// saying why; denied when the person asked about the address's host said no or did not answer. With reuse
// on, a texture already on the slot from the same address is handed back instead of a second one.
//
// A local:// or lumora:/// address needs nobody's leave. An http or https one needs the leave of the user at
// the machine running the script, the same leave web requests ask for: the host's user for a Shared script, each
// person on their own machine for a Local one, a local address or this machine included. An address no
// socket can open, and any other kind of address, is refused. Only on the world's own content from a world script, on its author's own
// things from a person's script, and on what a Local script made on its machine; never the world root, a
// protected slot, or anything in someone's body or hands. Every machine in the session loads the picture
// once something uses it.
[Node("Assets/Textures/Attach Texture", Capability.WorldStructure, Description = "Attaches a texture from a URL to a slot. Continues immediately and reports attachment or failure separately.",
    Keywords = "load image, image from url, load picture, web image, texture from url")]
[Answers("attached", "failed", "denied")]
public sealed class AttachTextureFrom : AwaitNode<TextureAttached>
{
    public static void Start(ScriptContext ctx, AwaitTicket<TextureAttached> ticket, Slot? slot, string? url, bool reuse = true)
        => ScriptAttach.Start(ctx, ticket, slot, url,
            (target, address) => new TextureAttached(Picture(ctx, target, address, reuse), string.Empty),
            why => new TextureAttached(null, why));

    internal static ImageProvider Picture(ScriptContext ctx, Slot slot, Uri address, bool reuse)
    {
        if (reuse)
        {
            foreach (var component in slot.Components)
            {
                if (component is ImageProvider found && !found.IsDestroyed && found.URL.Value == address)
                    return found;
            }
        }
        return ScriptAttach.Attach(ctx, slot, () =>
        {
            var image = slot.AttachComponent<ImageProvider>();
            image.URL.Value = address;
            return image;
        }, image => image.URL.Value == address);
    }
}
