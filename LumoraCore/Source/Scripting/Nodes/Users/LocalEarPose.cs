// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components.Audio;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Where the person at this machine hears this world from, in world space: the ear they listen through, which
// is their head's unless another ear given to them has taken over (overridden says so), and that ear's world
// scale. Their head, not overridden, while no ear of theirs is listening. For a Local script, the way the
// Input nodes are.
[Node("Users/Body/Local Ears", Capability.Input, Description = "Reads the world position, rotation and scale of the viewpoint this local user hears from.",
    Keywords = "listener, ears, hearing position")]
public sealed class LocalEarPose : ReadNode
{
    [ThreadStatic]
    private static List<AudioListener>? _ears;

    [return: Port("position")]
    public static float3 Run(ScriptContext ctx, out floatQ rotation, out float3 scale, out bool overridden)
    {
        var root = ScriptReads.RootOf(ctx.World.LocalUser);
        var ears = _ears ??= new List<AudioListener>();
        ears.Clear();
        ctx.World.Audio?.CollectLocalUserListeners(ears);
        AudioListener? ear = null;
        foreach (var listener in ears)
        {
            if (listener.ShouldBeActive && listener.Slot is { IsDestroyed: false })
            {
                ear = listener;
                break;
            }
        }
        ears.Clear();

        var at = ear?.Slot ?? root?.HeadSlot;
        if (at is { IsDestroyed: true })
            at = null;
        overridden = ear != null && !ReferenceEquals(ear, root?.PrimaryListener);
        rotation = at?.GlobalRotation ?? floatQ.Identity;
        scale = at?.GlobalScale ?? float3.One;
        return at?.GlobalPosition ?? default;
    }
}
