// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Assets;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Puts a sound file from an address on a slot as an audio clip and carries on through next at once.
// attached runs with the clip once it is on the slot; failed when the address or the slot cannot be used;
// denied when the person asked about the address's host said no or did not answer. With reuse on, a clip
// already on the slot from the same address is handed back instead of a second one. Addresses, leave and
// places as Attach Texture: local:// freely, http and https with the leave web requests ask for, only where
// the script may already change things.
[Node("Assets/Audio/Attach Audio Clip", Capability.WorldStructure, Description = "Attaches an audio clip from a URL to a slot. Continues immediately and reports attachment or failure separately.",
    Keywords = "load sound, sound from url, audio from url, sound file")]
[Answers("attached", "failed", "denied")]
public sealed class AttachClipFrom : AwaitNode<ClipAttached>
{
    public static void Start(ScriptContext ctx, AwaitTicket<ClipAttached> ticket, Slot? slot, string? url, bool reuse = true)
        => ScriptAttach.Start(ctx, ticket, slot, url, (target, address) =>
        {
            if (reuse)
            {
                foreach (var component in target.Components)
                {
                    if (component is StaticAudioClip found && !found.IsDestroyed && found.URL.Value == address)
                        return new ClipAttached(found, string.Empty);
                }
            }
            var clip = ScriptAttach.Attach(ctx, target, () =>
            {
                var made = target.AttachComponent<StaticAudioClip>();
                made.URL.Value = address;
                return made;
            }, made => made.URL.Value == address);
            return new ClipAttached(clip, string.Empty);
        }, why => new ClipAttached(null, why));
}
