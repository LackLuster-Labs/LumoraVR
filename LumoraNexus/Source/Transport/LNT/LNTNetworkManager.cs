// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;

namespace Lumora.Nexus.Transport.LNT;

// Sessions over TCP, for networks that let no UDP through.
//   lnt://<host>:<port>/<sessionId>          the host itself, on its session port number
//   lnt-nat://<node>:<main port>/<sessionId>  through the TCP gate of the host's NAT node
// Dialled after every UDP way in has failed. -xlinka
public sealed class LNTNetworkManager : INetworkManager
{
    public const string SCHEME = "lnt";
    public const string NAT_SCHEME = "lnt-nat";

    private readonly object _gate = new();
    private readonly List<LNTListener> _listeners = new();
    private readonly List<LNTConnection> _connections = new();

    public int Priority => -200;

    // The TCP address through the node a session holds an lnl-nat address on, or null when that node has no gate.
    public static Uri? NodeUriFor(Uri natUri, Lumora.Nexus.Cloud.NetworkNode? node)
    {
        if (natUri == null || node == null || !LNTNodeGate.HasGate(node))
            return null;
        return new UriBuilder(natUri) { Scheme = NAT_SCHEME }.Uri;
    }

    public bool UsesPort => true;

    public IConnection CreateConnection(Uri uri)
    {
        if (uri == null) throw new ArgumentNullException(nameof(uri));
        var connection = new LNTConnection(uri);
        lock (_gate)
        {
            _connections.RemoveAll(c => c.IsClosed);
            _connections.Add(connection);
        }
        return connection;
    }

    public IListener CreateListener(ushort port, string sessionId)
    {
        var listener = new LNTListener(port, sessionId);
        lock (_gate)
        {
            _listeners.RemoveAll(l => !l.IsActive);
            if (listener.IsActive)
                _listeners.Add(listener);
        }
        return listener;
    }

    public void GetSupportedSchemes(List<string> schemes)
    {
        if (schemes == null) return;
        schemes.Add(SCHEME);
        schemes.Add(NAT_SCHEME);
    }

    public bool SupportsScheme(string scheme)
        => string.Equals(scheme, SCHEME, StringComparison.OrdinalIgnoreCase)
           || string.Equals(scheme, NAT_SCHEME, StringComparison.OrdinalIgnoreCase);

    public List<Uri> GetPrioritizedUriList(IEnumerable<Uri> uris, out string expectedSessionId)
    {
        var result = new List<Uri>();
        expectedSessionId = null!;
        if (uris == null) return result;
        foreach (var uri in uris)
        {
            if (uri == null || !SupportsScheme(uri.Scheme))
                continue;
            result.Add(uri);
            if (expectedSessionId == null && string.Equals(uri.Scheme, NAT_SCHEME, StringComparison.OrdinalIgnoreCase))
                expectedSessionId = uri.AbsolutePath.Trim('/');
        }
        return result;
    }

    // Links run on their own tasks; nothing to pump.
    public void Update()
    {
    }

    public void Stop()
    {
        LNTListener[] listeners;
        LNTConnection[] connections;
        lock (_gate)
        {
            listeners = _listeners.ToArray();
            connections = _connections.ToArray();
            _listeners.Clear();
            _connections.Clear();
        }
        foreach (var listener in listeners)
            listener.Close();
        foreach (var connection in connections)
            connection.Close();
    }

    public void Dispose() => Stop();
}
