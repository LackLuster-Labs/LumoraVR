// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

// How much of somebody else's avatar this machine has to keep moving, from where it stands to the viewer's
// head: everything every frame up close and in front, less often further off, and nothing that only moves
// itself (swinging hair, idle animation) once it is behind the viewer or far enough to be a few pixels. The
// wearer's own avatar is always Full, and the whole thing is off with Ease Off Distant Avatars. Worked out at
// most once a frame per avatar. -xlinka
public static class AvatarViewLod
{
    public enum Level : byte
    {
        Full,
        Reduced,
        Distant,
        Hidden,
        // Drawn as a picture of itself (AvatarCrowd): the body is hidden and is only posed for the next picture.
        StandIn,
    }

    // In front: within this angle of where the head faces, which is past the edge of any lens it wears.
    private const float InViewCos = 0.17f;   // 80 degrees
    // Closer than this counts as in front whichever way the head faces: an arm's length off is in view the
    // moment it turns, and is what it bumps into.
    private const float AlwaysNear = 3f;
    private const float FullWithin = 10f;
    private const float ReducedWithin = 25f;
    private const float HiddenBeyond = 50f;

    private sealed class Entry
    {
        public ulong Frame = ulong.MaxValue;
        public Level Level;
        public float Distance;
        public bool InView;
        public bool Capturing;
        // One per avatar, so everything posing one body runs on the same frames and the skeleton takes its
        // picture of a whole pose, not of the IK's half of one frame and the hair's half of another.
        public readonly int Stagger = System.Threading.Interlocked.Increment(ref s_staggers) & 0x7fffffff;
    }

    private static int s_staggers;
    private static readonly ConditionalWeakTable<Slot, Entry> Cache = new();

    // What one avatar gets this frame, and on which frames.
    public readonly struct View
    {
        public readonly Level Level;
        public readonly float Distance;
        public readonly int Stagger;
        public readonly bool InView;
        // A picture of it is taken this frame, so a body that is only a picture still has to be posed now.
        public readonly bool Capturing;

        public View(Level level, float distance, int stagger, bool inView, bool capturing)
        {
            Level = level;
            Distance = distance;
            Stagger = stagger;
            InView = inView;
            Capturing = capturing;
        }

        // A frame the body is posed on: the IK, the hair, the animation and the skeleton all take this one.
        public bool PoseFrame(ulong frame)
        {
            if (Capturing)
                return true;
            int every = PoseInterval(Level);
            return every <= 1 || (frame + (ulong)Stagger) % (ulong)every == 0;
        }

        // For things that only move themselves (idle animation, finger poses): the pose frames in view, rarer
        // out of it.
        public bool RunsThisFrame(ulong frame)
        {
            if (Capturing)
                return true;
            int every = Interval(Level);
            return every <= 1 || (frame + (ulong)Stagger) % (ulong)every == 0;
        }
    }

    // The level for an avatar whose root slot is given (the slot carrying its AvatarForm, or the user root
    // wearing it). Callers resolve the root once and keep it.
    public static Level For(Slot? root) => Look(root).Level;

    // The level and how far the avatar stands from the viewer's head. The distance is zero when nothing was
    // measured: no viewer, the wearer's own avatar, or the easing switched off.
    public static Level For(Slot? root, out float distance)
    {
        var view = Look(root);
        distance = view.Distance;
        return view.Level;
    }

    public static View Look(Slot? root)
    {
        if (root == null || root.IsDestroyed)
            return default;
        var world = root.World;
        var local = world?.LocalUser?.Root;
        if (local == null || local.IsDestroyed || ReferenceEquals(root.ActiveUserRoot, local))
            return default;
        var head = local.HeadSlot;
        if (head == null || head.IsDestroyed)
            return default;

        ulong frame = world!.Time?.UpdateIndex ?? 0;
        var entry = Cache.GetValue(root, _ => new Entry());
        if (entry.Frame != frame)
            Measure(root, world, head, entry, frame);
        return new View(entry.Level, entry.Distance, entry.Stagger, entry.InView, entry.Capturing);
    }

    private static void Measure(Slot root, World world, Slot head, Entry entry, ulong frame)
    {
        var eye = head.GlobalPosition;
        var toward = root.GlobalPosition - eye;
        float distance = toward.Length;
        bool inView = distance < AlwaysNear
            || float3.Dot(head.GlobalRotation * float3.Backward, toward / distance) > InViewCos;
        // With a camera's picture on the desktop window, what the window shows is seen from the camera, so an
        // avatar it films is in view however the head is turned. Measured from the head alone, a body in the shot
        // behind the wearer was posed every fourth frame and its hair moved every fifteenth. -xlinka
        if (Lumora.Core.Photos.DesktopFeed.Viewpoint is { IsDestroyed: false } lens && ReferenceEquals(lens.World, world))
        {
            var fromLens = root.GlobalPosition - lens.GlobalPosition;
            float lensDistance = fromLens.Length;
            bool lensView = lensDistance < AlwaysNear
                || float3.Dot(lens.GlobalRotation * float3.Backward, fromLens / lensDistance) > InViewCos;
            if ((lensView && !inView) || (lensView == inView && lensDistance < distance))
            {
                distance = lensDistance;
                inView = lensView;
            }
        }

        // A picture is a picture whether or not the easing is on: the body under it is hidden either way.
        var member = AvatarCrowd.Find(world, root);
        Level level;
        if (member != null && member.Phase == AvatarCrowd.Phase.Picture)
            level = Level.StandIn;
        else if (!EngineSettings.ReduceDistantAvatars)
            level = Level.Full;
        else if (distance >= HiddenBeyond || !inView)
            level = Level.Hidden;
        else if (distance < FullWithin)
            level = Level.Full;
        else if (distance < ReducedWithin)
            level = Level.Reduced;
        else
            level = Level.Distant;

        entry.Frame = frame;
        entry.Level = level;
        entry.Distance = distance;
        entry.InView = inView;
        entry.Capturing = member != null && member.CaptureNow;
    }

    // Frames between the frames a body is posed on. Out of view it is posed now and then so it is never far
    // from right when it comes back; a picture's body only so its walk keeps up with it between pictures.
    public static int PoseInterval(Level level) => level switch
    {
        Level.Full => 1,
        Level.Reduced => 2,
        Level.Distant => 3,
        Level.Hidden => 4,
        _ => 8,
    };

    // Frames between runs for something that only moves itself; Hidden ones wait longest.
    public static int Interval(Level level) => level switch
    {
        Level.Full => 1,
        Level.Reduced => 2,
        Level.Distant => 3,
        _ => 15,
    };

    // Whether a thing at this level runs on this frame. The stagger (anything stable per instance) spreads a
    // room's worth of avatars at one level over the frames between, rather than all of them landing on one.
    public static bool RunsThisFrame(Level level, ulong frame, int stagger)
    {
        int every = Interval(level);
        return every <= 1 || (frame + (ulong)(stagger & 0x7fffffff)) % (ulong)every == 0;
    }

    // The avatar root a component under it belongs to: the user root wearing it, else the slot carrying its
    // AvatarForm, else null for something that is not on an avatar at all.
    public static Slot? RootOf(Slot? slot)
    {
        if (slot == null || slot.IsDestroyed)
            return null;
        var user = slot.ActiveUserRoot;
        if (user != null && !user.IsDestroyed)
            return user.Slot;
        return slot.GetComponentInParents<AvatarForm>()?.Slot;
    }
}
