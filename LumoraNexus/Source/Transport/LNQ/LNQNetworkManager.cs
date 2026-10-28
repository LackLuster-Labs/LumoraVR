// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace Lumora.Nexus.Transport.LNQ;

// Sessions over QUIC, straight to the host: lnq://<host>:<port>/<sessionId>. For a host reachable on a public
// address, where QUIC's congestion control and per-lane streams carry more than LNL's fixed windows. Register
// only where LNQSupport.IsAvailable. -xlinka
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class LNQNetworkManager : INetworkManager
{
    public const string SCHEME = LNQSupport.Scheme;

    private readonly object _gate = new();
    private readonly List<LNQListener> _listeners = new();
    private readonly List<LNQConnection> _connections = new();

    public int Priority => -100;

    public bool UsesPort => true;

    public IConnection CreateConnection(Uri uri)
    {
        if (uri == null) throw new ArgumentNullException(nameof(uri));
        var connection = new LNQConnection(uri);
        lock (_gate)
        {
            _connections.RemoveAll(c => c.IsClosed);
            _connections.Add(connection);
        }
        return connection;
    }

    public IListener CreateListener(ushort port, string sessionId)
    {
        var listener = new LNQListener(port, sessionId);
        lock (_gate)
        {
            _listeners.RemoveAll(l => !l.IsActive);
            if (listener.IsActive)
                _listeners.Add(listener);
        }
        return listener;
    }

    public void GetSupportedSchemes(List<string> schemes) => schemes?.Add(SCHEME);

    public bool SupportsScheme(string scheme) => string.Equals(scheme, SCHEME, StringComparison.OrdinalIgnoreCase);

    public List<Uri> GetPrioritizedUriList(IEnumerable<Uri> uris, out string expectedSessionId)
    {
        var result = new List<Uri>();
        expectedSessionId = null!;
        if (uris == null) return result;
        foreach (var uri in uris)
        {
            if (uri != null && SupportsScheme(uri.Scheme))
                result.Add(uri);
        }
        return result;
    }

    public void Update()
    {
    }

    public void Stop()
    {
        LNQListener[] listeners;
        LNQConnection[] connections;
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
