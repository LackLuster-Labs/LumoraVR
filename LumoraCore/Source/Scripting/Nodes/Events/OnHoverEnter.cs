// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Someone's beam came onto the object bound as its source, else the one the script sits on, or onto
// anything under it. -xlinka
[Node("Interaction/Pointer/On Hover Enter", Description = "Someone's beam came onto the object bound as its source, else the one the script sits on, or onto anything under it.",
    Keywords = "laser, pointer, ray, hover, mouse over, point at")]
[SourcedEvent]
public sealed class OnHoverEnter : EventNode<HoverStarted>
{
}
