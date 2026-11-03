// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Threading.Tasks;
using Lumora.Core.Localization;
using Lumora.Core.Networking.Web;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Asks this machine's user ahead of any request whether scripts may reach a host, named as a host or by any
// address on it, and carries on through next at once. allowed or denied runs with the answer, at once for a
// host already decided; ignored when the prompt was closed or left for two minutes, which is not kept, so a
// later ask asks again. A Shared script asks the host's user, a Local one each person on their own machine.
// The prompt shows the engine's own words, never text from a script, and says when the host is local. An
// address no socket can open, or text that names no host, is denied at once without asking.
[Node("Network/Access/Request Host Access", Capability.Web, Description = "Asks the local user to allow scripts to contact a web host, reporting approval, refusal or dismissal.",
    Keywords = "permission, allow site, web permission, ask user")]
[Answers("allowed", "denied", "ignored")]
public sealed class AskWebHost : AwaitNode<HostAccessAnswer>
{
    public static void Start(ScriptContext ctx, AwaitTicket<HostAccessAnswer> ticket, string? host)
    {
        var key = ScriptWebHosts.KeyOf(host);
        var client = ScriptWebClient.Shared;
        if (key == null || client.Unreachable(key) != null)
        {
            ticket.Answer("denied", new HostAccessAnswer(key ?? string.Empty));
            return;
        }
        string reason = "WebAccess.Reason.Ahead".AsLocale("A script asks ahead to reach this host.").Resolve();
        var asking = client.Access.AskAsync(key, ctx.Principal.DisplayName ?? "a script", ctx.World.Name, reason, ticket.Cancelled);
        asking.ContinueWith(done =>
        {
            if (!done.IsCompletedSuccessfully || ticket.Cancelled.IsCancellationRequested)
            {
                ticket.Abandon();
                return;
            }
            ticket.Answer(done.Result switch
            {
                WebAccessAnswer.Allowed => "allowed",
                WebAccessAnswer.Denied => "denied",
                _ => "ignored",
            }, new HostAccessAnswer(key));
        }, TaskScheduler.Default);
    }
}
