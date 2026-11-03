// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Someone equipped the tool bound as its source, else the one the script sits on. Heard where the equip
// arrives, which for a Shared script is the host, a moment after it happened in the person's hand. -xlinka
[Node("Interaction/Tools/On Tool Equipped", Description = "Someone equipped the tool bound as its source, else the one the script sits on.")]
[SourcedEvent]
public sealed class OnToolEquipped : EventNode<ToolEquipped>
{
}
