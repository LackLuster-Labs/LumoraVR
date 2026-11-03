// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whoever has the scripted tool bound as its source, else the one on the object the script sits on,
// equipped pressed its primary or secondary button. The press happens on their machine and reaches a
// Shared script on the host through their relay. Tool State says whether it is still held down. -xlinka
[Node("Interaction/Tools/On Tool Pressed", Description = "Whoever has the scripted tool bound as its source, else the one on the object the script sits on, equipped pressed its primary or secondary button.")]
[SourcedEvent]
public sealed class OnToolPressed : EventNode<ToolPressed>
{
}
