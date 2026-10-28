// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Net;
using LumoraLogger = Lumora.Nexus.Diagnostics.NexusLog;

namespace Lumora.Nexus.Transport.LNL;

// INetworkManager facade for the existing LNL transport. Owns an internal LNLManager and exposes
// scheme-routing so sessions can dispatch by URI without knowing they're talking to LiteNetLib. - xlinka
public sealed class LNLNetworkManager : INetworkManager
{
    public const string SCHEME = "lnl";
    // lnl-nat://<node address>:<port>/<sessionId>: punch through the host's NAT node, relayed through the same
    // node when the punch does not get through.
    public const string NAT_SCHEME = "lnl-nat";

    private readonly LNLManager _inner = new();
    private readonly List<IListener> _listeners = new();
    private readonly List<IConnection> _connections = new();
    private bool _disposed;

    // Direct UDP at neutral priority. Steam relay can opt in higher when
    // present; loopback transports can opt in lower. - xlinka
    public int Priority => 0;

    public bool UsesPort => true;

    public IConnection CreateConnection(Uri uri)
    {
        if (uri == null) throw new ArgumentNullException(nameof(uri));
        var connection = _inner.CreateConnection(uri);
        _connections.Add(connection);
        return connection;
    }

    public IListener CreateListener(ushort port, string sessionId)
    {
        var listener = new LNLListener(LNLManager.APP_ID, port, IPAddress.Any, sessionId);
        if (listener.IsInitialized)
        {
            _listeners.Add(listener);
            LumoraLogger.Debug($"[lnl] LNLNetworkManager: listener up on 0.0.0.0:{port}");
        }
        else
        {
            LumoraLogger.Error($"[lnl] LNLNetworkManager: failed to start listener on port {port}");
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

    public void Update()
    {
        // LNLManager.PollEvents drives connections registered via its own
        // CreateConnection (which we delegate to). Listeners are owned here, so
        // poll them directly. - xlinka
        _inner.PollEvents();
        for (int i = 0; i < _listeners.Count; i++)
        {
            if (_listeners[i] is LNLListener lnl) lnl.Poll();
        }
    }

    public void Stop()
    {
        if (_disposed) return;
        _inner.Stop();
        foreach (var listener in _listeners) listener.Close();
        foreach (var connection in _connections) connection.Close();
        _listeners.Clear();
        _connections.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}
