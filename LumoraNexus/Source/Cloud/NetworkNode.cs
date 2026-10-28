// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Nexus.Cloud;

// One NAT/relay node as the node list publishes it.
public sealed class NetworkNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Region { get; set; } = "";
    public string Address { get; set; } = "";
    // The node's IPv6 address, empty where it has none. Same ports as Address.
    public string Address6 { get; set; } = "";
    public int Port { get; set; }
    // The node's TCP gate for joiners without UDP, on the same addresses; 0 where it has none.
    public int TcpPort { get; set; }
    // "nat" or "relay".
    public string Kind { get; set; } = "";
    public int ProtocolVersion { get; set; }
    public int UserCount { get; set; }
    // 0 while the node drains: pick another.
    public int Capacity { get; set; }
    // Seconds the entry stays good for.
    public int Ttl { get; set; }
    public DateTime LastSeen { get; set; }

    public bool HasCapacity => Capacity > 0 && UserCount < Capacity;

    public bool IsUsable => !string.IsNullOrWhiteSpace(Address) && Port is > 0 and <= 65535;

    public override string ToString() => $"{Name} ({Id}) {Address}:{Port} {Region} {UserCount}/{Capacity}";
}
