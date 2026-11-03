// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// A message another script of the same author sent to this one's object. -xlinka
[Node("Network/Messages/On Message", Description = "A message another script of the same author sent to this one's object.")]
public sealed class OnMessage : EventNode<MessageReceived>
{
}
