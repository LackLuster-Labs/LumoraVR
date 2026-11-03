// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Input;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The finger bone for a finger, a segment and a side. The thumb has no intermediate bone and a finger bone
// needs a side, so a thumb's intermediate or a side of None gives NONE rather than a neighbouring bone.
[Node("Avatars/Body Nodes/Compose Finger", Description = "The finger bone for a finger, a segment and a side.")]
public sealed class ComposeFinger : ValueNode
{
    [return: Port("node")]
    public static BodyNode Run(FingerType finger, FingerSegmentType segment, Chirality side = Chirality.Left)
    {
        if (side == Chirality.None || !finger.IsValidFinger(segment))
            return BodyNode.NONE;
        return BodyNodeExtensions.ComposeFinger(finger, segment, side);
    }
}
