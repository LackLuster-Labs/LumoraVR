// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Networking.Web;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whether this machine's user already lets scripts reach a host, named as a host or by any address on it.
// decided is false while nobody has said either way; an address no socket can open reads decided and not
// allowed. A Shared script reads the host's decisions, a Local one this
// machine's. A decision covers the whole host, whatever the port or scheme.
[Node("Network/Access/Is Host Allowed", Capability.Web, Description = "Checks whether the local user has allowed scripts to contact a web host and whether a decision exists.",
    Keywords = "permission, allow site, web permission, site allowed")]
public sealed class WebHostAllowed : ReadNode
{
    [return: Port("allowed")]
    public static bool Run(string? host, out bool decided) => Allowed(ScriptWebHosts.KeyOf(host), out decided);

    [return: Port("allowed")]
    public static bool Run(Uri? url, out bool decided) => Allowed(ScriptWebHosts.KeyOf(url), out decided);

    private static bool Allowed(string? key, out bool decided)
    {
        decided = false;
        if (key == null)
            return false;
        var client = ScriptWebClient.Shared;
        if (client.Unreachable(key) != null)
        {
            decided = true;
            return false;
        }
        decided = client.Access.TryGetDecision(key, out bool allowed);
        return decided && allowed;
    }
}
