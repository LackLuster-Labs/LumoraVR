// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core;
using Lumora.Core.Components.Avatar;

namespace Lumora.Godot.Hooks;

// WHEN SOMEBODY ELSE'S ACCESSORIES ARE PLACED.
//
// A renderer is placed whenever its slot moves (MeshRendererHookBase.Place), and an avatar
// walking moves every accessory it carries: a dozen guests were a thousand placements a frame. Somebody else's
// avatar is posed on its pose frames only (AvatarViewLod), so its accessories are placed on those same frames and
// stay with the body they hang off: every frame up close, every other one a little further, every fourth behind
// the viewer, and not at all while the avatar is a picture. A move on a frame in between is kept and placed on
// the avatar's next pose frame, so nothing is left where it was when the avatar stops. Main thread. -xlinka
internal static class AvatarPlacement
{
    private sealed class Seat
    {
        public Slot? Root;
        public ulong ResolvedAt = ulong.MaxValue;
    }

    private static readonly ConditionalWeakTable<IPlacedRenderer, Seat> Seats = new();
    private static readonly Dictionary<Slot, HashSet<IPlacedRenderer>> Waiting = new();
    private static readonly List<Slot> Done = new();
    private static bool _flushing;

    // True when this move is kept for the avatar's next pose frame instead of placed now.
    public static bool Defers(IPlacedRenderer renderer, Slot slot)
    {
        if (_flushing || !AvatarPictures.Ticking)
            return false;
        ulong frame = slot.World?.Time.UpdateIndex ?? 0;
        var seat = Seats.GetValue(renderer, static _ => new Seat());
        if (seat.ResolvedAt == ulong.MaxValue || frame - seat.ResolvedAt > 120 || frame < seat.ResolvedAt || seat.Root is { IsDestroyed: true })
        {
            seat.Root = AvatarViewLod.RootOf(slot);
            seat.ResolvedAt = frame;
        }
        var root = seat.Root;
        if (root == null || PlacesNow(root, frame))
            return false;
        if (!Waiting.TryGetValue(root, out var stale))
        {
            stale = new HashSet<IPlacedRenderer>(ReferenceEqualityComparer.Instance);
            Waiting[root] = stale;
        }
        stale.Add(renderer);
        return true;
    }

    private static bool PlacesNow(Slot root, ulong frame)
    {
        var view = AvatarViewLod.Look(root);
        return view.Level switch
        {
            AvatarViewLod.Level.Full => true,
            AvatarViewLod.Level.StandIn => false,
            _ => view.PoseFrame(frame),
        };
    }

    // Once a frame, after the world: whatever was kept is placed once its avatar reaches a pose frame.
    public static void Flush()
    {
        if (Waiting.Count == 0)
            return;
        _flushing = true;
        try
        {
            foreach (var (root, stale) in Waiting)
            {
                if (root.IsDestroyed)
                {
                    Done.Add(root);
                    continue;
                }
                if (!PlacesNow(root, root.World?.Time.UpdateIndex ?? 0))
                    continue;
                foreach (var renderer in stale)
                    renderer.Place(false);
                Done.Add(root);
            }
        }
        finally
        {
            _flushing = false;
        }
        foreach (var root in Done)
            Waiting.Remove(root);
        Done.Clear();
    }
}
