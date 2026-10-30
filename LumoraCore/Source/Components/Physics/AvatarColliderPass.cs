// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core.Components.Avatar;

namespace Lumora.Core.Components;

// ONE LOOK PER AVATAR AT ITS COLLIDERS.
//
// A collider follows its slot through the slot's moved event. On an avatar that event fires for every collider
// on every bone every frame, a crowd's thousand and more, each a queued notice, a sorted pass and a call, even on
// the frames the collider was going to sit out. Once a collider is seen on an avatar it leaves the event and
// joins its avatar's group here. Each frame, before the hooks run, every group whose turn it is compares each of
// its colliders' world matrices with what it last followed and follows the ones that moved; a group not due
// this frame costs nothing. The turn comes from where the avatar stands to the viewer: every frame close by and
// in front, every second frame past a few metres or out to 25, every fourth to 50, every sixteenth behind the
// viewer or past 50. A collider that leaves the avatar goes back to its slot's event. -xlinka
internal sealed class AvatarColliderPass
{
    private const float ReachMetres = 4f;

    private sealed class Group
    {
        public Slot Root = null!;
        public readonly List<Collider> Colliders = new();
        public int Stagger;
    }

    private static readonly ConditionalWeakTable<World, AvatarColliderPass> Passes = new();

    private readonly World _world;
    private readonly Dictionary<Slot, Group> _groups = new();
    private readonly List<Group> _order = new();
    private int _staggers;

    private AvatarColliderPass(World world)
    {
        _world = world;
        world.AddFramePass(Tick);
    }

    public static AvatarColliderPass For(World world) => Passes.GetValue(world, w => new AvatarColliderPass(w));

    public void Add(Collider collider, Slot root)
    {
        if (!_groups.TryGetValue(root, out var group))
        {
            group = new Group { Root = root, Stagger = _staggers++ };
            _groups[root] = group;
            _order.Add(group);
        }
        group.Colliders.Add(collider);
    }

    public void Remove(Collider collider, Slot root)
    {
        if (_groups.TryGetValue(root, out var group))
            group.Colliders.Remove(collider);
    }

    private static int Interval(AvatarViewLod.Level level, float distance) => level switch
    {
        AvatarViewLod.Level.Reduced => 2,
        AvatarViewLod.Level.Distant => 4,
        AvatarViewLod.Level.Hidden or AvatarViewLod.Level.StandIn => 16,
        _ => distance > ReachMetres ? 2 : 1,
    };

    private void Tick()
    {
        ulong frame = _world.Time?.UpdateIndex ?? 0;
        for (int g = _order.Count - 1; g >= 0; g--)
        {
            var group = _order[g];
            if (group.Root.IsDestroyed || group.Colliders.Count == 0)
            {
                _groups.Remove(group.Root);
                _order[g] = _order[^1];
                _order.RemoveAt(_order.Count - 1);
                continue;
            }
            int every = Interval(AvatarViewLod.For(group.Root, out float distance), distance);
            if (every > 1 && (frame + (ulong)group.Stagger) % (ulong)every != 0)
                continue;
            var colliders = group.Colliders;
            for (int i = colliders.Count - 1; i >= 0; i--)
            {
                if (colliders[i].PassLook(frame))
                    continue;
                colliders[i] = colliders[^1];
                colliders.RemoveAt(colliders.Count - 1);
            }
        }
    }
}
