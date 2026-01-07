using System;
using System.Collections.Generic;
using System.Net;
using LiteNetLib;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.LNL;

/// <summary>
/// LiteNetLib network manager - creates connections and listeners.
/// Platform-agnostic implementation.
/// </summary>
public class LNLManager
{
    public const int DEFAULT_PORT = 7777;
    public const string APP_ID = "LumoraVR";

    private List<LNLListener> _listeners = new();
    private List<LNLConnection> _connections = new();

    /// <summary>
    /// Create a new client connection to a server.
    /// </summary>
    public LNLConnection CreateConnection(Uri uri, bool dontRoute = false, IPAddress bindIP = null)
    {
        bindIP ??= IPAddress.Any;

        var connection = new LNLConnection(APP_ID, uri, dontRoute, bindIP);
        _connections.Add(connection);

        AquaLogger.Log($"Created LNL connection to {uri}");
        return connection;
    }

    /// <summary>
    /// Create listeners on all available network interfaces.
    /// </summary>
    public List<LNLListener> CreateListeners(ushort port)
    {
        var listeners = new List<LNLListener>();

        // Global listener on all interfaces
        var globalListener = new LNLListener(APP_ID, port, IPAddress.Any);
        if (globalListener.IsInitialized)
        {
            _listeners.Add(globalListener);
            listeners.Add(globalListener);
            AquaLogger.Log($"Created LNL listener on 0.0.0.0:{port}");
        }

        return listeners;
    }

    // TODO: Call PollEvents() from platform driver update loop
    /// <summary>
    /// Poll all connections and listeners for events.
    /// Must be called every frame by platform driver.
    /// </summary>
    public void PollEvents()
    {
        // Poll all listeners
        foreach (var listener in _listeners)
        {
            listener.Poll();
        }

        // Poll all connections
        foreach (var connection in _connections)
        {
            connection.Poll();
        }
    }

    /// <summary>
    /// Send data to multiple connections.
    /// </summary>
    public void TransmitData(byte[] data, int length, List<IConnection> targets, bool reliable, bool background)
    {
        foreach (var target in targets)
        {
            target.Send(data, length, reliable, background);
        }
    }

    // TODO: Call Stop() from platform shutdown
    /// <summary>
    /// Shutdown all connections and listeners.
    /// Should be called by platform driver on shutdown.
    /// </summary>
    public void Stop()
    {
        foreach (var connection in _connections)
        {
            connection.Close();
        }
        _connections.Clear();

        foreach (var listener in _listeners)
        {
            listener.Stop();
        }
        _listeners.Clear();

        AquaLogger.Log("LNL Manager shut down");
    }
}
