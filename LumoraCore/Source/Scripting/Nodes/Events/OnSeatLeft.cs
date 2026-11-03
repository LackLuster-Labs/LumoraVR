// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whoever held the seat on the object bound as its source, else on the one the script sits on, is out of it,
// as the host rules it: they stood up, left the session or lost the seat to someone else. The object needs a
// seat. -xlinka
[Node("Interaction/Seats/On Seat Left", Description = "Whoever held the seat on the object bound as its source, else on the one the script sits on, is out of it, as the host rules it: they stood up, left the session or lost the seat to someone else.")]
[SourcedEvent]
public sealed class OnSeatLeft : EventNode<SeatLeft>
{
}
