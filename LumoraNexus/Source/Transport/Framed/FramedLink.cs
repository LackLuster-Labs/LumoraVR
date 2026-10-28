// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Nexus.Transport.LNL;
using LumoraLogger = Lumora.Nexus.Diagnostics.NexusLog;

namespace Lumora.Nexus.Transport.Framed;

// A session link over our stream transports. Both ends say hello with the app id first, so a mismatched build is
// turned away by name; then the same crypto handshake LNL runs, so everything after it is end to end, through a
// node or not. Sends are encrypted into pooled buffers and queued on their lane's leg; arrivals are put back
// together per lane, decrypted and handed up one at a time, whichever leg they came on. Each side pings every
// second; ten seconds of silence closes the link, as on LNL. -xlinka
public abstract class FramedLink : IConnection
{
    private const int PingEveryMs = 1000;
    public const int SilenceTimeoutMs = 10000;
    private const int HandshakeTimeoutMs = 10000;
    private const int CryptoOverhead = 32;
    private const int KeptAssemblyBytes = 256 * 1024;

    private readonly LNLCryptoSession _crypto;
    private readonly bool _isClient;
    private readonly string _appId;
    private readonly object _stateGate = new();
    private readonly object _receiveGate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<FramedLeg> _legs = new();
    private readonly FramedLeg?[] _legOfLane = new FramedLeg?[FramedWire.MaxLanes];
    private readonly byte[]?[] _assembly = new byte[]?[FramedWire.MaxLanes];
    private readonly int[] _assembled = new int[FramedWire.MaxLanes];
    private byte[] _bulkLanes = Array.Empty<byte>();
    private int _nextBulk;
    private volatile bool _helloHeard;
    private volatile bool _established;
    private volatile bool _closed;
    private volatile bool _open;
    private long _lastHeardMs;
    private long _openedMs;
    private double _rttMs = -1;
    private long _sentBytes;
    private long _receivedBytes;
    private long _ackedReceived;
    private bool _loggedNotOpen;
    private readonly FramedPacer? _pacer;

    // paced: the link keeps its unconfirmed bytes near one round trip of the path.
    protected FramedLink(bool isClient, string appId, Uri address, string identifier, bool paced = false)
    {
        _isClient = isClient;
        _appId = appId;
        _pacer = paced ? new FramedPacer() : null;
        _crypto = new LNLCryptoSession(isClient);
        Address = address;
        Identifier = identifier;
    }

    public bool IsOpen => _open;
    public string FailReason { get; protected set; } = null!;
    public IPAddress IP { get; protected set; } = null!;
    public Uri Address { get; protected set; }
    public string Identifier { get; protected set; }
    public ulong ReceivedBytes => (ulong)Interlocked.Read(ref _receivedBytes);
    public ulong SentBytes => (ulong)Interlocked.Read(ref _sentBytes);
    public int Ping => _rttMs < 0 ? -1 : (int)Math.Round(_rttMs);
    public bool IsEncrypted => _crypto.IsEstablished;
    public abstract string TransportName { get; }
    public bool ViaRelay { get; protected set; }
    public bool IsClosed => _closed;

    public long PacketsSent
    {
        get
        {
            long frames = 0;
            foreach (var leg in Legs())
                frames += leg.Outbox.Frames;
            return frames;
        }
    }

    // Unreliable messages dropped unsent behind a backlog. Not loss: the stream never loses anything once
    // written, and what the network drops is resent below us, out of sight.
    public long DroppedUnreliable
    {
        get
        {
            long dropped = 0;
            foreach (var leg in Legs())
                dropped += leg.Outbox.Dropped;
            return dropped;
        }
    }

    public int QueuedPackets
    {
        get
        {
            int queued = 0;
            foreach (var leg in Legs())
                queued += leg.Outbox.Queued;
            return queued;
        }
    }

    public long PendingSendBytes
    {
        get
        {
            long pending = 0;
            foreach (var leg in Legs())
                pending += leg.Outbox.PendingBytes;
            return pending;
        }
    }

    public event Action<IConnection> Closed = null!;
    public event Action<IConnection> Connected = null!;
    public event Action<IConnection> ConnectionFailed = null!;
    public event Action<byte[], int> DataReceived = null!;

    // Server side: the crypto handshake is done and the link can be handed to the session.
    internal Action<FramedLink>? Ready;

    protected CancellationToken Token => _cts.Token;

    private static long NowMs => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    private FramedLeg[] Legs()
    {
        lock (_legs)
            return _legs.ToArray();
    }

    // Legs are added before Begin; leg 0 carries the control frames and the foreground lane.
    private protected FramedLeg AddLeg(int index, IReadOnlyList<byte> lanes)
    {
        var leg = new FramedLeg(this, index, lanes);
        lock (_legs)
            _legs.Add(leg);
        var bulk = new List<byte>(_bulkLanes);
        foreach (byte lane in lanes)
        {
            _legOfLane[lane] = leg;
            if (lane >= FramedWire.FirstBulkLane && !bulk.Contains(lane))
                bulk.Add(lane);
        }
        _bulkLanes = bulk.ToArray();
        return leg;
    }

    private FramedLeg? ControlLeg => _legOfLane[FramedWire.LaneForeground];

    protected void Begin()
    {
        _openedMs = NowMs;
        Volatile.Write(ref _lastHeardMs, _openedMs);
        _ = Task.Run(KeepAliveAsync);
        if (_isClient)
            ControlLeg?.Outbox.EnqueueControl(FramedWire.Hello(_appId));
    }

    public abstract void Connect(Action<string> statusCallback);

    protected abstract void ShutdownTransport();

    public void Close() => CloseCore("closed here", sendClose: true);

    protected void Fail(string reason) => CloseCore(reason, sendClose: false);

    private void CloseCore(string reason, bool sendClose)
    {
        bool wasEstablished;
        lock (_stateGate)
        {
            if (_closed)
                return;
            _closed = true;
            wasEstablished = _established;
        }
        _open = false;
        FailReason ??= reason;
        var control = ControlLeg;
        foreach (var leg in Legs())
            leg.Outbox.Seal(sendClose && ReferenceEquals(leg, control) ? FramedWire.CloseFrame(reason) : null);
        _ = Task.Run(async () =>
        {
            if (sendClose && control != null)
                await Task.WhenAny(control.WriterDone, Task.Delay(300)).ConfigureAwait(false);
            _cts.Cancel();
            try
            {
                ShutdownTransport();
            }
            catch (Exception ex)
            {
                LumoraLogger.Debug($"[{TransportName}] {Identifier}: shutting down threw {ex.Message}");
            }
            _crypto.Dispose();
        });
        if (!wasEstablished)
            ConnectionFailed?.Invoke(this);
        Closed?.Invoke(this);
    }

    public void Dispose()
    {
        Close();
    }

    public void Poll()
    {
    }

    public void Send(byte[] data, int length, bool reliable, bool background)
        => SendOn(reliable ? (background ? FramedWire.LaneBackground : FramedWire.LaneForeground) : FramedWire.LaneUnreliable, data, length);

    public void SendBulk(byte[] data, int length) => SendOn(NextBulkLane(), data, length);

    private byte NextBulkLane()
    {
        var lanes = _bulkLanes;
        if (lanes.Length == 0)
            return FramedWire.LaneBackground;
        if (lanes.Length == 1)
            return lanes[0];
        byte best = lanes[0];
        long least = long.MaxValue;
        int start = _nextBulk;
        for (int i = 0; i < lanes.Length; i++)
        {
            byte lane = lanes[(start + i) % lanes.Length];
            long pending = _legOfLane[lane]?.Outbox.PendingBytes ?? long.MaxValue;
            if (pending < least)
            {
                least = pending;
                best = lane;
            }
        }
        _nextBulk = (start + 1) % lanes.Length;
        return best;
    }

    private void SendOn(byte lane, byte[] data, int length)
    {
        var leg = _legOfLane[lane];
        if (!_open || leg == null)
        {
            if (!_loggedNotOpen)
            {
                _loggedNotOpen = true;
                LumoraLogger.Debug($"[{TransportName}] Send to {Identifier} skipped - link not open");
            }
            return;
        }
        _loggedNotOpen = false;

        byte[]? scratch = ArrayPool<byte>.Shared.Rent(length + CryptoOverhead);
        byte[] output;
        int count;
        try
        {
            output = _crypto.EncryptReusing(data, length, ref scratch, out count);
        }
        catch (Exception ex)
        {
            if (scratch != null)
                ArrayPool<byte>.Shared.Return(scratch);
            LumoraLogger.Warn($"[{TransportName}] Send to {Identifier} failed before crypto was ready ({ex.Message})");
            return;
        }
        bool pooled = ReferenceEquals(output, scratch);
        if (!pooled && scratch != null)
            ArrayPool<byte>.Shared.Return(scratch);
        leg.Outbox.Enqueue(lane, output, count, pooled);
    }

    internal void CountSent(int bytes) => Interlocked.Add(ref _sentBytes, bytes);

    internal void CountReceived(int bytes)
    {
        long total = Interlocked.Add(ref _receivedBytes, bytes);
        if (_pacer == null || total - Interlocked.Read(ref _ackedReceived) < FramedPacer.AckEvery)
            return;
        Interlocked.Exchange(ref _ackedReceived, total);
        var payload = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(payload, total);
        ControlLeg?.Outbox.EnqueueControl(FramedWire.Control(FramedWire.KindAck, payload));
    }

    // Where a link has several legs, the foreground one may run a little past the window, so a backlog on another
    // leg that took the window first never holds it.
    private const long ForegroundReserve = 128 * 1024;

    private readonly object _takeGate = new();

    // The leg's next batch, counted as sent as it is taken, so legs writing at once never take the same room twice.
    internal int TakeFor(FramedLeg leg, byte[] batch)
    {
        if (_pacer == null)
        {
            int unpaced = leg.Outbox.Take(batch);
            CountSent(unpaced);
            return unpaced;
        }
        lock (_takeGate)
        {
            long allowance = _pacer.Allowance(Interlocked.Read(ref _sentBytes));
            if (leg.Index == 0 && _legs.Count > 1)
                allowance += ForegroundReserve;
            int count = leg.Outbox.Take(batch, allowance);
            CountSent(count);
            return count;
        }
    }

    internal void LegEnded(FramedLeg leg) => CloseCore("the other side closed the connection", sendClose: false);

    internal void LegFailed(FramedLeg leg, string reason)
    {
        if (!_closed)
            CloseCore(reason, sendClose: false);
    }

    // False ends the leg's reader.
    internal bool OnFrame(FramedLeg leg, byte kind, byte lane, byte[] buffer, int offset, int length)
    {
        Volatile.Write(ref _lastHeardMs, NowMs);
        if (_closed)
            return false;
        switch (kind)
        {
            case FramedWire.KindPart:
            case FramedWire.KindLast:
                if (!_helloHeard)
                    return Protocol("data before hello");
                if (lane >= FramedWire.MaxLanes)
                    return Protocol($"lane {lane}");
                return Assemble(lane, kind == FramedWire.KindLast, buffer, offset, length);
            case FramedWire.KindHello:
                return OnHello(buffer.AsSpan(offset, length));
            case FramedWire.KindPing:
                if (length == 8)
                    ControlLeg?.Outbox.EnqueueControl(FramedWire.Control(FramedWire.KindPong, buffer.AsSpan(offset, length)));
                return true;
            case FramedWire.KindPong:
                if (length == 8)
                {
                    long sent = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset, 8));
                    double sample = (Stopwatch.GetTimestamp() - sent) * 1000.0 / Stopwatch.Frequency;
                    if (sample >= 0 && sample < 60000)
                    {
                        _rttMs = _rttMs < 0 ? sample : _rttMs * 0.8 + sample * 0.2;
                        _pacer?.OnRtt(sample);
                    }
                }
                return true;
            case FramedWire.KindAck:
                if (length == 8 && _pacer != null && _pacer.OnAck(BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset, 8))))
                {
                    foreach (var each in Legs())
                        each.Outbox.Wake();
                }
                return true;
            case FramedWire.KindClose:
                var reason = FramedWire.ReadCloseReason(buffer.AsSpan(offset, length));
                FailReason = "the other side closed: " + reason;
                CloseCore(FailReason, sendClose: false);
                return false;
            default:
                return Protocol($"frame kind {kind}");
        }
    }

    private bool OnHello(ReadOnlySpan<byte> payload)
    {
        if (_helloHeard)
            return Protocol("second hello");
        if (!FramedWire.TryReadHello(payload, out byte version, out string appId))
            return Protocol("unreadable hello");
        if (version != FramedWire.Version || appId != _appId)
        {
            LumoraLogger.Warn($"[{TransportName}] {Identifier}: build mismatch (got '{appId}' v{version}, expected '{_appId}' v{FramedWire.Version})");
            FailReason = $"build mismatch: '{appId}' v{version}";
            CloseCore($"build mismatch: expected '{_appId}' v{FramedWire.Version}", sendClose: true);
            return false;
        }
        _helloHeard = true;
        SendPing();
        if (_isClient)
        {
            var clientHello = _crypto.CreateClientHello();
            _legOfLane[FramedWire.LaneForeground]?.Outbox.Enqueue(FramedWire.LaneForeground, clientHello, clientHello.Length, pooled: false);
        }
        else
        {
            ControlLeg?.Outbox.EnqueueControl(FramedWire.Hello(_appId));
        }
        return true;
    }

    private bool Assemble(byte lane, bool last, byte[] buffer, int offset, int length)
    {
        int limit = _crypto.IsEstablished ? FramedWire.MaxMessageBytes : FramedWire.MaxHandshakeMessageBytes;
        int have = _assembled[lane];
        if (have + length > limit)
            return Protocol($"message on lane {lane} over {limit} bytes");
        var into = _assembly[lane];
        if (into == null || into.Length < have + length)
        {
            int size = Math.Min(limit, Math.Max(have + length, Math.Max(4096, (into?.Length ?? 0) * 2)));
            var grown = new byte[size];
            if (into != null && have > 0)
                Buffer.BlockCopy(into, 0, grown, 0, have);
            _assembly[lane] = into = grown;
        }
        Buffer.BlockCopy(buffer, offset, into, have, length);
        have += length;
        if (!last)
        {
            _assembled[lane] = have;
            return true;
        }
        _assembled[lane] = 0;
        bool ok = Deliver(into, have);
        if (into.Length > KeptAssemblyBytes)
            _assembly[lane] = null;
        return ok;
    }

    private bool Deliver(byte[] data, int length)
    {
        byte[]? plaintext;
        bool nowEstablished = false;
        lock (_receiveGate)
        {
            bool wasEstablished = _crypto.IsEstablished;
            if (!_crypto.TryHandleIncoming(data, length, out plaintext, out var response))
                return Protocol("crypto handshake failed");
            if (response != null)
                _legOfLane[FramedWire.LaneForeground]?.Outbox.Enqueue(FramedWire.LaneForeground, response, response.Length, pooled: false);
            if (!wasEstablished && _crypto.IsEstablished)
            {
                lock (_stateGate)
                {
                    if (_closed)
                        return false;
                    _established = true;
                    _open = true;
                }
                nowEstablished = true;
            }

            if (nowEstablished)
            {
                LumoraLogger.Debug($"[{TransportName}] Crypto established with {Identifier}");
                if (_isClient)
                    Connected?.Invoke(this);
                else
                    Ready?.Invoke(this);
            }

            if (plaintext != null)
            {
                try
                {
                    DataReceived?.Invoke(plaintext, plaintext.Length);
                }
                catch (Exception ex)
                {
                    LumoraLogger.Warn($"[{TransportName}] {Identifier}: a receive handler threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        return true;
    }

    private bool Protocol(string what)
    {
        LumoraLogger.Warn($"[{TransportName}] Closing {Identifier}: {what}");
        CloseCore(what, sendClose: true);
        return false;
    }

    private void SendPing()
    {
        var stamp = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(stamp, Stopwatch.GetTimestamp());
        ControlLeg?.Outbox.EnqueueControl(FramedWire.Control(FramedWire.KindPing, stamp));
    }

    private async Task KeepAliveAsync()
    {
        var token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested && !_closed)
            {
                await Task.Delay(PingEveryMs, token).ConfigureAwait(false);
                long now = NowMs;
                if (!_established && now - _openedMs > HandshakeTimeoutMs)
                {
                    CloseCore("handshake timed out", sendClose: true);
                    return;
                }
                if (now - Volatile.Read(ref _lastHeardMs) > SilenceTimeoutMs)
                {
                    CloseCore($"nothing heard for {SilenceTimeoutMs / 1000} s", sendClose: false);
                    return;
                }
                if (_helloHeard)
                    SendPing();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
