// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Someone took the seat on the object bound as its source, else on the one the script sits on, as the host
// rules it: every machine hears the same sitter. The object needs a seat. -xlinka
[Node("Interaction/Seats/On Seat Taken", Description = "Someone took the seat on the object bound as its source, else on the one the script sits on, as the host rules it: every machine hears the same sitter.")]
[SourcedEvent]
public sealed class OnSeatTaken : EventNode<SeatTaken>
{
}
