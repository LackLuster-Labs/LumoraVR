// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The drop surface bound as its source, else the one on the object the script sits on, took something
// someone let go of above it and set it down. The drop happens on the machine of whoever let go and
// reaches a Shared script on the host through their relay. -xlinka
[Node("UI/Drag and Drop/On Drop Received", Description = "The drop surface bound as its source, else the one on the object the script sits on, took something someone let go of above it and set it down.")]
[SourcedEvent]
public sealed class OnDropReceived : EventNode<DropReceived>
{
}
