// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Lumora.Core.Networking.Session;

// The order a joiner dials a session's addresses in.
//
// A host advertises every address it listens on: one direct address per network card, its public IPv6 address,
// the address its router forwards, its NAT node (over IPv4, and IPv6 where the node has it), then Steam. A direct
// address on a private range only answers from inside the host's own network, and each one that does not
// answer costs a full connect timeout, so dialling them in advertised order left a remote joiner waiting out
// every dead LAN card before Steam was ever tried. The order, ties keeping the host's:
//   0  same network (cheap direct UDP), and this machine
//   1  public direct addresses: a forwarded port, or IPv6, which has no NAT in the way
//   2  the NAT node over IPv6, where a punch has only firewalls to get through
//   3  the NAT node over IPv4: punch, then relay through the same node
//   3.3 QUIC straight to the host (before LNL to the same address when preferred)
//   3.5 TCP straight to the host, 3.7 TCP through the node's gate: only once no UDP way in has worked
//   4  Steam, and transports this order does not know
//   5  private addresses from some other network
// IPv6 addresses are only dialled with IPv6 allowed and a public IPv6 address of our own, and QUIC and TCP only
// where their transport is here and the settings allow it.
//
// Before dialling, every node named among the addresses gets the node's own UDP ping. A node that answers it
// is dialled as usual. One that stays silent is behind a network that drops UDP: its punch and relay are passed
// over, every other UDP address gets a second instead of five, and TCP through the node comes up within a few
// seconds instead of after twenty. A node address with a TCP gate is also dialled over TCP, so a single address
// (an invite, a link) still carries the TCP way in. -xlinka
public static class SessionAddressOrder
{
    private const string NatScheme = Lumora.Nexus.Transport.LNL.LNLNetworkManager.NAT_SCHEME;
    private const string DirectScheme = Lumora.Nexus.Transport.LNL.LNLNetworkManager.SCHEME;
    private const string QuicScheme = Lumora.Nexus.Transport.LNQ.LNQSupport.Scheme;
    private const string TcpScheme = Lumora.Nexus.Transport.LNT.LNTNetworkManager.SCHEME;
    private const string TcpNodeScheme = Lumora.Nexus.Transport.LNT.LNTNetworkManager.NAT_SCHEME;
    private const string SteamScheme = "steam";

    // The network settings have the last word: a node address goes when neither punching nor relaying is
    // allowed, Steam goes first, last or not at all as picked, and IPv6 goes when it is off or not here.
    public static List<Uri> Order(IEnumerable<Uri> addresses)
    {
        var given = WithNodeTcp(addresses.Where(a => a != null).ToList());
        CheckNodeUdp(given);
        var networks = LocalNetworks();
        return given
            .Where(a => a != null && Allowed(a))
            .Distinct()
            .Select((address, index) => (address, index, rank: Rank(address, networks)))
            .OrderBy(t => t.rank)
            .ThenBy(t => t.index)
            .Select(t => t.address)
            .ToList();
    }

    // How long one address gets before the next is tried. A host on the same network answers within
    // milliseconds, so a private address still silent after a few seconds belongs to some other network
    // (home routers share the same ranges, so it can even look like ours). A public address answers in one
    // round trip, LNL answering from its own thread whatever the host's frame is doing, so five seconds of
    // silence is a forward or a firewall that is not open. Steam gets its whole first-connect window: finding
    // a relay route can take most of it. A NAT node address punches for five seconds, then sets up a relay
    // through the node, which takes a few more; a node that does not know the session says so at once.
    public static int ConnectTimeoutMs(Uri address)
    {
        if (Is(address, NatScheme))
            return 20000;
        // A node gone silent over UDP says UDP is blocked here; another UDP address that answers at all does so
        // within a round trip.
        if ((Is(address, DirectScheme) || Is(address, QuicScheme)) && Lumora.Nexus.Transport.NodeUdpCheck.UdpLooksBlocked)
            return BlockedUdpTimeoutMs;
        // QUIC and direct TCP answer in a round trip or not at all; through the gate the node rings the host
        // and waits up to ten seconds for it to call back.
        if (Is(address, QuicScheme) || Is(address, TcpScheme))
            return 8000;
        if (Is(address, TcpNodeScheme))
            return 15000;
        if (Is(address, SteamScheme))
            return 16000;
        if (Is(address, DirectScheme) && IPAddress.TryParse(address.DnsSafeHost, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork && (IPAddress.IsLoopback(ip) || IsPrivate(ToUInt(ip))))
                return 6000;
            return 5000;
        }
        return 10000;
    }

    private static bool Allowed(Uri address)
    {
        if (Is(address, QuicScheme) || Is(address, TcpScheme) || Is(address, TcpNodeScheme))
        {
            if (Lumora.Nexus.Transport.NetworkManagerRegistry.FindForScheme(address.Scheme) == null)
                return false;
            if (!Is(address, QuicScheme) && !Lumora.Nexus.Transport.NetworkOptions.AllowTcp)
                return false;
            if (Is(address, TcpNodeScheme) && !Lumora.Nexus.Transport.NetworkOptions.AllowRelay)
                return false;
            return address.HostNameType != UriHostNameType.IPv6
                   || (IPAddress.TryParse(address.DnsSafeHost, out var v6) && (IPAddress.IsLoopback(v6) || Lumora.Nexus.Transport.NetworkOptions.UsesIpv6));
        }
        if (Is(address, NatScheme))
            return Lumora.Nexus.Transport.NetworkOptions.UsesNodes
                   && (address.HostNameType != UriHostNameType.IPv6 || Lumora.Nexus.Transport.NetworkOptions.UsesIpv6)
                   && !(NodeEndPoint(address) is { } node && Lumora.Nexus.Transport.NodeUdpCheck.Answers(node) == false);
        if (Is(address, SteamScheme))
            return EngineSettings.SteamConnections != EngineSettings.SteamJoin.Off;
        if (Is(address, DirectScheme) && address.HostNameType == UriHostNameType.IPv6)
            return IPAddress.TryParse(address.DnsSafeHost, out var ip) && (IPAddress.IsLoopback(ip) || Lumora.Nexus.Transport.NetworkOptions.UsesIpv6);
        return true;
    }

    private static double Rank(Uri address, List<(uint Address, uint Mask)> networks)
    {
        if (Is(address, NatScheme))
            return address.HostNameType == UriHostNameType.IPv6 ? 2 : 3;
        if (Is(address, TcpNodeScheme))
            return 3.7;
        if (Is(address, QuicScheme) || Is(address, TcpScheme))
        {
            // Placed by where the same address would go over LNL: a foreign private one stays last.
            var asDirect = new UriBuilder(address) { Scheme = DirectScheme }.Uri;
            int place = (int)Rank(asDirect, networks);
            if (place >= 5)
                return 5;
            if (Is(address, TcpScheme))
                return 3.5;
            return Lumora.Nexus.Transport.NetworkOptions.PreferQuic ? place - 0.5 : 3.3;
        }
        if (Is(address, SteamScheme) && EngineSettings.SteamConnections == EngineSettings.SteamJoin.Prefer)
            return -1;
        if (!Is(address, DirectScheme))
            return 4;
        if (!IPAddress.TryParse(address.DnsSafeHost, out var ip))
            return 1;
        if (IPAddress.IsLoopback(ip))
            return 0;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return Lumora.Nexus.Transport.HostAddresses.IsGlobalIpv6(ip) ? 1 : 5;
        uint value = ToUInt(ip);
        foreach (var (local, mask) in networks)
        {
            if ((value & mask) == (local & mask))
                return 0;
        }
        return IsPrivate(value) ? 5 : 1;
    }

    private static bool Is(Uri address, string scheme) => string.Equals(address.Scheme, scheme, StringComparison.OrdinalIgnoreCase);

    private const int BlockedUdpTimeoutMs = 1000;
    private static readonly TimeSpan NodeUdpWait = TimeSpan.FromMilliseconds(1200);

    private static System.Net.IPEndPoint? NodeEndPoint(Uri address)
        => IPAddress.TryParse(address.DnsSafeHost, out var ip) && address.Port is > 0 and <= 65535
            ? new System.Net.IPEndPoint(ip, address.Port)
            : null;

    // Every node named gets its UDP ping, once a minute at most. Only off the main thread: the join runs on the
    // thread pool, and a frame must never wait on a network answer.
    private static void CheckNodeUdp(List<Uri> addresses)
    {
        var nodes = addresses
            .Where(a => (Is(a, NatScheme) || Is(a, TcpNodeScheme))
                        && (a.HostNameType != UriHostNameType.IPv6 || Lumora.Nexus.Transport.NetworkOptions.UsesIpv6))
            .Select(NodeEndPoint)
            .Where(n => n != null && Lumora.Nexus.Transport.NodeUdpCheck.Answers(n) == null)
            .Select(n => n!)
            .ToList();
        if (nodes.Count == 0 || !System.Threading.Thread.CurrentThread.IsThreadPoolThread)
            return;
        try
        {
            Lumora.Nexus.Transport.NodeUdpCheck.CheckAsync(nodes, NodeUdpWait).Wait(NodeUdpWait + TimeSpan.FromMilliseconds(400));
        }
        catch (AggregateException)
        {
        }
    }

    // An lnl-nat address whose node lists a TCP gate also goes in as lnt-nat, unless it is there already.
    private static List<Uri> WithNodeTcp(List<Uri> addresses)
    {
        if (Lumora.Nexus.Transport.NetworkManagerRegistry.FindForScheme(TcpNodeScheme) == null
            || !addresses.Any(a => Is(a, NatScheme)))
            return addresses;
        var nodes = Lumora.Nexus.Cloud.NetworkNodeDirectory.Cached;
        if (nodes.Count == 0 && System.Threading.Thread.CurrentThread.IsThreadPoolThread)
        {
            try
            {
                nodes = Lumora.Nexus.Cloud.NetworkNodeDirectory.GetAsync().Wait(TimeSpan.FromSeconds(2))
                    ? Lumora.Nexus.Cloud.NetworkNodeDirectory.Cached
                    : nodes;
            }
            catch (AggregateException)
            {
            }
        }
        var result = new List<Uri>(addresses);
        foreach (var nat in addresses.Where(a => Is(a, NatScheme)))
        {
            var node = nodes.FirstOrDefault(n => n.Port == nat.Port
                && (string.Equals(n.Address, nat.DnsSafeHost, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(n.Address6, nat.DnsSafeHost, StringComparison.OrdinalIgnoreCase)));
            if (Lumora.Nexus.Transport.LNT.LNTNetworkManager.NodeUriFor(nat, node) is { } overTcp && !result.Contains(overTcp))
                result.Add(overTcp);
        }
        return result;
    }

    // 10/8, 172.16/12, 192.168/16, link-local 169.254/16 and carrier NAT 100.64/10.
    private static bool IsPrivate(uint ip)
        => (ip & 0xFF000000) == 0x0A000000
        || (ip & 0xFFF00000) == 0xAC100000
        || (ip & 0xFFFF0000) == 0xC0A80000
        || (ip & 0xFFFF0000) == 0xA9FE0000
        || (ip & 0xFFC00000) == 0x64400000;

    private static List<(uint Address, uint Mask)> LocalNetworks()
    {
        var networks = new List<(uint, uint)>();
        try
        {
            foreach (var card in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (card.OperationalStatus != OperationalStatus.Up)
                    continue;
                foreach (var unicast in card.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address))
                        continue;
                    var mask = unicast.IPv4Mask;
                    networks.Add((ToUInt(unicast.Address),
                        mask != null && mask.AddressFamily == AddressFamily.InterNetwork ? ToUInt(mask) : 0xFFFFFFFF));
                }
            }
        }
        catch
        {
            // No card list means no address counts as same-network; the order still puts Steam ahead of
            // private addresses.
        }
        return networks;
    }

    private static uint ToUInt(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}
