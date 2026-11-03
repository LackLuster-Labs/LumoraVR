// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Input;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The same body node on the side asked for: LeftHand on Right is RightHand, RightHand on Left is LeftHand.
// A node down the middle has no side and comes back as it is, and so does any node when the side is None.
[Node("Avatars/Body Nodes/On Side", Description = "The same body node on the side asked for: LeftHand on Right is RightHand, RightHand on Left is LeftHand.",
    Keywords = "mirror body node, flip side, swap left right")]
public sealed class BodyNodeOnSide : ValueNode
{
    public static BodyNode Run(BodyNode node, Chirality side = Chirality.Left) => ScriptBodyReads.OnSide(node, side);
}
