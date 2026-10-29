// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

public enum HandPoseShape
{
    Idle = 0,
    Fist = 1,
    Point = 2,
    Open = 3,
}

// The named hand shapes, one StaticHandPose each for the whole process. Every one is the model's open
// hand curled in through the same per-joint curl the modifier applies, so a preset and a modifier
// offset compose in the same units. Curl is radians per joint about the segment's flex axis and
// stacks down the finger: three joints at 1.2 rad fold a finger back onto the palm. -xlinka
public static class HandPosePresets
{
    public static readonly StaticHandPose Open = Build(0f, 0f, 0f, 0f, 0f, 0f);
    public static readonly StaticHandPose Idle = Build(0.15f, 0.18f, 0.22f, 0.26f, 0.30f, 0f);
    public static readonly StaticHandPose Fist = Build(0.55f, 1.20f, 1.22f, 1.24f, 1.26f, 0.55f);
    public static readonly StaticHandPose Point = Build(0.40f, 0.04f, 1.22f, 1.24f, 1.26f, 0.45f);

    public static StaticHandPose Get(HandPoseShape shape) => shape switch
    {
        HandPoseShape.Open => Open,
        HandPoseShape.Fist => Fist,
        HandPoseShape.Point => Point,
        _ => Idle,
    };

    // One preset component per shape per slot, made on first ask and found afterwards.
    public static HandPosePreset GetShared(Slot slot, HandPoseShape shape)
    {
        var preset = slot.GetComponent<HandPosePreset>(p => p.Shape.Value == shape);
        if (preset != null)
            return preset;
        preset = slot.AttachComponent<HandPosePreset>();
        preset.Shape.Value = shape;
        return preset;
    }

    private static StaticHandPose Build(float thumb, float index, float middle, float ring, float pinky, float thumbSplay)
    {
        var pose = new StaticHandPose();
        foreach (var side in HandPoseHelper.Sides)
            Shape(pose, side, thumb, index, middle, ring, pinky, thumbSplay);
        return pose;
    }

    // Writes one side of a pose as the open hand curled per finger, in the presets' units. Called every frame
    // it animates a hand: a gesture keys its curls and a hand pose source hands them on, so fingers can relax,
    // spread for a wave and close into a fist instead of jumping between the four presets.
    public static void Shape(StaticHandPose pose, Chirality side, float thumb, float index, float middle, float ring, float pinky, float thumbSplay)
    {
        var positions = HandPoseHelper.NewPositions();
        var rotations = HandPoseHelper.NewRotations();
        HandPoseModel.BuildOpenPose(side, positions, rotations);
        HandPoseHelper.ToHierarchy(positions, rotations);
        // A splay turns about the parent's up, which mirrors as itself between the hands, so the
        // left thumb needs the opposite sign to land as the right thumb's mirror image. Curl turns
        // about the parent's right, which mirrors with a sign flip of its own and needs nothing.
        float splay = side == Chirality.Left ? -thumbSplay : thumbSplay;
        HandPoseHelper.ModifyPose(rotations, thumb, index, middle, ring, pinky, splay, 0f, 0f, 0f, 0f);
        HandPoseHelper.ToFlat(positions, rotations);
        pose.SetPose(side, positions, rotations);
    }
}
