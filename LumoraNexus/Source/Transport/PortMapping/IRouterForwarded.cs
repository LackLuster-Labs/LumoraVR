// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Nexus.Transport.PortMapping;

// A listener that can ask the router to forward its own port, and the scheme its forwarded address is dialled by.
public interface IRouterForwarded
{
    string Scheme { get; }

    PortMapper? PortMapper { get; }

    // Raised off the main thread when the forward starts, moves or ends.
    event Action? PortMappingChanged;

    void StartPortMapping();

    // Takes the forward back before returning, up to two seconds.
    void StopPortMapping();
}
