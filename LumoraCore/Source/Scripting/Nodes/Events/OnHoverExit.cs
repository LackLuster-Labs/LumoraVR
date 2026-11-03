// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Someone's beam went off the object, as On Hover Enter means it. -xlinka
[Node("Interaction/Pointer/On Hover Exit", Description = "Someone's beam went off the object, as On Hover Enter means it.",
    Keywords = "laser, pointer, ray, hover, mouse out")]
[SourcedEvent]
public sealed class OnHoverExit : EventNode<HoverEnded>
{
}
