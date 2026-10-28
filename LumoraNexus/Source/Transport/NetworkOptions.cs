// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Nexus.Transport;

// How this machine goes through NAT nodes and over IPv6. The engine's settings write these; the transport reads
// them as it connects, and a hosted session's node link as it refreshes, so a change reaches a running session.
// -xlinka
public static class NetworkOptions
{
    private static volatile string _preferredNode = "";
    private static volatile bool _allowPunch = true;
    private static volatile bool _allowRelay = true;
    private static volatile bool _allowIpv6 = true;
    private static volatile bool _allowTcp = true;
    private static volatile bool _preferQuic;

    // A node id from the official node list. Empty, or a node not listed right now, takes the nearest by round
    // trip, and the rest follow by round trip as fallbacks.
    public static string PreferredNode
    {
        get => _preferredNode;
        set
        {
            var picked = value?.Trim() ?? "";
            if (_preferredNode == picked)
                return;
            _preferredNode = picked;
            NodeChoiceChanged?.Invoke();
        }
    }

    // Raised when a different node was picked, so a hosted session moves at once.
    public static event Action? NodeChoiceChanged;

    // Joining through a node: a punch is always tried before a relay. Punch off starts at the relay; relay off
    // stops after the punch. With both off a node address is skipped altogether.
    public static bool AllowPunch
    {
        get => _allowPunch;
        set => _allowPunch = value;
    }

    public static bool AllowRelay
    {
        get => _allowRelay;
        set => _allowRelay = value;
    }

    public static bool UsesNodes => _allowPunch || _allowRelay;

    // IPv6 addresses advertised for hosted worlds, dialled when joining, and used to reach a node that has one.
    public static bool AllowIpv6
    {
        get => _allowIpv6;
        set => _allowIpv6 = value;
    }

    // Both ends of an IPv6 path: allowed here, and this machine has a public IPv6 address to use.
    public static bool UsesIpv6 => _allowIpv6 && HostAddresses.HasGlobalIpv6;

    // TCP once every UDP way in has failed: to the host itself, or through its node's TCP gate, which is a relay
    // and so also needs AllowRelay.
    public static bool AllowTcp
    {
        get => _allowTcp;
        set => _allowTcp = value;
    }

    // QUIC to a host's address ahead of LNL to it, rather than after every UDP way in has failed.
    public static bool PreferQuic
    {
        get => _preferQuic;
        set => _preferQuic = value;
    }
}
