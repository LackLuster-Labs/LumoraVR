// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Input;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// What a body node is: its side, None for one down the middle; whether it is an eye; and for a finger bone
// which finger, which segment and its place among the hand's 24 finger bones, thumb metacarpal first. For
// anything that is not a finger bone, isFinger is false, finger and segment read Thumb and Metacarpal and
// index is -1, rather than numbers that name no finger.
[Node("Avatars/Body Nodes/Split Body Node", Description = "What a body node is: its side, None for one down the middle; whether it is an eye; and for a finger bone which finger, which segment and its place among the hand's 24 finger bones, thumb metacarpal first.")]
public sealed class SplitBodyNode : ValueNode
{
    [return: Port("side")]
    public static Chirality Run(BodyNode node, out bool isFinger, out FingerType finger, out FingerSegmentType segment,
        out int index, out bool isEye)
    {
        index = node.GetFingerSegmentIndex();
        isFinger = index >= 0;
        finger = isFinger ? node.GetFingerType() : FingerType.Thumb;
        segment = isFinger ? node.GetFingerSegmentType() : FingerSegmentType.Metacarpal;
        isEye = node is BodyNode.LeftEye or BodyNode.RightEye;
        return node.GetChirality();
    }
}
