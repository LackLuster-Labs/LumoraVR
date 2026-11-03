// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading.Tasks;
using Lumora.Core.Networking.Web;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Opens a websocket from the machine the script runs on and carries on through next at once. connected runs
// with the socket once it is open, to keep in a Stored Value and send through; failed when nothing opened (no
// connection, a timeout, a limit passed, an address that is not ws or wss); denied when this machine's user
// refused the host or never answered. It is asked and guarded like HTTP Request: the user at the machine
// allows the host, a local address is reached only when the URL names it, and one author's Shared scripts, or Local
// ones, hold at most four open on a machine. What comes in arrives through On WebSocket Message and the end
// through On WebSocket Closed. headers is one "Name: value" per line; protocols a comma list the server may
// pick one of. The socket closes when the script stops, and stays open through a live graph swap.
[Node("Network/WebSockets/Connect", Capability.Web, Description = "Opens a WebSocket connection and reports when it connects, fails or is denied by the user.",
    Keywords = "websocket, ws, socket, realtime, server connection")]
[Answers("connected", "failed", "denied")]
public sealed class ConnectWebSocket : AwaitNode<SocketOpened>
{
    public static void Start(ScriptContext ctx, AwaitTicket<SocketOpened> ticket, Uri? url, string? headers = null, string? protocols = null,
        float timeout = 30f)
    {
        var host = ctx.Host;
        bool local = ctx.OffHost;
        string requester = ctx.Principal.AuthorKey + (local ? "/local" : "/shared");
        var opening = ScriptWebSocket.ConnectAsync(url, headers, protocols, timeout, requester, ctx.Principal.DisplayName ?? "a script",
            ctx.World.Name,
            (socket, text) => ScriptHostEvents.Post(host, local, new SocketMessage(socket, text)),
            (socket, reason) => ScriptHostEvents.Post(host, local, new SocketClosed(socket, reason)),
            ticket.Cancelled);
        opening.ContinueWith(done => Answer(ticket, done), TaskScheduler.Default);
    }

    private static void Answer(AwaitTicket<SocketOpened> ticket, Task<ScriptWebSocketResult> done)
    {
        var result = done.IsCompletedSuccessfully ? done.Result : null;
        if (ticket.Cancelled.IsCancellationRequested)
        {
            result?.Socket?.Close();
            ticket.Abandon();
            return;
        }
        if (result == null)
        {
            ticket.Answer("failed", new SocketOpened(null, "The websocket could not be opened"));
            return;
        }
        var exit = result.Outcome switch
        {
            WebOutcome.Response => "connected",
            WebOutcome.Denied => "denied",
            _ => "failed",
        };
        // An answer nobody takes leaves nobody holding the socket.
        if (!ticket.Answer(exit, new SocketOpened(result.Socket, result.Error)))
            result.Socket?.Close();
    }
}
