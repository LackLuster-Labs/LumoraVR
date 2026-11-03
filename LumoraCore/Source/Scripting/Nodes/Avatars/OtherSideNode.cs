// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Input;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The mirrored body node, left for right and right for left. A node down the middle comes back as it is.
[Node("Avatars/Body Nodes/Other Side", Description = "The mirrored body node, left for right and right for left.",
    Keywords = "mirror body node, flip side, swap left right")]
public sealed class OtherSideNode : ValueNode
{
    [return: Port("mirrored")]
    public static BodyNode Run(BodyNode node) => ScriptBodyReads.Mirror(node);
}
