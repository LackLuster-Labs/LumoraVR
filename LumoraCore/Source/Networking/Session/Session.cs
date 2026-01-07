using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Networking.LNL;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Session;

/// <summary>
/// Session wraps all networking for a World.
/// 
/// </summary>
public class Session : IDisposable
{
    public World World { get; private set; }
    public SessionConnectionManager Connections { get; private set; }
    public SessionSyncManager Sync { get; private set; }

    public bool IsDisposed { get; private set; }
    public TimeSpan Latency { get; set; }

    private Session(World world)
    {
        World = world;
        Connections = new SessionConnectionManager(this);
    }

    /// <summary>
    /// Create a new session as host (authority).
    /// </summary>
    public static Session NewSession(World world, ushort port = 7777)
    {
        var session = new Session(world);

        AquaLogger.Log($"Creating new session as host on port {port}");

        // Start listener
        if (!session.Connections.StartListener(port))
        {
            throw new Exception($"Failed to start listener on port {port}");
        }

        // Create sync manager with dedicated thread
        session.Sync = new SessionSyncManager(session);
        session.Sync.Start();

        AquaLogger.Log("Session created as host");
        return session;
    }

    /// <summary>
    /// Join an existing session as client.
    /// </summary>
    public static Session JoinSession(World world, IEnumerable<Uri> addresses)
    {
        var session = new Session(world);

        AquaLogger.Log($"Joining session at {string.Join(", ", addresses)}");

        // Connect to host
        var connected = session.Connections.ConnectToAsync(addresses).Result;
        if (!connected)
        {
            session.Dispose();
            throw new Exception("Failed to connect to session");
        }

        // Create sync manager with dedicated thread
        session.Sync = new SessionSyncManager(session);
        session.Sync.Start();

        AquaLogger.Log("Session joined as client");
        return session;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;

        AquaLogger.Log("Disposing session");

        Sync?.Dispose();
        Connections?.Dispose();

        World = null;
    }
}
