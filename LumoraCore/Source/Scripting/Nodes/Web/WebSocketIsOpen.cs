// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Networking.Web;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whether a websocket is open, and once it is not, why it closed. A socket being closed reads as not open.
[Node("Network/WebSockets/Is Open", Capability.Web, Description = "Whether a websocket is open, and once it is not, why it closed.",
    Keywords = "ws, socket, connected")]
public sealed class WebSocketIsOpen : ReadNode
{
    [return: Port("open")]
    public static bool Run(ScriptWebSocket? socket, out string reason)
    {
        reason = socket?.ClosedBecause ?? string.Empty;
        return socket?.IsOpen == true;
    }
}
