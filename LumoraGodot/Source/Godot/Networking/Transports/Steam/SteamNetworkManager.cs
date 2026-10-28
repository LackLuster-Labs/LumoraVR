// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.
// Steam is a desktop-only transport. The Steamworks binding has no Android build, and a type that
// merely MENTIONS a Steamworks type cannot be loaded there: the whole engine init failed on a Pico 4
// with 'Could not load type of field SteamNetworkManager:_listenerMap', long before any runtime
// 'is this Android' check could run. Compile the transport out on mobile entirely. -xlinka
#if GODOT_PC

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Steamworks;
using Lumora.Nexus.Transport;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Networking.Transports.Steam;

// Top-level Steam relay transport. One instance per engine: owns the global
// SteamNetConnectionStatusChangedCallback_t callback, allocates channel-group indices, and pumps
// every owned SteamListener / SteamConnection from a thread of its own.
//
// URI scheme is steam://{hostSteamID}/{channelGroup}/{sessionId}; channel group is doubled to
// virtual ports (foreground/background) inside the listener and connection. - xlinka
public sealed class SteamNetworkManager : INetworkManager
{
    public const string SCHEME = "steam";
    private const int MessageBufferSize = 256;
    // Batches read per poll group per pump. The cap only bounds one pass's work if a peer is flooded. -xlinka
    internal const int MaxReceiveBatchesPerFrame = 64;
    // A buffer this full is not fatal: what does not fit waits in the connection's own queue (SteamSendQueue)
    // and the sync loop sends a peer that far behind only what it cannot do without. -xlinka
    private const int SendBufferSize = 8 * 1024 * 1024;
    // The background connection carries asset transfers. What actually goes out is held by the transferer's
    // window, which backs off as soon as anything queues; its buffer holds the largest window with room over.
    // The rate of both is SteamPace's. -xlinka
    private const int BulkSendBufferSize = 32 * 1024 * 1024;
    // What arrived and nobody has taken yet. The library's own limit is 1 MB and 1000 messages, past which it
    // drops packets, and a dropped reliable packet costs its whole channel a resend round trip. At 16 MB/s
    // 1 MB is 60 ms, one long frame or one GC on this side. -xlinka
    private const int RecvBufferSize = 32 * 1024 * 1024;
    private const int RecvBufferMessages = 65536;
    private const int InitialTimeoutMs = 15000;
    private const int ConnectedTimeoutMs = 15000;
    private const double PaceIntervalMs = 50;

    internal object Lock { get; } = new();

    private readonly ConcurrentQueue<RawOutMessage> _toTransmit = new();
    private readonly Dictionary<HSteamListenSocket, SteamListener> _listenerMap = new();
    private readonly Dictionary<uint, SteamConnection> _connectionMap = new();
    private readonly List<SteamListener> _listeners = new();
    private readonly List<SteamConnection> _connections = new();
    private readonly Queue<IDisposable> _toDispose = new();
    // Taken off the poll groups under the lock, handed on after it: a session handler that closes a
    // connection or answers on one must not do it inside the transport's lock.
    private readonly List<(SteamConnection Connection, byte[] Data, int Length)> _arrived = new();

    private Callback<SteamNetConnectionStatusChangedCallback_t> _statusCallback = null!;
    private IntPtr[] _messageBuffer = null!;
    private CSteamID _localUser;
    private DateTime _lastStatUpdate;
    private double _nextPace;
    private int _channelPool;
    private Thread? _pump;
    private volatile bool _pumpStop;
    private bool _initialized;
    private bool _disposed;

    internal SteamNetworkingConfigValue_t[] DefaultConfigMain { get; private set; } = null!;
    internal SteamNetworkingConfigValue_t[] DefaultConfigBackground { get; private set; } = null!;

    internal SteamNetworkingConfigValue_t[] GetConfig(int channelIndex) => channelIndex switch
    {
        0 => DefaultConfigMain,
        1 => DefaultConfigBackground,
        _ => throw new ArgumentOutOfRangeException(nameof(channelIndex), "Steam channel index must be 0 or 1"),
    };

    public int Priority => 100;

    // Steam relay has no host port - addressing is identity-based.
    public bool UsesPort => false;

    // True once Initialize has succeeded. Bootstrap should skip the manager (and not register it)
    // if Steam isn't running. - xlinka
    public bool IsInitialized => _initialized;

    public CSteamID LocalUser => _localUser;

    // Wires up the relay callback and reads the local CSteamID. Must be called after
    // SteamAPI.Init() (or returns false silently if Steam is unavailable). - xlinka
    public bool Initialize()
    {
        if (_initialized) return true;
        try
        {
            _statusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
            _messageBuffer = new IntPtr[MessageBufferSize];
            SteamNetworkingSockets.GetIdentity(out var identity);
            _localUser = identity.GetSteamID();

            DefaultConfigMain = BuildConfig(SendBufferSize);
            DefaultConfigBackground = BuildConfig(BulkSendBufferSize);

            _initialized = true;
            StartPump();
            LumoraLogger.Log($"SteamNetworkManager: ready, local SteamID={_localUser.m_SteamID}");
            return true;
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"SteamNetworkManager: init failed ({ex.Message}) - Steam transport disabled");
            return false;
        }
    }

    public IConnection CreateConnection(Uri uri)
    {
        if (uri == null) throw new ArgumentNullException(nameof(uri));
        if (!_initialized) throw new InvalidOperationException("SteamNetworkManager: Initialize() not called");
        ParseUri(uri, out var user, out var channelGroup, out var sessionId);
        lock (Lock)
        {
            var conn = new SteamConnection(user, channelGroup, sessionId, this);
            conn.Address = uri;
            _connections.Add(conn);
            return conn;
        }
    }

    public IListener CreateListener(ushort port, string sessionId)
    {
        if (!_initialized) throw new InvalidOperationException("SteamNetworkManager: Initialize() not called");
        lock (Lock)
        {
            var channel = AllocateChannelGroup();
            var listener = new SteamListener(_localUser, channel, sessionId ?? string.Empty, this);
            _listeners.Add(listener);
            return listener;
        }
    }

    public void GetSupportedSchemes(List<string> schemes)
    {
        if (schemes != null) schemes.Add(SCHEME);
    }

    public bool SupportsScheme(string scheme)
        => string.Equals(scheme, SCHEME, StringComparison.OrdinalIgnoreCase);

    public List<Uri> GetPrioritizedUriList(IEnumerable<Uri> uris, out string expectedSessionId)
    {
        var result = new List<Uri>();
        expectedSessionId = null!;
        if (uris == null) return result;
        foreach (var uri in uris)
        {
            if (uri == null || !SupportsScheme(uri.Scheme)) continue;
            ParseUri(uri, out _, out _, out var sessionId);
            expectedSessionId ??= sessionId;
            result.Add(uri);
        }
        return result;
    }

    // The pump thread drives the link, so what arrives comes off the wire within a millisecond or two whatever
    // the world thread is doing. Driven from the frame, a long frame left everything sitting in the library,
    // and at a few MB/s its receive buffer fills and drops. Update only pumps when that thread could not
    // start. -xlinka
    public void Update()
    {
        if (!_initialized || _pump != null) return;
        Pump();
    }

    private void StartPump()
    {
        if (_pump != null) return;
        _pumpStop = false;
        _pump = new Thread(PumpLoop)
        {
            Name = "SteamNetworkPump",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _pump.Start();
    }

    private void StopPump()
    {
        var pump = _pump;
        if (pump == null) return;
        _pumpStop = true;
        if (pump != Thread.CurrentThread) pump.Join(1000);
        _pump = null;
    }

    // Optimized up front: a loop that never returns is otherwise swapped for an optimized copy mid-run, compiled
    // on this thread while every peer waits on it.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void PumpLoop()
    {
        while (!_pumpStop)
        {
            try
            {
                Pump();
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"SteamNetworkManager: pump threw: {ex}");
            }
            Thread.Sleep(1);
        }
    }

    private void Pump()
    {
        lock (Lock)
        {
            while (_toTransmit.TryDequeue(out var msg)) TransmitInternal(msg);
            while (_toDispose.Count > 0) _toDispose.Dequeue().Dispose();
            // What a full send buffer held back goes on first, ahead of anything new. By index from the end: a
            // flush or a receive that finds a connection dead closes it, which takes it out of the list.
            for (int i = _listeners.Count - 1; i >= 0; i--) _listeners[i].FlushWaiting();
            for (int i = _connections.Count - 1; i >= 0; i--) _connections[i].FlushWaiting();
            for (int i = _listeners.Count - 1; i >= 0; i--) _listeners[i].Update(_messageBuffer);
            for (int i = _connections.Count - 1; i >= 0; i--) _connections[i].Update(_messageBuffer);

            double now = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
            if (now >= _nextPace)
            {
                _nextPace = now + PaceIntervalMs;
                for (int i = _listeners.Count - 1; i >= 0; i--) _listeners[i].Pace(now);
                for (int i = _connections.Count - 1; i >= 0; i--) _connections[i].Pace(now);
            }

            if ((DateTime.UtcNow - _lastStatUpdate).TotalSeconds >= 1.0)
            {
                _lastStatUpdate = DateTime.UtcNow;
                foreach (var listener in _listeners) listener.UpdateStats();
                foreach (var connection in _connections) connection.UpdateStats();
            }
        }

        for (int i = 0; i < _arrived.Count; i++)
        {
            var (connection, data, length) = _arrived[i];
            try
            {
                connection.Deliver(data, length);
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"SteamNetworkManager: a receive handler on {connection.Identifier} threw: {ex}");
            }
        }
        _arrived.Clear();
    }

    // Under the lock, from a receive.
    internal void Arrived(SteamConnection connection, byte[] data, int length) => _arrived.Add((connection, data, length));

    public void Stop()
    {
        StopPump();
        lock (Lock)
        {
            for (int i = _listeners.Count - 1; i >= 0; i--) _listeners[i].Close();
            for (int i = _connections.Count - 1; i >= 0; i--) _connections[i].Close();
            while (_toDispose.Count > 0) _toDispose.Dequeue().Dispose();
            _listeners.Clear();
            _connections.Clear();
            _listenerMap.Clear();
            _connectionMap.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _statusCallback?.Dispose();
        _statusCallback = null!;
    }

    internal void RegisterListener(HSteamListenSocket socket, SteamListener listener) => _listenerMap[socket] = listener;

    internal void RegisterConnection(HSteamNetConnection connection, SteamConnection peer)
        => _connectionMap[connection.m_HSteamNetConnection] = peer;

    internal void UnregisterListener(SteamListener listener)
    {
        foreach (var s in listener.Listeners) _listenerMap.Remove(s);
        _listeners.Remove(listener);
        _toDispose.Enqueue(listener);
    }

    internal void UnregisterConnection(SteamConnection connection)
    {
        foreach (var handle in connection.Connections) _connectionMap.Remove(handle.m_HSteamNetConnection);
        _connections.Remove(connection);
        _toDispose.Enqueue(connection);
    }

    internal int AllocateChannelGroup() => _channelPool++;

    // Stored in session metadata for clients to dial back through the relay. - xlinka
    public static Uri BuildUri(CSteamID host, int channelGroup, string sessionId)
        => new($"{SCHEME}://{host.m_SteamID}/{channelGroup}/{sessionId}");

    private static void ParseUri(Uri uri, out CSteamID user, out int channelGroup, out string sessionId)
    {
        if (!string.Equals(uri.Scheme, SCHEME, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Not a Steam URI: {uri}");
        user = new CSteamID(ulong.Parse(uri.Host));
        channelGroup = int.Parse(uri.Segments[1].Trim('/'));
        sessionId = uri.Segments.Length > 2 ? uri.Segments[2].Trim('/') : string.Empty;
    }

    // Every connection starts at SteamPace's opening rate and is paced from there. The receive limits are
    // only set where this Steam client knows them: an option it does not know fails the whole connect.
    private static SteamNetworkingConfigValue_t[] BuildConfig(int sendBuffer)
    {
        int startRate = (int)SteamPace.StartRate;
        var config = new List<SteamNetworkingConfigValue_t>
        {
            ConfigInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, sendBuffer),
            ConfigInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, startRate),
            ConfigInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, startRate),
            ConfigInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutInitial, InitialTimeoutMs),
            ConfigInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected, ConnectedTimeoutMs),
        };
        if (Known(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_RecvBufferSize))
            config.Add(ConfigInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_RecvBufferSize, RecvBufferSize));
        if (Known(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_RecvBufferMessages))
            config.Add(ConfigInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_RecvBufferMessages, RecvBufferMessages));
        return config.ToArray();
    }

    private static bool Known(ESteamNetworkingConfigValue key)
    {
        try
        {
            return !string.IsNullOrEmpty(SteamNetworkingUtils.GetConfigValueInfo(key, out _, out _));
        }
        catch
        {
            return false;
        }
    }

    private static SteamNetworkingConfigValue_t ConfigInt(ESteamNetworkingConfigValue key, int value)
    {
        var v = new SteamNetworkingConfigValue_t
        {
            m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
            m_eValue = key,
        };
        v.m_val.m_int32 = value;
        return v;
    }

    private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t info)
    {
        lock (Lock)
        {
            if (_listenerMap.TryGetValue(info.m_info.m_hListenSocket, out var listener))
            {
                listener.ConnectionStatusChanged(ref info);
                return;
            }
            if (_connectionMap.TryGetValue(info.m_hConn.m_HSteamNetConnection, out var connection))
            {
                connection.ConnectionStatusChanged(ref info);
                return;
            }
            LumoraLogger.Log(
                $"SteamNetworkManager: unrouted status {info.m_info.m_eState} conn={info.m_hConn} remote={info.m_info.m_identityRemote.GetSteamID()}");
        }
    }

    private void TransmitInternal(RawOutMessage msg)
    {
        var pinned = GCHandle.Alloc(msg.Data, GCHandleType.Pinned);
        try
        {
            var ptr = Marshal.UnsafeAddrOfPinnedArrayElement(msg.Data, 0);
            foreach (var target in msg.Targets)
            {
                if (target is SteamConnection sc) sc.Send(msg.Data, msg.Length, msg.UseReliable, msg.Background);
                // Non-Steam targets are routed via their own IConnection.Send;
                // we ignore them here to avoid double-sending. - xlinka
                _ = ptr;
            }
        }
        finally
        {
            pinned.Free();
        }
        msg.TransmissionFinished();
    }

    internal static void ExtractMessages(
        int count,
        IntPtr[] messages,
        SteamMessageHandler messageHandler,
        SteamConnectionFailureHandler failureHandler,
        ReassemblyStateFetcher reassemblyFetcher)
    {
        HashSet<uint> aborted = null!;
        void Abort(uint handle)
        {
            failureHandler(handle);
            aborted ??= new HashSet<uint>();
            aborted.Add(handle);
        }

        for (int i = 0; i < count; i++)
        {
            var msgPtr = messages[i];
            var raw = Marshal.PtrToStructure<SteamNetworkingMessage_t>(msgPtr);

            uint handle = raw.m_conn.m_HSteamNetConnection;
            if (aborted != null && aborted.Contains(handle))
            {
                SteamNetworkingMessage_t.Release(msgPtr);
                continue;
            }

            try
            {
                ref var state = ref reassemblyFetcher(handle);
                bool isReliable = (raw.m_nFlags & Constants.k_nSteamNetworkingSend_Reliable) != 0;
                int payloadSize = raw.m_cbSize;

                switch (SteamFraming.Classify(state, isReliable, payloadSize))
                {
                    case SteamFraming.Arrival.Prefix:
                        if (!SteamFraming.Begin(ref state, Marshal.ReadInt32(raw.m_pData)))
                        {
                            Abort(handle);
                            continue;
                        }
                        break;

                    case SteamFraming.Arrival.Piece:
                        int at = SteamFraming.Place(ref state, payloadSize);
                        if (at < 0)
                        {
                            SteamFraming.Reset(ref state);
                            Abort(handle);
                            continue;
                        }
                        Marshal.Copy(raw.m_pData, state.buffer, at, payloadSize);
                        if (SteamFraming.IsComplete(state))
                        {
                            var frame = SteamFraming.Take(ref state, out int length);
                            messageHandler(raw.m_identityPeer.GetSteamID().m_SteamID, handle, frame, length);
                        }
                        break;

                    default:
                        var data = new byte[payloadSize];
                        Marshal.Copy(raw.m_pData, data, 0, payloadSize);
                        messageHandler(raw.m_identityPeer.GetSteamID().m_SteamID, handle, data, payloadSize);
                        break;
                }
            }
            finally
            {
                // Every message taken off the poll group is released, whatever the handler did with it; one
                // that threw used to leave the rest of the batch held by Steam for good. -xlinka
                SteamNetworkingMessage_t.Release(msgPtr);
            }
        }
    }
}
#endif
