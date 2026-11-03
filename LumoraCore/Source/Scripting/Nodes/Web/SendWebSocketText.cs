// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Threading.Tasks;
using Lumora.Core.Networking.Web;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Sends text through a websocket and carries on through next at once. sent runs once it went out, failed
// when it could not: the socket is closed, the text is longer than 64 Ki characters, or the send took more
// than 30 s. Sends through one socket go out in the order they were made.
[Node("Network/WebSockets/Send Text", Capability.Web, Description = "Sends text over a WebSocket and reports whether the send succeeded.",
    Keywords = "ws, socket, realtime")]
[Answers("sent", "failed")]
public sealed class SendWebSocketText : AwaitNode<SocketSent>
{
    public static void Start(AwaitTicket<SocketSent> ticket, ScriptWebSocket? socket, string? text)
    {
        if (socket == null)
        {
            ticket.Answer("failed", new SocketSent("No websocket"));
            return;
        }
        socket.SendAsync(text, ticket.Cancelled).ContinueWith(done =>
        {
            if (ticket.Cancelled.IsCancellationRequested)
            {
                ticket.Abandon();
                return;
            }
            var error = done.IsCompletedSuccessfully ? done.Result : "The message could not be sent";
            ticket.Answer(error == null ? "sent" : "failed", new SocketSent(error ?? string.Empty));
        }, TaskScheduler.Default);
    }
}
