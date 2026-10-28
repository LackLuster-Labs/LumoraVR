// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Nexus.Transport.PortMapping;

// What a router forward carries: LNL and QUIC sessions are UDP, TCP sessions TCP.
public enum PortProtocol
{
    Udp,
    Tcp,
}
