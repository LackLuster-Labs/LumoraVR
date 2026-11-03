// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Networking.Web;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Closes a websocket, telling the server. On WebSocket Closed follows once the server answers, or after a
// few seconds when it does not; nothing that arrives after this is heard. No socket fails.
[Node("Network/WebSockets/Close", Capability.Web, Description = "Closes a websocket, telling the server.",
    Keywords = "ws, socket, disconnect")]
public sealed class CloseWebSocket : ActionNode
{
    public static void Run(ScriptContext ctx, ScriptWebSocket? socket)
    {
        if (socket == null)
            ctx.Fail("no websocket");
        else
            socket.Close();
    }
}
