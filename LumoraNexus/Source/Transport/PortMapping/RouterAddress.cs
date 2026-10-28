// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Lumora.Nexus.Transport.PortMapping;

// The router in front of this machine: the IPv4 gateway of the card that traffic to the internet leaves by,
// and this machine's address on that card, which is what a forward points at. -xlinka
internal static class RouterAddress
{
    public static bool TryFind(out IPAddress gateway, out IPAddress local)
    {
        gateway = IPAddress.None;
        local = IPAddress.None;
        try
        {
            // Connecting a UDP socket sends nothing; it only asks the system which card and address it would use.
            using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                probe.Connect(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53));
                local = ((IPEndPoint)probe.LocalEndPoint!).Address;
            }
            foreach (var card in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (card.OperationalStatus != OperationalStatus.Up)
                    continue;
                var properties = card.GetIPProperties();
                bool ours = false;
                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.Equals(local))
                        ours = true;
                }
                if (!ours)
                    continue;
                foreach (var route in properties.GatewayAddresses)
                {
                    if (route.Address.AddressFamily == AddressFamily.InterNetwork && !route.Address.Equals(IPAddress.Any))
                    {
                        gateway = route.Address;
                        return true;
                    }
                }
            }
        }
        catch
        {
        }
        return false;
    }

    // An address the internet can reach: not private, not carrier NAT, not link-local, loopback or unset.
    public static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = address.GetAddressBytes();
        return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                 || (b[0] == 192 && b[1] == 168)
                 || (b[0] == 169 && b[1] == 254)
                 || (b[0] == 100 && b[1] >= 64 && b[1] <= 127));
    }
}
