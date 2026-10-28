// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Godot;

namespace Lumora.Godot.Helpers;

// One physics material per friction and bounce in use, handed to every collider body that asks for that pair.
// Every body used to be made with a material of its own, one allocation per collider and another per rebuild,
// for the same two numbers on all of them. A material goes once the last body using it moves on to another, so a
// value dragged across a range in the inspector leaves nothing behind. The plain surface (friction 1, no bounce)
// is no material at all: a body without one is exactly that. Hooks apply on the main thread only. -xlinka
internal static class SharedPhysicsMaterials
{
    private sealed class Entry
    {
        public PhysicsMaterial Material = null!;
        public int Users;
    }

    private static readonly Dictionary<(float Friction, float Bounce), Entry> _byValues = new();

    public static bool IsPlain(float friction, float bounce) => friction == 1f && bounce == 0f;

    // The material for the pair, counted as one more user of it; null for the plain surface.
    public static PhysicsMaterial? Take(float friction, float bounce)
    {
        if (IsPlain(friction, bounce))
            return null;
        var key = (friction, bounce);
        if (!_byValues.TryGetValue(key, out var entry))
        {
            entry = new Entry { Material = new PhysicsMaterial { Friction = friction, Bounce = bounce } };
            _byValues.Add(key, entry);
        }
        entry.Users++;
        return entry.Material;
    }

    // One user fewer of the pair taken before.
    public static void Give(float friction, float bounce)
    {
        if (IsPlain(friction, bounce))
            return;
        var key = (friction, bounce);
        if (_byValues.TryGetValue(key, out var entry) && --entry.Users <= 0)
            _byValues.Remove(key);
    }

    public static int InUse => _byValues.Count;
}
