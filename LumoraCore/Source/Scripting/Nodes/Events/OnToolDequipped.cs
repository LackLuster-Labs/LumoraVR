// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whoever had the tool bound as its source, else the one the script sits on, put it away or swapped it
// for another. -xlinka
[Node("Interaction/Tools/On Tool Dequipped", Description = "Whoever had the tool bound as its source, else the one the script sits on, put it away or swapped it for another.")]
[SourcedEvent]
public sealed class OnToolDequipped : EventNode<ToolDequipped>
{
}
