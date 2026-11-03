// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading.Tasks;
using Lumora.Core.Networking.Web;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Sends a web request from the machine the script runs on and carries on through next at once. Its answer
// runs response for whatever status the server sent, 404 and 500 included; failed when nothing came back
// (no connection, a timeout, a limit passed, an address that is not http or https); denied when this
// machine's user refused the host or never answered. A Shared script runs on the host, so the host's user is
// asked; a Local one asks each person on their own machine. A local address is reached only when the URL
// names it, or a local name for it, itself; a public name that leads to one is refused.
// headers is one "Name: value" per line, for the request and for what comes back. The body goes out as UTF-8
// with POST, PUT, PATCH and a DELETE that has one, as JSON unless a Content-Type header says otherwise.
[Node("Network/HTTP/HTTP Request", Capability.Web, Description = "Sends a web request from the machine the script runs on and carries on through next at once.",
    Keywords = "http get, http post, fetch, download, api, rest, web, url")]
[Answers("response", "failed", "denied")]
public sealed class HttpRequest : AwaitNode<HttpAnswer>
{
    public static void Start(ScriptContext ctx, AwaitTicket<HttpAnswer> ticket, string? url, WebMethod method = WebMethod.GET,
        string? headers = null, string? body = null, float timeout = 30f)
    {
        var text = url?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var address) || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
        {
            ticket.Answer("failed", new HttpAnswer(0, string.Empty, string.Empty, text, "not an http or https address"));
            return;
        }

        var request = new ScriptWebRequest(address, method, headers, body, timeout);
        string requester = ctx.Principal.AuthorKey + (ctx.OffHost ? "/local" : "/shared");
        var sending = ScriptWebClient.Shared.SendAsync(request, requester, ctx.Principal.DisplayName ?? "a script", ctx.World.Name,
            ticket.Cancelled);
        sending.ContinueWith(done => Answer(ticket, done, text), TaskScheduler.Default);
    }

    private static void Answer(AwaitTicket<HttpAnswer> ticket, Task<ScriptWebResult> done, string url)
    {
        if (done.IsCanceled || ticket.Cancelled.IsCancellationRequested)
        {
            ticket.Abandon();
            return;
        }
        if (done.IsFaulted)
        {
            ticket.Answer("failed", new HttpAnswer(0, string.Empty, string.Empty, url, "the request could not be made"));
            return;
        }
        var result = done.Result;
        var answer = new HttpAnswer(result.Status, result.Body, result.Headers, result.FinalUrl, result.Error);
        ticket.Answer(result.Outcome switch
        {
            WebOutcome.Response => "response",
            WebOutcome.Denied => "denied",
            _ => "failed",
        }, answer);
    }
}
