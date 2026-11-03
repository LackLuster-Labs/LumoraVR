// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Input;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The body node a node's pose is kept relative to: a finger bone or a palm to its hand, an eye to the head,
// every other node to the root. NONE stays NONE.
[Node("Avatars/Body Nodes/Relative Node", Description = "The body node a node's pose is kept relative to: a finger bone or a palm to its hand, an eye to the head, every other node to the root.")]
public sealed class RelativeBodyNode : ValueNode
{
    [return: Port("relativeTo")]
    public static BodyNode Run(BodyNode node) => node switch
    {
        BodyNode.NONE => BodyNode.NONE,
        BodyNode.LeftEye or BodyNode.RightEye => BodyNode.Head,
        BodyNode.LeftPalm => BodyNode.LeftHand,
        BodyNode.RightPalm => BodyNode.RightHand,
        _ when node.IsFinger() => node.GetChirality() == Chirality.Right ? BodyNode.RightHand : BodyNode.LeftHand,
        _ => BodyNode.Root,
    };
}
