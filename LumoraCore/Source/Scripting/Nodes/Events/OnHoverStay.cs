// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Every frame, once for each person whose beam is still on the object bound as its source, else the one
// the script sits on, or on anything under it: from the frame after On Hover Enter until On Hover Exit.
// -xlinka
[Node("Interaction/Pointer/On Hover Stay", Description = "Every frame, once for each person whose beam is still on the object bound as its source, else the one the script sits on, or on anything under it: from the frame after On Hover Enter until On Hover Exit.",
    Keywords = "laser, pointer, ray, hover, mouse over, point at")]
[SourcedEvent]
public sealed class OnHoverStay : EventNode<HoverStaying>
{
}
