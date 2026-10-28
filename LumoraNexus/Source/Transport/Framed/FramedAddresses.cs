// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Lumora.Nexus.Transport.Framed;

// The address a stream listener advertises for joiners outside this network: a public IPv4 address on one of
// this machine's cards (a server, or a host with a routed address), else, where allowed, its public IPv6 one. A
// host behind a NAT has neither and advertises nothing; LNL covers its own network, and the node covers the
// rest. -xlinka
internal static class FramedAddresses
{
    public static IPAddress? PublicIpv4()
    {
        try
        {
            foreach (var card in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (card.OperationalStatus != OperationalStatus.Up || card.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var unicast in card.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily == AddressFamily.InterNetwork && IsPublic(address))
                        return address;
                }
            }
        }
        catch
        {
            // No card list: nothing is advertised beyond what LNL already lists.
        }
        return null;
    }

    public static IPAddress? PublicIpv4OrIpv6()
    {
        var v4 = PublicIpv4();
        if (v4 != null)
            return v4;
        if (!NetworkOptions.AllowIpv6)
            return null;
        var v6 = HostAddresses.GlobalIpv6;
        return v6.Count > 0 ? v6[0] : null;
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return HostAddresses.IsGlobalIpv6(address);
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
            return false;
        var b = address.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 0 || b[0] >= 224
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127));
    }

    public static IPAddress Plain(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    public static string UriHost(IPAddress address)
    {
        address = Plain(address);
        return address.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + address + "]" : address.ToString();
    }

    public static Uri BuildUri(string scheme, string host, int port, string? sessionId)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
            host = UriHost(literal);
        return new Uri($"{scheme}://{host}:{port}/{sessionId ?? ""}");
    }
}
