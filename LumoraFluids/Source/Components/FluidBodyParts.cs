// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Input;

namespace Lumora.Fluids;

// The fixed list of contact parts behind FluidPerson.Touching's bits and the GPU's part indices, and the four
// limbs behind FluidPerson.Limbs. Every bone of a rig folds into one of the 16 parts: fingers into their hand, the
// spine and shoulders into the chest, the jaw and eyes into the head. The region a bone belongs to is finer than
// its part (a finger is part Hand, region Fingers), because AvatarFluidContact switches regions, not parts. -xlinka
public static class FluidBodyParts
{
    public const int Count = 16;

    public const int LeftFoot = 0;
    public const int RightFoot = 1;
    public const int LeftToes = 2;
    public const int RightToes = 3;
    public const int LeftLowerLeg = 4;
    public const int RightLowerLeg = 5;
    public const int LeftUpperLeg = 6;
    public const int RightUpperLeg = 7;
    public const int LeftHand = 8;
    public const int RightHand = 9;
    public const int LeftLowerArm = 10;
    public const int RightLowerArm = 11;
    public const int LeftUpperArm = 12;
    public const int RightUpperArm = 13;
    public const int Chest = 14;
    public const int Head = 15;

    public const int LimbLeftFoot = 0;
    public const int LimbRightFoot = 1;
    public const int LimbLeftHand = 2;
    public const int LimbRightHand = 3;

    public static readonly BodyNode[] Nodes =
    {
        BodyNode.LeftFoot, BodyNode.RightFoot, BodyNode.LeftToes, BodyNode.RightToes,
        BodyNode.LeftLowerLeg, BodyNode.RightLowerLeg, BodyNode.LeftUpperLeg, BodyNode.RightUpperLeg,
        BodyNode.LeftHand, BodyNode.RightHand, BodyNode.LeftLowerArm, BodyNode.RightLowerArm,
        BodyNode.LeftUpperArm, BodyNode.RightUpperArm, BodyNode.Chest, BodyNode.Head,
    };

    public static readonly BodyNode[] Limbs = { BodyNode.LeftFoot, BodyNode.RightFoot, BodyNode.LeftHand, BodyNode.RightHand };

    public static int IndexOf(BodyNode node) => System.Array.IndexOf(Nodes, node);

    public static int Bit(int part) => part >= 0 && part < Count ? 1 << part : 0;

    public static bool IsLeft(int part) => part < Chest && (part & 1) == 0;

    // The limb a part's stickiness counts toward: toes and lower legs pull on their foot, lower arms on their hand.
    public static int LimbOf(int part) => part switch
    {
        LeftFoot or LeftToes or LeftLowerLeg => LimbLeftFoot,
        RightFoot or RightToes or RightLowerLeg => LimbRightFoot,
        LeftHand or LeftLowerArm => LimbLeftHand,
        RightHand or RightLowerArm => LimbRightHand,
        _ => -1,
    };

    public static int PartOfLimb(int limb) => limb switch
    {
        LimbLeftFoot => LeftFoot,
        LimbRightFoot => RightFoot,
        LimbLeftHand => LeftHand,
        LimbRightHand => RightHand,
        _ => -1,
    };

    // A part's own region. Fingers never come out of this: they are a region of the hand part, found per bone.
    public static FluidBodyRegions RegionOf(int part) => part switch
    {
        LeftFoot or RightFoot or LeftToes or RightToes => FluidBodyRegions.Feet,
        LeftLowerLeg or RightLowerLeg => FluidBodyRegions.LowerLegs,
        LeftHand or RightHand or LeftLowerArm or RightLowerArm => FluidBodyRegions.Hands,
        Head => FluidBodyRegions.Head,
        _ => FluidBodyRegions.Torso,
    };

    // The part and region a rig node folds into, or -1 for nodes that carry no body (controllers, the view).
    public static int PartOf(BodyNode node, out FluidBodyRegions region)
    {
        int n = (int)node;
        if (n >= (int)BodyNode.LEFT_FINGER_START && n <= (int)BodyNode.LEFT_FINGER_END)
        {
            region = FluidBodyRegions.Fingers;
            return LeftHand;
        }
        if (n >= (int)BodyNode.RIGHT_FINGER_START && n <= (int)BodyNode.RIGHT_FINGER_END)
        {
            region = FluidBodyRegions.Fingers;
            return RightHand;
        }
        int part = node switch
        {
            BodyNode.LeftPalm => LeftHand,
            BodyNode.RightPalm => RightHand,
            BodyNode.Hips or BodyNode.Spine or BodyNode.Chest or BodyNode.UpperChest or BodyNode.Neck
                or BodyNode.LeftShoulder or BodyNode.RightShoulder => Chest,
            BodyNode.Head or BodyNode.Jaw or BodyNode.LeftEye or BodyNode.RightEye => Head,
            _ => IndexOf(node),
        };
        region = part >= 0 ? RegionOf(part) : FluidBodyRegions.None;
        return part;
    }

    // Every rig bone and its node, built once per equip so walking a mesh's bones stays a lookup.
    public static Dictionary<Slot, BodyNode> RigMap(HumanoidRig? rig)
    {
        var map = new Dictionary<Slot, BodyNode>();
        if (rig == null || rig.IsDestroyed)
            return map;
        foreach (var entry in rig.Bones)
        {
            var bone = entry.Value.Target;
            if (bone != null && !bone.IsDestroyed)
                map[bone] = entry.Key.Value;
        }
        return map;
    }

    // The part a bone slot belongs to: its own rig node, else the nearest rig bone above it (a twist bone, a shoe
    // bone parented under the foot). -1 when nothing above it, up to the avatar root, is in the rig.
    public static int PartOfBone(Dictionary<Slot, BodyNode> rigMap, Slot? bone, Slot? stopAt, out FluidBodyRegions region)
    {
        region = FluidBodyRegions.None;
        int depth = 0;
        for (var walk = bone; walk != null && !walk.IsDestroyed && depth < 256; walk = walk.Parent, depth++)
        {
            if (rigMap.TryGetValue(walk, out var node))
                return PartOf(node, out region);
            if (walk == stopAt)
                break;
        }
        return -1;
    }

    // The rig bone that carries a part. Toes ride the foot and the chest the nearest spine bone when the rig has no
    // bone of their own; anything else missing is missing.
    public static Slot? BoneOf(HumanoidRig? rig, int part)
    {
        if (rig == null || rig.IsDestroyed || part < 0 || part >= Count)
            return null;
        var bone = Alive(rig.TryGetBone(Nodes[part]));
        if (bone != null)
            return bone;
        return part switch
        {
            LeftToes => Alive(rig.TryGetBone(BodyNode.LeftFoot)),
            RightToes => Alive(rig.TryGetBone(BodyNode.RightFoot)),
            Chest => Alive(rig.TryGetBone(BodyNode.UpperChest)) ?? Alive(rig.TryGetBone(BodyNode.Spine)) ?? Alive(rig.TryGetBone(BodyNode.Hips)),
            _ => null,
        };
    }

    private static Slot? Alive(Slot? slot) => slot != null && !slot.IsDestroyed ? slot : null;
}
