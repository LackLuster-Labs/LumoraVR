// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Someone clicked the object with their beam: the object bound as its source, else the one the script sits
// on, or anything under it that the beam treats as a thing rather than a panel. -xlinka
[Node("Interaction/Pointer/On Beam Click", Description = "Someone clicked the object with their beam: the object bound as its source, else the one the script sits on, or anything under it that the beam treats as a thing rather than a panel.",
    Keywords = "click, laser, pointer, ray, point at")]
[SourcedEvent]
public sealed class OnBeamClick : EventNode<BeamClicked>
{
}
