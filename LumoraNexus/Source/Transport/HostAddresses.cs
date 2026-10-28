// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Lumora.Nexus.Transport;

// This machine's public IPv6 addresses: global unicast (2000::/3), not a tunnel's, and the stable address of an
// interface rather than a temporary privacy one, so a listing stays good for as long as the session runs. Read
// again at most every half minute. -xlinka
public static class HostAddresses
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private static readonly object Gate = new();
    private static readonly Stopwatch Age = new();
    private static IReadOnlyList<IPAddress> _ipv6 = Array.Empty<IPAddress>();

    public static bool HasGlobalIpv6 => GlobalIpv6.Count > 0;

    public static IReadOnlyList<IPAddress> GlobalIpv6
    {
        get
        {
            lock (Gate)
            {
                if (!Age.IsRunning || Age.Elapsed > Lifetime)
                {
                    _ipv6 = Read();
                    Age.Restart();
                }
                return _ipv6;
            }
        }
    }

    public static bool IsGlobalIpv6(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.IsIPv4MappedToIPv6)
            return false;
        var bytes = address.GetAddressBytes();
        if ((bytes[0] & 0xE0) != 0x20)
            return false;
        // Teredo 2001:0::/32 and 6to4 2002::/16 tunnel through IPv4 and are not worth listing.
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
            return false;
        return !(bytes[0] == 0x20 && bytes[1] == 0x02);
    }

    private static IReadOnlyList<IPAddress> Read()
    {
        var stable = new List<IPAddress>();
        var temporary = new List<IPAddress>();
        try
        {
            foreach (var card in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (card.OperationalStatus != OperationalStatus.Up || card.NetworkInterfaceType == NetworkInterfaceType.Loopback
                    || card.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;
                foreach (var unicast in card.GetIPProperties().UnicastAddresses)
                {
                    if (!IsGlobalIpv6(unicast.Address))
                        continue;
                    bool isTemporary = false;
                    try
                    {
                        isTemporary = unicast.SuffixOrigin == SuffixOrigin.Random;
                    }
                    catch (PlatformNotSupportedException)
                    {
                    }
                    (isTemporary ? temporary : stable).Add(unicast.Address);
                }
            }
        }
        catch
        {
            // No card list means no IPv6 is offered; IPv4 carries on as before.
        }
        return stable.Count > 0 ? stable : temporary;
    }
}
