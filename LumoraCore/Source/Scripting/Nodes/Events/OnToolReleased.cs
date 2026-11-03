// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whoever has the scripted tool bound as its source, else the one on the object the script sits on,
// equipped let go of its primary or secondary button. -xlinka
[Node("Interaction/Tools/On Tool Released", Description = "Whoever has the scripted tool bound as its source, else the one on the object the script sits on, equipped let go of its primary or secondary button.")]
[SourcedEvent]
public sealed class OnToolReleased : EventNode<ToolReleased>
{
}
