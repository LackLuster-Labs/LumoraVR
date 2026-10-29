// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Scripting;

// A person's body as the user and avatar nodes read it: the slots tracking moves and the bones of the avatar
// they wear. Where their feet, hips and facing are is the user root's to say (UserRoot's body points), so a
// script, a seat and an anchor all agree. Everything here reads; nothing moves anybody. -xlinka
internal static class ScriptBodyReads
{
    // The slot tracking moves for a body node: the head, a controller, a hand, a foot, or whatever else a
    // positioner or socket on the user root carries. Root is the whole rig. Null for one they lack.
    public static Slot? Tracked(UserRoot root, BodyNode node)
    {
        switch (node)
        {
            case BodyNode.NONE:
                return null;
            case BodyNode.Root:
                return Alive(root.Slot);
            case BodyNode.Head:
                return Alive(root.HeadSlot);
            case BodyNode.LeftHand:
                return Alive(root.LeftHandSlot);
            case BodyNode.RightHand:
                return Alive(root.RightHandSlot);
            case BodyNode.LeftFoot:
                return Alive(root.LeftFootSlot);
            case BodyNode.RightFoot:
                return Alive(root.RightFootSlot);
        }
        var positioner = root.GetRegisteredComponent(PositionerFor(node));
        if (Alive(positioner?.Slot) is { } tracked)
            return tracked;
        return Alive(root.GetRegisteredComponent(SocketFor(node))?.Slot);
    }

    // One match per body node, made once: these searches run every frame for every person a script looks at,
    // and a new closure each time was an allocation each time.
    private static readonly ConcurrentDictionary<BodyNode, Predicate<TrackedDevicePositioner>> _positioners = new();
    private static readonly ConcurrentDictionary<BodyNode, Predicate<AvatarSocket>> _sockets = new();

    private static Predicate<TrackedDevicePositioner> PositionerFor(BodyNode node)
        => _positioners.GetOrAdd(node, static wanted => positioner => positioner.AutoBodyNode.Value == wanted);

    private static Predicate<AvatarSocket> SocketFor(BodyNode node)
        => _sockets.GetOrAdd(node, static wanted => socket => socket.Node.Value == wanted);

    // The humanoid rig of the avatar a user wears. Null while they wear the default pieces or an avatar
    // with no rig.
    public static HumanoidRig? RigOf(UserRoot root)
    {
        var rig = root.GetRegisteredComponent<AvatarEquipManager>()?.WornHumanoidRig;
        return rig is { IsDestroyed: false } ? rig : null;
    }

    public static Slot? Bone(HumanoidRig? rig, BodyNode node)
        => rig == null || node == BodyNode.NONE ? null : Alive(rig.TryGetBone(node));

    // A body node's slot at or under a slot: a rig's bone first, then a socket or a positioner naming it.
    public static Slot? FindUnder(Slot slot, BodyNode node)
    {
        if (node == BodyNode.NONE)
            return null;
        if (Bone(slot.GetComponentInChildren<HumanoidRig>(), node) is { } bone)
            return bone;
        if (Alive(slot.GetComponentInChildren(SocketFor(node))?.Slot) is { } socket)
            return socket;
        return Alive(slot.GetComponentInChildren(PositionerFor(node))?.Slot);
    }

    // A hand for the nearest search and the body pose: the hand socket, else the controller carrying it.
    public static Slot? Hand(UserRoot root, bool left)
        => Tracked(root, left ? BodyNode.LeftHand : BodyNode.RightHand)
           ?? Tracked(root, left ? BodyNode.LeftController : BodyNode.RightController);

    // A foot: a tracked one, else the avatar's foot bone.
    public static Slot? Foot(UserRoot root, HumanoidRig? rig, bool left)
    {
        var node = left ? BodyNode.LeftFoot : BodyNode.RightFoot;
        return Tracked(root, node) ?? Bone(rig, node);
    }

    // In front of this world and, in a headset, wearing it.
    public static bool IsPresent(User user)
        => user.IsPresent.Value && (user.PresentInHeadset.Value || !user.VRActive.Value);

    // A sided body node on a side; one down the middle, or a side of None, leaves it as it is.
    public static BodyNode OnSide(BodyNode node, Chirality side)
    {
        var current = node.GetChirality();
        return current == Chirality.None || side == Chirality.None || current == side ? node : Mirror(node);
    }

    // Left for right and right for left; a node down the middle stays.
    public static BodyNode Mirror(BodyNode node) => node.GetChirality() switch
    {
        Chirality.Left => node.GetRightSide(),
        Chirality.Right => node switch
        {
            BodyNode.RightController => BodyNode.LeftController,
            BodyNode.RightEye => BodyNode.LeftEye,
            >= BodyNode.RightShoulder and <= BodyNode.RightPinky_Tip => node - (BodyNode.RightShoulder - BodyNode.LeftShoulder),
            >= BodyNode.RightUpperLeg and <= BodyNode.RightToes => node - (BodyNode.RightUpperLeg - BodyNode.LeftUpperLeg),
            _ => node,
        },
        _ => node,
    };

    public enum Part
    {
        Head,
        Hand,
        Foot,
    }

    // The nearest head, hand or foot to a point among the people the script can see, leaving out ignore and,
    // with presentOnly, anyone not present. Left and right pick which of a pair count. Side is the one that
    // was nearest, None for a head. Nobody qualifying gives no user and an infinite distance, so a distance
    // test never passes for nobody.
    public static User? Nearest(ScriptContext ctx, float3 point, Part part, bool left, bool right, User? ignore, bool presentOnly,
        out Slot? slot, out float distance, out Chirality side)
    {
        Slot? bestSlot = null;
        float best = float.PositiveInfinity;
        var bestSide = Chirality.None;
        User? nearest = null;
        foreach (var user in ScriptReads.Users(ctx))
        {
            if (ReferenceEquals(user, ignore) || (presentOnly && !IsPresent(user)))
                continue;
            var root = ScriptReads.RootOf(user);
            if (root == null || !ScriptReads.Visible(ctx, root.Slot))
                continue;
            switch (part)
            {
                case Part.Head:
                    Consider(user, Tracked(root, BodyNode.Head), Chirality.None);
                    break;
                case Part.Hand:
                    if (left)
                        Consider(user, Hand(root, left: true), Chirality.Left);
                    if (right)
                        Consider(user, Hand(root, left: false), Chirality.Right);
                    break;
                case Part.Foot:
                    var rig = RigOf(root);
                    if (left)
                        Consider(user, Foot(root, rig, left: true), Chirality.Left);
                    if (right)
                        Consider(user, Foot(root, rig, left: false), Chirality.Right);
                    break;
            }
        }
        slot = bestSlot;
        distance = best;
        side = bestSide;
        return nearest;

        void Consider(User user, Slot? candidate, Chirality candidateSide)
        {
            if (candidate == null || !ScriptReads.Visible(ctx, candidate))
                return;
            float away = (candidate.GlobalPosition - point).Length;
            if (float.IsNaN(away) || away >= best)
                return;
            best = away;
            bestSlot = candidate;
            bestSide = candidateSide;
            nearest = user;
        }
    }

    private static Slot? Alive(Slot? slot) => slot is { IsDestroyed: false } ? slot : null;
}
