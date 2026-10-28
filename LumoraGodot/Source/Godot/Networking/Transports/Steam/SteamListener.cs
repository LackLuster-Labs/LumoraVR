// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.
// Steam is a desktop-only transport. The Steamworks binding has no Android build, and a type that
// merely MENTIONS a Steamworks type cannot be loaded there: the whole engine init failed on a Pico 4
// with 'Could not load type of field SteamNetworkManager:_listenerMap', long before any runtime
// 'is this Android' check could run. Compile the transport out on mobile entirely. -xlinka
#if GODOT_PC

using System;
using System.Collections.Generic;
using Steamworks;
using Lumora.Nexus.Transport;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Networking.Transports.Steam;

// Host-side P2P listener over SteamNetworkingSockets. Opens two virtual ports per session
// (foreground + background, mirroring SteamConnection's channel split) and lazily materialises a
// SteamConnection per remote CSteamID as incoming requests come in.
//
// LocalUri is null because Steam P2P is relay-only; only the GlobalUri
// (steam://{hostSteamID}/{channelGroup}/{sessionId}) is dialable. - xlinka
public sealed class SteamListener : IListener
{
    private const int ChannelCount = 2;

    private readonly SteamNetworkManager _manager;
    private readonly CSteamID _hostUser;
    private readonly int _channelGroup;
    private readonly string _sessionId;
    private readonly HSteamListenSocket[] _listeners = new HSteamListenSocket[ChannelCount];
    private readonly Dictionary<ulong, SteamConnection> _connectionsBySteamId = new();
    private readonly Dictionary<uint, SteamConnection> _connectionsByHandle = new();
    // Peers already handed to the session. Both channels report Connecting and then Connected, and each
    // of those used to raise PeerConnected again: the session hooked its message handler once per raise,
    // ran every join request three times, rejected the copies as stale, and counted the same peer as
    // pending over and over until the host refused it outright. -xlinka
    private readonly HashSet<SteamConnection> _announced = new();
    private HSteamNetPollGroup _pollGroup;
    private ReassemblyState _dummyReassembly;
    private bool _isActive;

    public bool IsActive => _isActive;
    public Uri LocalUri => null!;
    public Uri GlobalUri { get; }

    public event Action<IConnection> PeerConnected = null!;
    public event Action<IConnection> PeerDisconnected = null!;

    internal IEnumerable<HSteamListenSocket> Listeners
    {
        get
        {
            for (int i = 0; i < _listeners.Length; i++)
            {
                var s = _listeners[i];
                if (s != HSteamListenSocket.Invalid) yield return s;
            }
        }
    }

    internal SteamListener(CSteamID hostUser, int channelGroup, string sessionId, SteamNetworkManager manager)
    {
        _hostUser = hostUser;
        _channelGroup = channelGroup;
        _sessionId = sessionId ?? string.Empty;
        _manager = manager;

        int basePort = channelGroup * ChannelCount;
        for (int i = 0; i < ChannelCount; i++)
        {
            var config = manager.GetConfig(i);
            _listeners[i] = SteamNetworkingSockets.CreateListenSocketP2P(basePort + i, config.Length, config);
            manager.RegisterListener(_listeners[i], this);
        }
        _pollGroup = SteamNetworkingSockets.CreatePollGroup();

        // Reassembly state sentinel for messages on connections we haven't yet
        // bound to a SteamConnection (e.g. during handshake). - xlinka
        _dummyReassembly = new ReassemblyState { buffer = Array.Empty<byte>(), expectingBytes = -1, receivedBytes = -1 };

        GlobalUri = SteamNetworkManager.BuildUri(hostUser, channelGroup, _sessionId);
        _isActive = true;
        LumoraLogger.Log($"SteamListener: up for host {hostUser.m_SteamID}, channelGroup {channelGroup}, sessionId={_sessionId}");
    }

    public void Close()
    {
        lock (_manager.Lock)
        {
            if (!_isActive) return;
            _isActive = false;
            var snapshot = new List<SteamConnection>(_connectionsBySteamId.Values);
            foreach (var c in snapshot) c.Close(SteamCloseReason.ClosedLocally);
            _connectionsBySteamId.Clear();
            _connectionsByHandle.Clear();
            _announced.Clear();
            _manager.UnregisterListener(this);
        }
    }

    public void Dispose()
    {
        for (int i = 0; i < _listeners.Length; i++)
        {
            if (_listeners[i] != HSteamListenSocket.Invalid)
            {
                SteamNetworkingSockets.CloseListenSocket(_listeners[i]);
                _listeners[i] = default;
            }
        }
        if (_pollGroup != default)
        {
            SteamNetworkingSockets.DestroyPollGroup(_pollGroup);
            _pollGroup = default;
        }
    }

    internal void Update(IntPtr[] buffer)
    {
        if (!_isActive) return;
        try
        {
            // Every peer of this session shares the one poll group, so the same drain applies. -xlinka
            for (int batch = 0; batch < SteamNetworkManager.MaxReceiveBatchesPerFrame && _isActive; batch++)
            {
                int count = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(_pollGroup, buffer, buffer.Length);
                SteamNetworkManager.ExtractMessages(count, buffer, OnMessage, OnReceiveError, GetReassembly);
                if (count < buffer.Length) break;
            }
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"SteamListener.Update: {ex}");
            Close();
        }
    }

    private readonly List<SteamConnection> _flushScratch = new();

    internal void FlushWaiting()
    {
        if (!_isActive || _connectionsBySteamId.Count == 0) return;
        _flushScratch.Clear();
        _flushScratch.AddRange(_connectionsBySteamId.Values);
        foreach (var c in _flushScratch) c.FlushWaiting();
        _flushScratch.Clear();
    }

    internal void UpdateStats()
    {
        if (!_isActive) return;
        foreach (var c in _connectionsBySteamId.Values) c.UpdateStats();
    }

    internal void Pace(double nowMs)
    {
        if (!_isActive) return;
        foreach (var c in _connectionsBySteamId.Values) c.Pace(nowMs);
    }

    private ref ReassemblyState GetReassembly(uint connectionHandle)
    {
        if (_connectionsByHandle.TryGetValue(connectionHandle, out var conn)) return ref conn.GetReassembly(connectionHandle);
        // A handle no connection holds yet: its messages are dropped, so its state starts over each time
        // rather than carrying a half-built frame from one stray message into the next. -xlinka
        SteamFraming.Reset(ref _dummyReassembly);
        return ref _dummyReassembly;
    }

    private void OnReceiveError(uint connectionHandle)
    {
        if (_connectionsByHandle.TryGetValue(connectionHandle, out var conn))
            conn.Close(SteamCloseReason.ReceiveError);
    }

    private void OnMessage(ulong steamId, uint connectionHandle, byte[] data, int length)
    {
        if (!_isActive) return;
        if (_connectionsBySteamId.TryGetValue(steamId, out var conn))
            conn.OnMessage(steamId, connectionHandle, data, length);
    }

    internal void ConnectionStatusChanged(ref SteamNetConnectionStatusChangedCallback_t info)
    {
        if (!_isActive) return;
        var remoteSteamId = info.m_info.m_identityRemote.GetSteamID();
        int channel = GetChannel(info.m_info.m_hListenSocket);

        switch (info.m_info.m_eState)
        {
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                if (channel < 0)
                {
                    SteamNetworkingSockets.CloseConnection(info.m_hConn, (int)SteamCloseReason.ChannelMismatch, null, false);
                    return;
                }
                if (!_connectionsBySteamId.TryGetValue(remoteSteamId.m_SteamID, out var conn))
                    conn = Track(remoteSteamId);
                bool assigned = conn.AssignConnection(info.m_hConn, channel);
                if (!assigned)
                {
                    // The same account dialing in again while we still hold its last connection: it lost the link or
                    // left and is back before that one timed out. Turned away, it could not rejoin for fifteen seconds
                    // while the session kept its old user standing there; the old connection goes instead. -xlinka
                    LumoraLogger.Log($"SteamListener: {remoteSteamId.m_SteamID} connected again, replacing its last connection");
                    conn.Close(SteamCloseReason.ClosedRemotely);
                    conn = Track(remoteSteamId);
                    assigned = conn.AssignConnection(info.m_hConn, channel);
                }
                if (!assigned)
                {
                    SteamNetworkingSockets.CloseConnection(info.m_hConn, (int)SteamCloseReason.ChannelMismatch, null, false);
                    return;
                }
                _connectionsByHandle[info.m_hConn.m_HSteamNetConnection] = conn;
                SteamNetworkingSockets.AcceptConnection(info.m_hConn);
                SteamNetworkingSockets.SetConnectionPollGroup(info.m_hConn, _pollGroup);

                // PeerConnected fires once both channels are bound, and only once. - xlinka
                Announce(conn);
                break;

            // These go to the connection that holds the handle, not to whoever has that account now: a close still
            // queued for a replaced connection's handles would otherwise take its replacement down with it.
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                // The second channel can come up after Connecting was seen for both. - xlinka
                if (_connectionsByHandle.TryGetValue(info.m_hConn.m_HSteamNetConnection, out var openedConn))
                    Announce(openedConn);
                break;

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                if (_connectionsByHandle.TryGetValue(info.m_hConn.m_HSteamNetConnection, out var closing))
                    closing.Close(SteamCloseReason.ClosedRemotely);
                break;

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                if (_connectionsByHandle.TryGetValue(info.m_hConn.m_HSteamNetConnection, out var failed))
                    failed.Close(SteamCloseReason.LocalProblem);
                break;
        }
    }

    private SteamConnection Track(CSteamID remoteSteamId)
    {
        var conn = new SteamConnection(remoteSteamId, _channelGroup, _sessionId, _manager);
        _connectionsBySteamId[remoteSteamId.m_SteamID] = conn;
        conn.Closed += OnConnectionClosed;
        return conn;
    }

    private void Announce(SteamConnection conn)
    {
        if (conn.IsOpen && _announced.Add(conn))
            PeerConnected?.Invoke(conn);
    }

    private void OnConnectionClosed(IConnection connection)
    {
        var sc = connection as SteamConnection;
        if (sc == null) return;
        bool announced;
        lock (_manager.Lock)
        {
            foreach (var handle in sc.Connections) _connectionsByHandle.Remove(handle.m_HSteamNetConnection);
            if (_connectionsBySteamId.TryGetValue(sc.RemoteId.m_SteamID, out var held) && held == sc)
                _connectionsBySteamId.Remove(sc.RemoteId.m_SteamID);
            announced = _announced.Remove(sc);
        }
        // A connection the session never heard of has nothing for it to take down.
        if (announced)
            PeerDisconnected?.Invoke(sc);
    }

    private int GetChannel(HSteamListenSocket listener)
    {
        for (int i = 0; i < _listeners.Length; i++)
        {
            if (_listeners[i] == listener) return i;
        }
        return -1;
    }
}
#endif
