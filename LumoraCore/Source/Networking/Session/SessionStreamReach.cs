// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Lumora.Nexus.Cloud;
using Lumora.Nexus.Transport;
using Lumora.Nexus.Transport.LNL;
using Lumora.Nexus.Transport.LNT;
using Lumora.Nexus.Transport.PortMapping;

namespace Lumora.Core.Networking.Session;

// What a listed session offers joiners over TCP and QUIC, beside what its LNL listener holds: TCP through the node
// its UDP registered on when that node has a gate, or through a node it holds over TCP when its UDP never got
// there, and its TCP and QUIC ports forwarded on the router like the LNL one. -xlinka
internal sealed class SessionStreamReach
{
    private readonly LNLListener _lnl;
    private readonly LNTListener? _tcp;
    private readonly List<IRouterForwarded> _forwarded;
    private readonly Action _changed;

    public SessionStreamReach(IReadOnlyList<IListener> listeners, LNLListener lnl, Action changed)
    {
        _lnl = lnl;
        _changed = changed;
        _tcp = listeners.OfType<LNTListener>().FirstOrDefault();
        _forwarded = listeners.OfType<IRouterForwarded>().ToList();
    }

    // The node the session is held on over TCP alone, while it is.
    public NetworkNode? NodeHeldOverTcp => _tcp?.NodeHeld;

    public void Start(bool forward)
    {
        if (_tcp != null)
        {
            _tcp.NodeUriChanged += _changed;
            _tcp.StartNodeHold(() => _lnl.NatUris.Count > 0);
        }
        foreach (var listener in _forwarded)
            listener.PortMappingChanged += _changed;
        if (forward)
            Forwarding(true);
    }

    public void Stop()
    {
        if (_tcp != null)
        {
            _tcp.NodeUriChanged -= _changed;
            _tcp.StopNodeHold();
        }
        foreach (var listener in _forwarded)
            listener.PortMappingChanged -= _changed;
        TakeForwardsBack();
    }

    // Turned off, the forwards go back off the calling thread: the router can take a while to answer.
    public void Forwarding(bool on)
    {
        if (on)
        {
            foreach (var listener in _forwarded)
                listener.StartPortMapping();
        }
        else if (_forwarded.Any(l => l.PortMapper != null))
        {
            _ = Task.Run(TakeForwardsBack);
        }
    }

    // All at once, so the whole lot takes no longer than one forward does.
    private void TakeForwardsBack()
    {
        var stopping = _forwarded.Where(l => l.PortMapper != null).Select(l => Task.Run(l.StopPortMapping)).ToArray();
        if (stopping.Length > 0)
            Task.WaitAll(stopping, TimeSpan.FromSeconds(3));
    }

    public List<Uri> Urls(string sessionId)
    {
        var urls = new List<Uri>();
        foreach (var listener in _forwarded)
        {
            if (listener.PortMapper is { State: PortMapState.Mapped, External: { } outside })
                urls.Add(Build(listener.Scheme, outside, sessionId));
        }
        if (_tcp != null)
        {
            var natV4 = _lnl.NatUris.FirstOrDefault(u => u.HostNameType != UriHostNameType.IPv6);
            var overTcp = natV4 != null ? LNTNetworkManager.NodeUriFor(natV4, _lnl.NatNode) : _tcp.NodeUri;
            if (overTcp != null)
                urls.Add(overTcp);
        }
        return urls;
    }

    private static Uri Build(string scheme, IPEndPoint outside, string sessionId)
    {
        var address = outside.Address.IsIPv4MappedToIPv6 ? outside.Address.MapToIPv4() : outside.Address;
        string host = address.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + address + "]" : address.ToString();
        return new Uri($"{scheme}://{host}:{outside.Port}/{sessionId}");
    }
}
