// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Net;

namespace Lumora.Nexus.Transport.PortMapping;

// What a router said to one forward request. Refused means a router answered and said no; neither flag means
// nothing answered in that protocol at all.
internal sealed class PortMapResult
{
    public bool Mapped;
    public bool Refused;
    public IPAddress External = IPAddress.None;
    public int ExternalPort;
    public int LifetimeSeconds;
    public string Detail = "";

    public static PortMapResult No(string detail) => new() { Refused = true, Detail = detail };
}
