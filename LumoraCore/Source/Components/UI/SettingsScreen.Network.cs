// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Helio.UI.Listing;
using Lumora.Core.Localization;
using Lumora.Nexus.Cloud;
using Lumora.Nexus.Transport;
using Lumora.Nexus.Transport.LNL;
using Lumora.Nexus.Transport.PortMapping;

namespace Lumora.Core.Components.UI;

// NETWORK
//
// How this machine reaches other people's worlds and lets them reach its own. The live rows up top say what
// is happening now: the node holding the world you host and its round trip, how you are joined to the world
// in front of you, the router forward and this machine's IPv6. The node ranking below is the order a hosted
// world tries the official nodes in, which is also where it moves when its node fails. Every other row
// drives the transport directly; nothing here is a label for a behaviour that does not exist.
//
// Rows carry an Order so the ranking, rebuilt as round trips come in, lands under its own header. -xlinka
public sealed partial class SettingsScreen
{
    private const string NetworkPath = "network";
    private const long NetStatus = 100, NetJoining = 200, NetNodes = 300, NetRanking = 340, NetMeasure = 390, NetHosting = 400;

    private ListingDropdown? _nodeChoice;
    private ListingChoice? _steamChoice;
    private ListingChoice? _joinLevelChoice;
    private readonly List<ListingItem> _rankRows = new();
    private string _nodeSignature = string.Empty;
    private float _nodeRefreshAccum;

    private void BuildNetwork()
    {
        const string path = NetworkPath;

        NetHeader("Status", "Settings.Network.Section.Status".AsLocale("Status"), NetStatus);
        NetRow(new ListingLabel("net.node", "Settings.Network.NodeInUse".AsLocale("NAT Node"), DescribeNodeInUse), NetStatus + 1);
        NetRow(new ListingLabel("net.session", "Settings.Network.ThisSession".AsLocale("This Session"), DescribeSessionLink), NetStatus + 2);
        NetRow(new ListingLabel("net.router", "Settings.Network.Router".AsLocale("Router"), DescribeRouter), NetStatus + 3);
        NetRow(new ListingLabel("net.ipv6", "Settings.Network.Ipv6Address".AsLocale("IPv6"), DescribeIpv6), NetStatus + 4);

        NetHeader("Joining", "Settings.Network.Section.Joining".AsLocale("Joining"), NetJoining);
        // A punch always comes first. Off skips it and starts at the relay; the relay off stops after the punch.
        Toggle(path, "natpunch", "Settings.Network.Punch".AsLocale("Punch Through NAT"),
            () => EngineSettings.NatPunch, v => EngineSettings.NatPunch = v,
            "Settings.Network.Punch.Hint".AsLocale("Open a direct path through the host's node first")).Order = NetJoining + 1;
        Toggle(path, "natrelay", "Settings.Network.Relay".AsLocale("Relay When Needed"),
            () => EngineSettings.NatRelay, v => EngineSettings.NatRelay = v,
            "Settings.Network.Relay.Hint".AsLocale("Go through the node when no direct path opens")).Order = NetJoining + 2;
        Toggle(path, "ipv6", "Settings.Network.Ipv6".AsLocale("Use IPv6"),
            () => EngineSettings.Ipv6, v => EngineSettings.Ipv6 = v,
            "Settings.Network.Ipv6.Hint".AsLocale("List and dial IPv6 where both ends have it")).Order = NetJoining + 3;
        BuildSteamChoice();
        Toggle(path, "tcpfallback", "Settings.Network.Tcp".AsLocale("TCP When UDP Fails"),
            () => EngineSettings.TcpFallback, v => EngineSettings.TcpFallback = v,
            "Settings.Network.Tcp.Hint".AsLocale("Join over TCP when no UDP gets through")).Order = NetJoining + 5;
        // Only where this machine has QUIC at all.
        if (NetworkManagerRegistry.FindForScheme(Lumora.Nexus.Transport.LNQ.LNQSupport.Scheme) != null)
            Toggle(path, "preferquic", "Settings.Network.PreferQuic".AsLocale("Prefer QUIC"),
                () => EngineSettings.PreferQuic, v => EngineSettings.PreferQuic = v,
                "Settings.Network.PreferQuic.Hint".AsLocale("Try QUIC first when a host takes it directly")).Order = NetJoining + 6;

        NetHeader("Nodes", "Settings.Network.Section.Nodes".AsLocale("Nodes"), NetNodes);
        BuildNodeChoice();
        NetRow(new ListingAction("measurenodes", "Settings.Network.Measure".AsLocale("Round trip to every node"))
        {
            Invoke = () => MeasureNodes(force: true),
            ButtonLabel = "Settings.Network.MeasureButton".AsLocale("Measure"),
        }, NetMeasure);

        NetHeader("Hosting", "Settings.Network.Section.Hosting".AsLocale("Hosting"), NetHosting);
        BuildJoinLevelChoice();
        NetRow(new ListingTextField("hostingport", "Settings.Network.HostingPort".AsLocale("Hosting Port"))
        {
            DetailText = "Settings.Network.HostingPort.Hint".AsLocale("Your worlds use this port and the next ones up"),
            Placeholder = "Settings.Network.HostingPort.Placeholder".AsLocale("Any free port").Resolve(),
            MaxLength = 5,
            Read = () => EngineSettings.HostingPort > 0 ? EngineSettings.HostingPort.ToString() : string.Empty,
            Write = WriteHostingPort,
        }, NetHosting + 2);
        Toggle(path, "portforwarding", "Settings.Network.PortForwarding".AsLocale("Forward on Router"),
            () => EngineSettings.PortForwarding, v => EngineSettings.PortForwarding = v,
            "Settings.Network.PortForwarding.Hint".AsLocale("Open the port with UPnP, NAT-PMP or PCP")).Order = NetHosting + 3;
        Toggle(path, "landiscovery", "Settings.Network.LanDiscovery".AsLocale("Local Network Discovery"),
            () => EngineSettings.LanDiscovery, v => EngineSettings.LanDiscovery = v,
            "Settings.Network.LanDiscovery.Hint".AsLocale("Show your worlds on this network and find others")).Order = NetHosting + 4;
    }

    // Who can join a world you host until you change it on the create form or in the Session screen. It follows
    // the account. The levels are the catalog's, the ones the Session screen offers; the home stays private.
    private void BuildJoinLevelChoice()
    {
        var levels = SettingsCatalog.DefaultJoinLevel.Options;
        var options = new string[levels.Count];
        for (int i = 0; i < levels.Count; i++)
            options[i] = AccessLabels.Label(levels[i]).Resolve();
        _joinLevelChoice ??= NetRow(new ListingChoice("defaultjoinlevel", "Settings.Network.DefaultJoinLevel".AsLocale("Default Join Level"))
        {
            Options = options,
            Read = () => System.Math.Max(0, SettingsCatalog.DefaultJoinLevel.IndexOf(EngineSettings.DefaultJoinLevel)),
            Write = index => EngineSettings.DefaultJoinLevel = SettingsCatalog.DefaultJoinLevel.At(index),
            DetailText = "Settings.Network.DefaultJoinLevel.Hint".AsLocale("Who can join the worlds you host. Your home stays private"),
        }, NetHosting + 1);
        _joinLevelChoice.Options = options;
    }

    private void NetHeader(string id, LocaleText label, long order)
        => NetRow(new ListingHeader(NetworkPath + ".h." + id, label), order);

    private T NetRow<T>(T item, long order) where T : ListingItem
    {
        item.Order = order;
        return _entries.Add(NetworkPath, item);
    }

    // The choices whose option words are translated, updated in place when the language changes.
    private void BuildNetworkChoices()
    {
        BuildSteamChoice();
        BuildNodeChoice();
        BuildJoinLevelChoice();
    }

    // Only where Steam can carry a session at all.
    private void BuildSteamChoice()
    {
        if (NetworkManagerRegistry.FindForScheme("steam") == null)
            return;
        var options = new[]
        {
            "Settings.Network.Steam.Fallback".AsLocale("Last Resort").Resolve(),
            "Settings.Network.Steam.Prefer".AsLocale("First").Resolve(),
            "Settings.Network.Steam.Off".AsLocale("Never").Resolve(),
        };
        _steamChoice ??= NetRow(new ListingChoice("steamconnections", "Settings.Network.Steam".AsLocale("Steam Connections"))
        {
            Options = options,
            Read = () => (int)EngineSettings.SteamConnections,
            Write = index => EngineSettings.SteamConnections = (EngineSettings.SteamJoin)System.Math.Clamp(index, 0, 2),
            DetailText = "Settings.Network.Steam.Hint".AsLocale("Where Steam goes among a world's addresses"),
        }, NetJoining + 4);
        _steamChoice.Options = options;
    }

    // Automatic first, then every official NAT node with its last round trip. A picked node that has left the list
    // stays on offer, marked, so the choice is never silently lost. The ranking rows under it follow the same list.
    private void BuildNodeChoice()
    {
        var nodes = NatNodes();
        var ids = new List<string> { string.Empty };
        var names = new List<string> { "Settings.Network.Node.Automatic".AsLocale("Automatic").Resolve() };
        var details = new List<string> { "Settings.Network.Node.Automatic.Hint".AsLocale("The nearest node with room").Resolve() };
        foreach (var node in nodes)
        {
            if (ids.Contains(node.Id))
                continue;
            ids.Add(node.Id);
            names.Add(NodeTitle(node));
            details.Add(DescribePing(node, withAddress: true));
        }
        string saved = EngineSettings.NatNode;
        if (saved.Length > 0 && !ids.Contains(saved))
        {
            ids.Add(saved);
            names.Add("Settings.Network.Node.NotListed".AsLocale("{0} (not listed)", saved).Resolve());
            details.Add(string.Empty);
        }

        _nodeChoice ??= NetRow(new ListingDropdown("natnode", "Settings.Network.Node".AsLocale("Preferred Node"))
        {
            Options = names,
            Read = () => 0,
            Write = _ => { },
        }, NetNodes + 1);
        _nodeChoice.Options = names;
        _nodeChoice.OptionDetails = details;
        _nodeChoice.Read = () => System.Math.Max(0, ids.IndexOf(EngineSettings.NatNode));
        _nodeChoice.Write = index =>
        {
            if (index >= 0 && index < ids.Count)
                EngineSettings.NatNode = ids[index];
            RefreshNodeChoice(delta: 0f, force: true);
        };
        BuildRankRows();
        _nodeSignature = NodeSignature(nodes);
    }

    // One row per node in the order a hosted world tries them: the picked node first, then by round trip.
    private void BuildRankRows()
    {
        foreach (var row in _rankRows)
            _entries.Remove(NetworkPath, row.Key);
        _rankRows.Clear();

        var ranked = NetworkNodeDirectory.Ranked(EngineSettings.NatNode);
        var held = HeldNode();
        if (ranked.Count == 0)
        {
            _rankRows.Add(NetRow(new ListingLabel("net.rank.none", "Settings.Network.Ranking".AsLocale("Ranking"),
                () => "Settings.Network.Ranking.None".AsLocale("No node listed yet").Resolve()), NetRanking));
            return;
        }
        for (int i = 0; i < ranked.Count; i++)
        {
            var node = ranked[i];
            var marks = new List<string> { DescribePing(node, withAddress: false) };
            if (node.Id == EngineSettings.NatNode)
                marks.Add("Settings.Network.Ranking.Picked".AsLocale("picked").Resolve());
            if (held != null && held.Id == node.Id)
                marks.Add("Settings.Network.Ranking.InUse".AsLocale("in use").Resolve());
            string value = string.Join(", ", marks);
            _rankRows.Add(NetRow(new ListingLabel("net.rank." + i, $"{i + 1}. {NodeTitle(node)}", () => value), NetRanking + i));
        }
    }

    private static List<NetworkNode> NatNodes()
        => NetworkNodeDirectory.Cached.Where(n => n.Kind == NetworkNodeDirectory.KindNat).ToList();

    private static string NodeTitle(NetworkNode node)
        => string.IsNullOrEmpty(node.Region) ? node.Name : $"{node.Name} ({node.Region})";

    private static string DescribePing(NetworkNode node, bool withAddress)
    {
        var ping = NetworkNodeDirectory.LastPing(node);
        if (!node.HasCapacity)
            return "Settings.Network.Node.Full".AsLocale("full or draining").Resolve();
        string where = $"{node.Address}:{node.Port}";
        if (ping is { } ms)
            return withAddress
                ? "Settings.Network.Node.Detail".AsLocale("{0}, {1} ms", where, ms.ToString("0")).Resolve()
                : "Settings.Network.Node.Ping".AsLocale("{0} ms", ms.ToString("0")).Resolve();
        return withAddress
            ? "Settings.Network.Node.Unmeasured".AsLocale("{0}, not measured", where).Resolve()
            : "Settings.Network.Node.NotMeasured".AsLocale("not measured").Resolve();
    }

    // Rebuilt only when the list, a round trip or the node in use moved, at most once a second, so an open list
    // is not torn out from under the pointer for nothing.
    private void RefreshNodeChoice(float delta, bool force = false)
    {
        _nodeRefreshAccum += delta;
        if (!force && _nodeRefreshAccum < 1f)
            return;
        _nodeRefreshAccum = 0f;
        if (force || NodeSignature(NatNodes()) != _nodeSignature)
        {
            BuildNodeChoice();
            _entryView?.RefreshValues();
        }
    }

    private static string NodeSignature(List<NetworkNode> nodes)
    {
        var text = new StringBuilder();
        text.Append(EngineSettings.NatNode).Append('|').Append(HeldNode()?.Id).Append('|');
        foreach (var node in nodes)
        {
            text.Append(node.Id).Append('@').Append(node.Address).Append(':').Append(node.Port).Append('/').Append(node.HasCapacity).Append('=');
            if (NetworkNodeDirectory.LastPing(node) is { } ms)
                text.Append((int)ms);
            text.Append(';');
        }
        return text.ToString();
    }

    // The node holding the focused world, when this machine hosts it and it is listed.
    private static NetworkNode? HeldNode()
    {
        var world = Engine.Current?.WorldManager?.FocusedWorld;
        return world is { IsAuthority: true } ? world.Session?.NatListener?.NatNode : null;
    }

    // The list, then one ping to each node. At most every half minute unless asked from the button.
    private static void MeasureNodes(bool force)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await NetworkNodeDirectory.GetAsync(refresh: force).ConfigureAwait(false);
                await LNLNodeProbe.PingAsync(force).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn($"[lnl] Measuring nodes failed: {ex.Message}");
            }
        });
    }

    // Blank is any free port; a number outside the usable range snaps into it.
    private static void WriteHostingPort(string text)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            EngineSettings.HostingPort = 0;
            return;
        }
        if (int.TryParse(text, out int port))
            EngineSettings.HostingPort = port;
    }

    private static string DescribeNodeInUse()
    {
        var world = Engine.Current?.WorldManager?.FocusedWorld;
        var session = world?.Session;
        if (world != null && session != null && world.IsAuthority)
        {
            var listener = session.NatListener;
            if (listener == null)
                return "Settings.Network.Status.NotListed".AsLocale("Only for listed worlds").Resolve();
            if (listener.NatNode is { } held)
            {
                double rtt = listener.NatRoundTripMs;
                return rtt >= 0
                    ? "Settings.Network.Status.NodeHeld".AsLocale("{0}, {1} ms", held.Name, rtt.ToString("0")).Resolve()
                    : "Settings.Network.Status.NodeHeldMeasuring".AsLocale("{0}, measuring", held.Name).Resolve();
            }
            if (session.StreamReach?.NodeHeldOverTcp is { } overTcp)
                return "Settings.Network.Status.NodeHeldTcp".AsLocale("{0}, over TCP", overTcp.Name).Resolve();
            return "Settings.Network.Status.FindingNode".AsLocale("Finding a node").Resolve();
        }

        var first = NetworkNodeDirectory.Ranked(EngineSettings.NatNode).FirstOrDefault();
        if (first != null && NetworkNodeDirectory.LastPing(first) is { } ms)
            return "Settings.Network.Status.Nearest".AsLocale("Nearest: {0}, {1} ms", first.Name, ms.ToString("0")).Resolve();
        return "Settings.Network.Status.NoNode".AsLocale("No node measured").Resolve();
    }

    private static string DescribeSessionLink()
    {
        var world = Engine.Current?.WorldManager?.FocusedWorld;
        var session = world?.Session;
        if (world == null || session == null || world.IsDisposed)
            return "Settings.Network.Status.NoSession".AsLocale("Not in a session").Resolve();

        if (world.IsAuthority)
        {
            int joined = 0, relayed = 0;
            foreach (var user in world.GetAllUsers())
            {
                if (user == world.LocalUser || !session.Connections.TryGetConnection(user, out var connection))
                    continue;
                joined++;
                if (connection.ViaRelay)
                    relayed++;
            }
            return "Settings.Network.Status.Hosting".AsLocale("Hosting, {0} joined, {1} relayed", joined, relayed).Resolve();
        }

        var link = session.Connections.HostConnection;
        if (link == null)
            return "Settings.Network.Status.Connecting".AsLocale("Connecting").Resolve();
        if (link.TransportName == "Steam")
            return "Settings.Network.Status.Steam".AsLocale("Through Steam").Resolve();
        if (link.TransportName == "TCP")
            return link.ViaRelay
                ? "Settings.Network.Status.TcpRelayed".AsLocale("Over TCP through {0}", NodeName(link.Address)).Resolve()
                : "Settings.Network.Status.Tcp".AsLocale("Direct over TCP").Resolve();
        if (link.TransportName == "QUIC")
            return "Settings.Network.Status.Quic".AsLocale("Direct over QUIC").Resolve();
        if (link.ViaRelay)
            return "Settings.Network.Status.Relayed".AsLocale("Relayed through {0}", NodeName(link.Address)).Resolve();
        if (string.Equals(link.Address.Scheme, LNLNetworkManager.NAT_SCHEME, StringComparison.OrdinalIgnoreCase))
            return "Settings.Network.Status.Punched".AsLocale("Punched through {0}", NodeName(link.Address)).Resolve();
        if (link.Address.HostNameType == UriHostNameType.IPv6)
            return "Settings.Network.Status.DirectIpv6".AsLocale("Direct over IPv6").Resolve();
        return "Settings.Network.Status.Direct".AsLocale("Direct").Resolve();
    }

    // The router forward of the focused hosted world.
    private static string DescribeRouter()
    {
        if (!EngineSettings.PortForwarding)
            return "Settings.Network.Router.Off".AsLocale("Forwarding off").Resolve();
        var world = Engine.Current?.WorldManager?.FocusedWorld;
        var listener = world is { IsAuthority: true } ? world.Session?.NatListener : null;
        if (listener == null)
            return "Settings.Network.Router.Idle".AsLocale("Only while hosting a listed world").Resolve();
        var mapper = listener.PortMapper;
        return mapper?.State switch
        {
            PortMapState.Mapped when mapper.External is { } outside
                => "Settings.Network.Router.Mapped".AsLocale("Forwarded {0} by {1}", outside.Port, mapper.Method).Resolve(),
            PortMapState.Refused => "Settings.Network.Router.Refused".AsLocale("Router didn't allow it").Resolve(),
            PortMapState.NoRouter => "Settings.Network.Router.NoRouter".AsLocale("No router answered").Resolve(),
            PortMapState.NotPublic => "Settings.Network.Router.NotPublic".AsLocale("Router is behind another NAT").Resolve(),
            _ => "Settings.Network.Router.Asking".AsLocale("Asking the router").Resolve(),
        };
    }

    private static string DescribeIpv6()
    {
        if (!EngineSettings.Ipv6)
            return "Settings.Network.Ipv6.Off".AsLocale("Off").Resolve();
        var addresses = HostAddresses.GlobalIpv6;
        return addresses.Count > 0
            ? addresses[0].ToString()
            : "Settings.Network.Ipv6.None".AsLocale("No public IPv6 here").Resolve();
    }

    // The node's name where the list knows the address, else the address itself.
    private static string NodeName(Uri address)
    {
        string host = address.DnsSafeHost;
        foreach (var node in NetworkNodeDirectory.Cached)
        {
            if (node.Port == address.Port && (string.Equals(node.Address, host, StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(node.Address6, host, StringComparison.OrdinalIgnoreCase)))
                return node.Name;
        }
        return address.Host;
    }
}
