// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Puts a picture from an address on a slot as a UI sprite: the texture, and a sprite drawing the whole of it,
// for images and panels to show. Carries on through next at once. attached runs with both once they are on
// the slot; failed when the address or the slot cannot be used; denied when the person asked about the
// address's host said no or did not answer. With reuse on, a sprite already on the slot drawing a texture
// from the same address is handed back instead of a second one. Addresses, leave and places as Attach
// Texture: local:// freely, http and https with the leave web requests ask for, only where the script may
// already change things.
[Node("Assets/Attach Sprite", Capability.WorldStructure, Description = "Puts a picture from an address on a slot as a UI sprite: the texture, and a sprite drawing the whole of it, for images and panels to show.",
    Keywords = "ui image from url, sprite from url, load icon")]
[Answers("attached", "failed", "denied")]
public sealed class AttachSpriteFrom : AwaitNode<SpriteAttached>
{
    public static void Start(ScriptContext ctx, AwaitTicket<SpriteAttached> ticket, Slot? slot, string? url, bool reuse = true)
        => ScriptAttach.Start(ctx, ticket, slot, url, (target, address) =>
        {
            if (reuse)
            {
                foreach (var component in target.Components)
                {
                    if (component is Sprite found && !found.IsDestroyed && found.Texture.Target is ImageProvider image
                        && !image.IsDestroyed && image.URL.Value == address)
                        return new SpriteAttached(image, found, string.Empty);
                }
            }
            var texture = AttachTextureFrom.Picture(ctx, target, address, reuse);
            var sprite = ScriptAttach.Attach(ctx, target, () =>
            {
                var made = target.AttachComponent<Sprite>();
                made.Texture.Target = texture;
                return made;
            }, made => ReferenceEquals(made.Texture.Target, texture));
            return new SpriteAttached(texture, sprite, string.Empty);
        }, why => new SpriteAttached(null, null, why));
}
