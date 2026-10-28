// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.
// Steam is a desktop-only transport. The Steamworks binding has no Android build, and a type that
// merely MENTIONS a Steamworks type cannot be loaded there: the whole engine init failed on a Pico 4
// with 'Could not load type of field SteamNetworkManager:_listenerMap', long before any runtime
// 'is this Android' check could run. Compile the transport out on mobile entirely. -xlinka
#if GODOT_PC

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using Steamworks;
using Lumora.Nexus.Transport;
using LumoraLogger = Lumora.Core.Logging.Logger;
using System.Threading;

namespace Lumora.Godot.Networking.Transports.Steam;

// One peer connection over SteamNetworkingSockets. Maintains two channels (foreground +
// background) so high-priority frames don't queue behind bulk asset transfers - same shape as the
// LNL transport's reliable/sequenced channel split. Handles >512KB reliable payloads via
// length-prefix + fragment reassembly; small frames go straight through. - xlinka
public sealed class SteamConnection : IConnection
{
    private const int ChannelCount = 2;
    // About what one of Steam's packets carries, for counting a backlog in packets.
    private const int SteamPacketBytes = 1200;
    // The live channel's changes go the moment they are handed over: Nagle held each one back up to 5 ms for
    // company that is a whole sync cycle away. Bulk fills its packets anyway and keeps it. -xlinka
    private static readonly int[] SendFlagsReliable =
    {
        Constants.k_nSteamNetworkingSend_ReliableNoNagle,
        Constants.k_nSteamNetworkingSend_Reliable,
    };
    // Unreliable is streams: voice and tracking. A pose or a voice frame that cannot go out soon is worth
    // nothing by the time it would, so the library drops it instead of queueing it behind the rest.
    private const int SendFlagsUnreliable = Constants.k_nSteamNetworkingSend_UnreliableNoDelay;

    private enum State { Connecting, Open, Closed }

    private readonly SteamNetworkManager _manager;
    private readonly CSteamID _remoteId;
    private readonly int _remoteChannelGroup;
    private readonly string _sessionId;

    private readonly HSteamNetConnection[] _connections = new HSteamNetConnection[ChannelCount];
    private readonly bool[] _connectionsConnected = new bool[ChannelCount];
    private readonly ReassemblyState[] _reassembly = new ReassemblyState[ChannelCount];
    // Where a message from a handle this connection does not hold is collected: never delivered, and
    // started over for every such message. -xlinka
    private ReassemblyState _stray;
    // A frame's length and its pieces leave as one run. The encode thread and the join handshake both send
    // on a connection, and a reliable message from one landing between another's pieces would be taken for
    // a piece. -xlinka
    private readonly object _sendLock = new();
    // Reliable data that found a channel's send buffer full, waiting its turn on that channel.
    private readonly SteamSendQueue[] _waiting = { new(), new() };
    private readonly SteamSendQueue.Transmit[] _transmit = new SteamSendQueue.Transmit[ChannelCount];
    private long _droppedUnreliable;
    private DateTime _lastBehindLog;
    private HSteamNetPollGroup _pollGroup;
    // Why a send found the connection gone, for the close that follows it.
    private SteamCloseReason _sendFailure = SteamCloseReason.TransmissionError;

    // Bytes Steam took on each channel, reliable and unreliable apart; what of them got through paces the link.
    private readonly long[] _reliableHanded = new long[ChannelCount];
    private readonly long[] _unreliableHanded = new long[ChannelCount];
    private readonly SteamPace _pace = new();
    private readonly int[] _appliedRate = new int[ChannelCount];
    private IntPtr _rateScratch;

    private State _state;
    private ulong _receivedBytes;
    private SteamNetConnectionRealTimeStatus_t _lastMainStatus;
    private SteamNetConnectionRealTimeStatus_t _lastBackgroundStatus;
    private double _packetsSent;
    private double _packetsLost;
    private long _statsAt;

    public bool IsOpen => _state == State.Open;
    public string FailReason { get; private set; } = string.Empty;
    public SteamCloseReason CloseReason { get; private set; }
    public IPAddress IP => null!;
    public Uri Address { get; internal set; } = null!;
    public string Identifier { get; }
    public ulong ReceivedBytes => _receivedBytes;
    private long _sentBytes;
    public ulong SentBytes => (ulong)Interlocked.Read(ref _sentBytes);
    // m_nPing is refreshed every transport Update via UpdateStats. Steam Datagram Relay is always
    // end-to-end encrypted, so IsEncrypted is unconditionally true once open. -xlinka
    public int Ping => IsOpen ? _lastMainStatus.m_nPing : -1;

    // What waits here plus what Steam holds unsent on the live channel: what the next change sent waits behind.
    // The bulk channel is paced under the live one and only gets what it leaves, so asset chunks queued there
    // hold up nothing the world sends. Counted, every transfer read as a peer far behind and the host withheld
    // that peer's moving values for as long as it ran. -xlinka
    public long PendingSendBytes
    {
        get
        {
            if (!IsOpen) return 0;
            long pending = _waiting[0].Bytes;
            SteamNetConnectionRealTimeLaneStatus_t lanes = default;
            var status = default(SteamNetConnectionRealTimeStatus_t);
            if (SteamNetworkingSockets.GetConnectionRealTimeStatus(_connections[0], ref status, 0, ref lanes) == EResult.k_EResultOK)
                pending += status.m_cbPendingReliable + status.m_cbPendingUnreliable;
            return pending;
        }
    }

    public long DroppedUnreliable => Interlocked.Read(ref _droppedUnreliable);

    // Steam counts no resends; it reports the share of our packets the far end got, which is the same loss.
    public long PacketsSent => (long)_packetsSent;
    public long PacketsLost => (long)_packetsLost;

    // What waits here and what Steam holds unsent, both channels, in packets of its size.
    public int QueuedPackets
    {
        get
        {
            if (!IsOpen) return 0;
            long bytes = _waiting[0].Bytes + _waiting[1].Bytes
                + _lastMainStatus.m_cbPendingReliable + _lastMainStatus.m_cbPendingUnreliable
                + _lastBackgroundStatus.m_cbPendingReliable + _lastBackgroundStatus.m_cbPendingUnreliable;
            return (int)Math.Min(int.MaxValue, (bytes + SteamPacketBytes - 1) / SteamPacketBytes);
        }
    }
    public bool IsEncrypted => IsOpen;
    public string TransportName => "Steam";

    public CSteamID RemoteId => _remoteId;

    public event Action<IConnection> Connected = null!;
    public event Action<IConnection> ConnectionFailed = null!;
    public event Action<IConnection> Closed = null!;
    public event Action<byte[], int> DataReceived = null!;

    internal IEnumerable<HSteamNetConnection> Connections
    {
        get
        {
            for (int i = 0; i < _connections.Length; i++)
            {
                var handle = _connections[i];
                if (handle != HSteamNetConnection.Invalid) yield return handle;
            }
        }
    }

    internal SteamConnection(CSteamID remoteId, int remoteChannelGroup, string sessionId, SteamNetworkManager manager)
    {
        _remoteId = remoteId;
        _remoteChannelGroup = remoteChannelGroup;
        _sessionId = sessionId;
        _manager = manager;
        Identifier = "Steam:" + remoteId.m_SteamID.ToString(CultureInfo.InvariantCulture);
        for (int i = 0; i < ChannelCount; i++)
        {
            int channel = i;
            _transmit[i] = (data, offset, count) => Push(channel, data, offset, count, SendFlagsReliable[channel]);
            _appliedRate[i] = (int)SteamPace.StartRate;
        }
        _pollGroup = SteamNetworkingSockets.CreatePollGroup();
        for (int i = 0; i < ChannelCount; i++)
        {
            _reassembly[i].buffer = null!;
            _reassembly[i].expectingBytes = -1;
            _reassembly[i].receivedBytes = -1;
        }
    }

    public void Connect(Action<string> statusCallback)
    {
        statusCallback?.Invoke($"SteamNetworkingSockets P2P to {_remoteId.m_SteamID}");
        var identity = default(SteamNetworkingIdentity);
        identity.SetSteamID64(_remoteId.m_SteamID);

        lock (_manager.Lock)
        {
            int basePort = _remoteChannelGroup * ChannelCount;
            for (int i = 0; i < ChannelCount; i++)
            {
                var config = _manager.GetConfig(i);
                _connections[i] = SteamNetworkingSockets.ConnectP2P(ref identity, basePort + i, config.Length, config);
                SteamNetworkingSockets.SetConnectionPollGroup(_connections[i], _pollGroup);
                _manager.RegisterConnection(_connections[i], this);
            }
        }
    }

    // polled centrally by the manager - xlinka
    public void Poll() { }

    public void Send(byte[] data, int length, bool reliable, bool background)
    {
        if (!IsOpen) return;
        if (length <= 0) return;
        Interlocked.Add(ref _sentBytes, length);

        bool alive;
        lock (_sendLock)
            alive = TransmitInternal(data, length, reliable, background);
        // Closed outside the send lock: Close takes the manager's lock, and the pump holds that one while it
        // flushes, where it only ever tries this lock.
        if (!alive)
            Close(_sendFailure);
    }

    // Hands on what waits, from the manager's pump. A send under way on another thread flushes for itself.
    internal void FlushWaiting()
    {
        if (!IsOpen || (_waiting[0].Count == 0 && _waiting[1].Count == 0)) return;
        if (!Monitor.TryEnter(_sendLock)) return;
        bool alive = true;
        try
        {
            for (int i = 0; i < ChannelCount && alive; i++)
            {
                if (_waiting[i].Count > 0)
                    alive = _waiting[i].Flush(_transmit[i]) != SteamSendQueue.Outcome.Dead;
            }
        }
        finally
        {
            Monitor.Exit(_sendLock);
        }
        if (!alive)
            Close(_sendFailure);
    }

    // From the manager's pump, a few times a second: the link's rate from what got through.
    internal void Pace(double nowMs)
    {
        if (!IsOpen) return;
        SteamNetConnectionRealTimeLaneStatus_t lanes = default;
        var live = default(SteamNetConnectionRealTimeStatus_t);
        var bulk = default(SteamNetConnectionRealTimeStatus_t);
        if (SteamNetworkingSockets.GetConnectionRealTimeStatus(_connections[0], ref live, 0, ref lanes) != EResult.k_EResultOK
            || SteamNetworkingSockets.GetConnectionRealTimeStatus(_connections[1], ref bulk, 0, ref lanes) != EResult.k_EResultOK)
            return;

        long handed = Interlocked.Read(ref _reliableHanded[0]) + Interlocked.Read(ref _reliableHanded[1]);
        long acked = handed - (live.m_cbPendingReliable + live.m_cbSentUnackedReliable + bulk.m_cbPendingReliable + bulk.m_cbSentUnackedReliable);
        long unreliableOut = Interlocked.Read(ref _unreliableHanded[0]) + Interlocked.Read(ref _unreliableHanded[1])
            - live.m_cbPendingUnreliable - bulk.m_cbPendingUnreliable;
        long queued = _waiting[0].Bytes + _waiting[1].Bytes
            + live.m_cbPendingReliable + live.m_cbPendingUnreliable + bulk.m_cbPendingReliable + bulk.m_cbPendingUnreliable;
        _pace.Update(nowMs, acked, unreliableOut, queued, System.Math.Max(live.m_nPing, bulk.m_nPing));
        bool bulkWaiting = _waiting[1].Bytes + bulk.m_cbPendingReliable + bulk.m_cbPendingUnreliable > 0;
        bool liveWaiting = _waiting[0].Bytes + live.m_cbPendingReliable + live.m_cbPendingUnreliable > 0;
        SetRate(0, _pace.LiveRate(bulkWaiting));
        SetRate(1, _pace.BulkRate(live.m_flOutBytesPerSec, liveWaiting));
    }

    private void SetRate(int channel, int rate)
    {
        // A change under a couple of percent is not worth a call into the library.
        int applied = _appliedRate[channel];
        if (System.Math.Abs(rate - applied) <= applied / 64) return;
        if (_rateScratch == IntPtr.Zero) _rateScratch = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(_rateScratch, rate);
        var handle = (IntPtr)_connections[channel].m_HSteamNetConnection;
        bool set = SteamNetworkingUtils.SetConfigValue(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin,
            ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, handle, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, _rateScratch);
        set &= SteamNetworkingUtils.SetConfigValue(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax,
            ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, handle, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, _rateScratch);
        if (set) _appliedRate[channel] = rate;
    }

    public void Close() => Close(SteamCloseReason.ClosedLocally);

    internal void Close(SteamCloseReason reason)
    {
        if (_state == State.Closed) return;
        lock (_manager.Lock)
        {
            if (_state == State.Closed) return;
            bool wasOpen = _state == State.Open;
            _state = State.Closed;
            CloseReason = reason;
            FailReason = reason.ToString();
            _manager.UnregisterConnection(this);
            if (!wasOpen)
            {
                ConnectionFailed?.Invoke(this);
            }
            Closed?.Invoke(this);
        }
    }

    public void Dispose()
    {
        lock (_sendLock)
        {
            _waiting[0].Clear();
            _waiting[1].Clear();
        }
        // A close of our own lingers until what is already queued is through, so the last thing said (a
        // kick, a leave) still arrives; one the peer or a failure caused has nobody left to hear it.
        bool linger = CloseReason == SteamCloseReason.ClosedLocally;
        for (int i = 0; i < _connections.Length; i++)
        {
            var handle = _connections[i];
            if (handle != HSteamNetConnection.Invalid)
            {
                SteamNetworkingSockets.CloseConnection(handle, (int)CloseReason, null, linger);
                _connections[i] = default;
            }
        }
        if (_pollGroup != default)
        {
            SteamNetworkingSockets.DestroyPollGroup(_pollGroup);
            _pollGroup = default;
        }
        if (_rateScratch != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_rateScratch);
            _rateScratch = IntPtr.Zero;
        }
    }

    // Listener calls this after AcceptConnection to bind a channel handle to this peer. Returns
    // false if the handle/channel combination is invalid. - xlinka
    internal bool AssignConnection(HSteamNetConnection connection, int channel)
    {
        if (_state != State.Connecting) return false;
        if (channel < 0 || channel >= ChannelCount) return false;
        if (_connectionsConnected[channel]) return false;
        _connections[channel] = connection;
        _connectionsConnected[channel] = true;
        SteamNetworkingSockets.SetConnectionPollGroup(connection, _pollGroup);
        if (AllConnected()) _state = State.Open;
        return true;
    }

    internal void Update(IntPtr[] buffer)
    {
        try
        {
            for (int batch = 0; batch < SteamNetworkManager.MaxReceiveBatchesPerFrame && IsOpen; batch++)
            {
                int count = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(_pollGroup, buffer, buffer.Length);
                SteamNetworkManager.ExtractMessages(count, buffer, OnMessage, OnReceiveError, GetReassembly);
                if (count < buffer.Length) break;
            }
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"SteamConnection.Update: {ex}");
            Close(SteamCloseReason.UnhandledException);
        }
    }

    internal void UpdateStats()
    {
        if (!IsOpen) return;
        // nLanes=0 so we skip per-lane status; we only care about overall ping
        // / send rate / pending bytes. pLanes still has to be a valid ref  - 
        // pass a stack-local sentinel. - xlinka
        SteamNetConnectionRealTimeLaneStatus_t lanes = default;
        SteamNetworkingSockets.GetConnectionRealTimeStatus(_connections[0], ref _lastMainStatus, 0, ref lanes);
        SteamNetworkingSockets.GetConnectionRealTimeStatus(_connections[1], ref _lastBackgroundStatus, 0, ref lanes);

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_statsAt != 0)
        {
            double seconds = (now - _statsAt) / (double)System.Diagnostics.Stopwatch.Frequency;
            CountLoss(_lastMainStatus, seconds);
            CountLoss(_lastBackgroundStatus, seconds);
        }
        _statsAt = now;
    }

    private void CountLoss(in SteamNetConnectionRealTimeStatus_t status, double seconds)
    {
        // Quality is unknown (below zero) until the far end has reported.
        if (status.m_flConnectionQualityRemote < 0 || status.m_flOutPacketsPerSec <= 0)
            return;
        double sent = status.m_flOutPacketsPerSec * seconds;
        _packetsSent += sent;
        _packetsLost += sent * (1 - Math.Clamp(status.m_flConnectionQualityRemote, 0f, 1f));
    }

    internal ref ReassemblyState GetReassembly(uint connectionHandle)
    {
        for (int i = 0; i < _connections.Length; i++)
        {
            if (_connections[i].m_HSteamNetConnection == connectionHandle) return ref _reassembly[i];
        }
        // Throwing here left the rest of the poll batch unreleased and closed the connection over a message
        // that is dropped anyway. -xlinka
        SteamFraming.Reset(ref _stray);
        return ref _stray;
    }

    private void OnReceiveError(uint connectionHandle)
    {
        for (int i = 0; i < _connections.Length; i++)
        {
            if (_connections[i].m_HSteamNetConnection == connectionHandle)
            {
                Close(SteamCloseReason.ReceiveError);
                return;
            }
        }
    }

    internal void OnMessage(ulong senderId, uint connectionHandle, byte[] data, int length)
    {
        if (!IsOpen) return;
        if (GetChannel(connectionHandle) < 0)
        {
            LumoraLogger.Warn($"SteamConnection: invalid channel for sender {senderId}, handle {connectionHandle}, size {length}");
            return;
        }
        _receivedBytes += (ulong)length;
        _manager.Arrived(this, data, length);
    }

    // The pump, after it lets go of the manager's lock.
    internal void Deliver(byte[] data, int length)
    {
        if (IsOpen)
            DataReceived?.Invoke(data, length);
    }

    internal void ConnectionStatusChanged(ref SteamNetConnectionStatusChangedCallback_t info)
    {
        if (_state == State.Closed) return;
        switch (info.m_info.m_eState)
        {
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                if (_state == State.Open)
                {
                    Close(SteamCloseReason.ChannelMismatch);
                    return;
                }
                int channel = GetChannel(info.m_hConn.m_HSteamNetConnection);
                if (channel < 0)
                {
                    Close(SteamCloseReason.ChannelMismatch);
                    return;
                }
                _connectionsConnected[channel] = true;
                if (AllConnected())
                {
                    _state = State.Open;
                    Connected?.Invoke(this);
                }
                break;

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                Close(SteamCloseReason.ClosedRemotely);
                break;

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                LumoraLogger.Warn($"SteamConnection: local problem, end={info.m_info.m_eEndReason}, debug={info.m_info.m_szEndDebug}");
                Close(SteamCloseReason.LocalProblem);
                break;
        }
    }

    private bool AllConnected()
    {
        for (int i = 0; i < _connectionsConnected.Length; i++)
        {
            if (!_connectionsConnected[i]) return false;
        }
        return true;
    }

    internal int GetChannel(uint connectionHandle)
    {
        for (int i = 0; i < _connections.Length; i++)
        {
            if (_connections[i].m_HSteamNetConnection == connectionHandle) return i;
        }
        return -1;
    }

    // False once the connection is gone. A full buffer is not that: the link is behind, so reliable data waits
    // in order and unreliable data, the latest of something sent again soon, is let go. An unreliable frame
    // bigger than the library sends unreliably would go reliably anyway, so it goes as a reliable frame of ours,
    // behind whatever waits on the channel instead of between a held frame's pieces.
    private bool TransmitInternal(byte[] data, int count, bool reliable, bool background)
    {
        int channel = background ? 1 : 0;
        if (!reliable && count <= SteamFraming.MaxUnreliableSize)
        {
            var outcome = Push(channel, data, 0, count, SendFlagsUnreliable);
            if (outcome == SteamSendQueue.Outcome.Full)
                Interlocked.Increment(ref _droppedUnreliable);
            return outcome != SteamSendQueue.Outcome.Dead;
        }

        var queue = _waiting[channel];
        var transmit = _transmit[channel];
        bool wasWaiting = queue.Count > 0;
        if (SteamFraming.NeedsPrefix(count, true))
        {
            // The 4-byte total length first, so the receiver can allocate one contiguous buffer, then the
            // payload in message-sized pieces. The pieces wait in the same queue, so no other frame splits them.
            var sizeBytes = BitConverter.GetBytes((uint)count);
            if (queue.Send(sizeBytes, 0, sizeBytes.Length, transmit) == SteamSendQueue.Outcome.Dead)
                return false;
            for (int at = 0; at < count; at += SteamFraming.MaxMessageSize)
            {
                int chunk = Math.Min(SteamFraming.MaxMessageSize, count - at);
                if (queue.Send(data, at, chunk, transmit) == SteamSendQueue.Outcome.Dead)
                    return false;
            }
        }
        else if (queue.Send(data, 0, count, transmit) == SteamSendQueue.Outcome.Dead)
        {
            return false;
        }

        if (!wasWaiting && queue.Count > 0 && (DateTime.UtcNow - _lastBehindLog).TotalSeconds >= 10)
        {
            _lastBehindLog = DateTime.UtcNow;
            LumoraLogger.Log($"SteamConnection: {Identifier} is behind, holding {queue.Bytes}B on channel {channel} (peak {queue.PeakBytes}B, {DroppedUnreliable} unreliable let go), paced at {_pace.Rate / 1024:0} KB/s");
        }
        return true;
    }

    private SteamSendQueue.Outcome Push(int channel, byte[] data, int offset, int count, int flags)
    {
        var handle = _connections[channel];
        EResult result;
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            result = SteamNetworkingSockets.SendMessageToConnection(handle, pinned.AddrOfPinnedObject() + offset, (uint)count, flags, out _);
        }
        finally
        {
            pinned.Free();
        }

        switch (result)
        {
            case EResult.k_EResultOK:
                if ((flags & Constants.k_nSteamNetworkingSend_Reliable) != 0)
                    Interlocked.Add(ref _reliableHanded[channel], count);
                else
                    Interlocked.Add(ref _unreliableHanded[channel], count);
                return SteamSendQueue.Outcome.Sent;
            case EResult.k_EResultIgnored:
                return SteamSendQueue.Outcome.Sent;
            case EResult.k_EResultLimitExceeded:
                return SteamSendQueue.Outcome.Full;
            default:
                SteamNetworkingSockets.GetConnectionInfo(handle, out var info);
                // A peer that hung up shows up here first when a send beats its status callback, which only
                // arrives with the next frame's callbacks. That is a close, not a fault.
                if (info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer)
                {
                    _sendFailure = SteamCloseReason.ClosedRemotely;
                    LumoraLogger.Log($"SteamConnection: {Identifier} closed by the peer, end={info.m_eEndReason}");
                }
                else
                {
                    _sendFailure = info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally
                        ? SteamCloseReason.LocalProblem
                        : SteamCloseReason.TransmissionError;
                    LumoraLogger.Warn(
                        $"SteamConnection: send {count}B failed, result={result}, state={info.m_eState}, end={info.m_eEndReason}");
                }
                return SteamSendQueue.Outcome.Dead;
        }
    }

    public override string ToString()
        => $"SteamConnection({_remoteId.m_SteamID}, state={_state}, handles=[{string.Join(",", _connections)}])";
}
#endif
