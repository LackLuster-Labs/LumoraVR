// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// A text message on any websocket this script opened, with the socket it came on so the script can answer
// through it. Heard on the machine that opened the socket, in the order they came; binary messages are not
// text and are not heard.
[Node("Network/WebSockets/On WebSocket Message", Description = "A text message on any websocket this script opened, with the socket it came on so the script can answer through it.")]
public sealed class OnWebSocketMessage : EventNode<SocketMessage>
{
}
