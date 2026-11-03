// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Avatar;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// How a finger bone lies in its hand, as a hand pose source has it: hand space, origin at the wrist, +Z along
// the stretched fingers, +Y out of the back of the hand; identity is a segment lying flat along the hand.
// The source is a hand pose source component, or a user for the one their avatar publishes. Tracked is false,
// with identity and zero, for a node that is no finger bone, a hand the source does not track, or a bone it
// has nothing for; the position is zero from a source that keeps rotations only.
[Node("Avatars/Finger Pose", Capability.UsersRead, Description = "Reads the pose of a finger bone relative to the wrist from a hand pose source.",
    Keywords = "finger tracking, finger rotation, hand tracking, finger bone")]
public sealed class FingerPose : ReadNode
{
    [return: Port("rotation")]
    public static floatQ Run(ScriptContext ctx, Component? source, BodyNode node, out float3 position, out bool tracked)
    {
        position = default;
        tracked = false;
        return ScriptReads.Visible(ctx, source) && source is IHandPoseSource hand ? Read(hand, node, out position, out tracked) : floatQ.Identity;
    }

    [return: Port("rotation")]
    public static floatQ Run(ScriptContext ctx, User? user, BodyNode node, out float3 position, out bool tracked)
    {
        position = default;
        tracked = false;
        return HandPoseSourceOf.Find(ctx, user) is IHandPoseSource hand ? Read(hand, node, out position, out tracked) : floatQ.Identity;
    }

    private static floatQ Read(IHandPoseSource source, BodyNode node, out float3 position, out bool tracked)
    {
        position = default;
        tracked = false;
        if (!node.IsFinger() || !source.IsHandTracked(node.GetChirality()) || !source.TryGetFingerRotation(node, out var rotation))
            return floatQ.Identity;
        tracked = true;
        if (!source.TryGetFingerPosition(node, out position))
            position = default;
        return rotation;
    }
}
