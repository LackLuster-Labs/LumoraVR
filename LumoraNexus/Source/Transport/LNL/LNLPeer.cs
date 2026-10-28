// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Net;
using LiteNetLib;
using LumoraLogger = Lumora.Nexus.Diagnostics.NexusLog;
using System.Threading;

namespace Lumora.Nexus.Transport.LNL;

public class LNLPeer : IConnection
{
    internal readonly NetManager Server;
    internal readonly NetPeer Peer;
    private readonly LNLCryptoSession _crypto = new(isClient: false);
    private readonly LinkEmulator? _emulator = LinkEmulator.Create();
    // Everything goes through the outbox, which hands LNL no more than the link carries.
    private readonly LNLOutbox _outbox;

    public bool IsOpen { get; private set; }
    public string FailReason { get; private set; } = null!;
    public IPAddress IP => (Peer?.Address) ?? null!;
    public Uri Address { get; private set; }
    public string Identifier { get; private set; }
    public ulong ReceivedBytes { get; private set; }
    public ulong SentBytes => _outbox.SentBytes;
    public int Ping => Peer?.Ping ?? -1;
    public long PacketsSent => _outbox.ReliablePackets;
    public long PacketsLost => _outbox.Resent;
    public int QueuedPackets => LNLLanes.Queued(Peer) + _outbox.HeldPackets;
    public long PendingSendBytes => _outbox.LiveBacklogBytes;
    public long DroppedUnreliable => _outbox.DroppedUnreliable;
    public bool IsEncrypted => _crypto.IsEstablished;
    public string TransportName => "LNL";
    public bool ViaRelay { get; }

    public event Action<IConnection> Closed = null!;
    public event Action<IConnection> Connected
    {
        add { }
        remove { }
    }

    public event Action<IConnection> ConnectionFailed
    {
        add { }
        remove { }
    }
    public event Action<byte[], int> DataReceived = null!;
    internal event Action<LNLPeer> CryptoEstablished = null!;

    internal DateTime CreatedUtc { get; } = DateTime.UtcNow;
    internal bool IsCryptoEstablished => _crypto.IsEstablished;

    public LNLPeer(NetManager server, NetPeer peer, bool viaRelay = false)
    {
        Server = server;
        Peer = peer;
        _outbox = new LNLOutbox(peer, server, _emulator);
        ViaRelay = viaRelay;
        IsOpen = true;
        // An IPv6 peer needs brackets in a URL, and its zone id cannot go in one at all.
        var address = peer.Address;
        Address = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? new Uri($"lnl://[{new IPAddress(address.GetAddressBytes())}]:{peer.Port}")
            : new Uri($"lnl://{address}:{peer.Port}");
        Identifier = $"LNL:{peer.Address}:{peer.Port}";

        LumoraLogger.Debug($"[lnl] LNL Peer created: {Identifier}");
    }

    public void Connect(Action<string> statusCallback)
    {
        throw new NotSupportedException("LNLPeer doesn't support Connect - already connected");
    }

    public void Close()
    {
        if (IsOpen && Peer != null)
        {
            Server.DisconnectPeer(Peer);
            InformClosed();
        }
    }

    private bool _loggedNotConnected;

    public void Send(byte[] data, int length, bool reliable, bool background)
    {
        if (Peer == null || Peer.ConnectionState != ConnectionState.Connected)
        {
            // Expected while a peer is leaving - the host keeps trying to stream to it until LNL times the peer
            // out (~30s). Don't Warn every frame; note it once at Debug. The Closed event is the real signal. -xlinka
            if (!_loggedNotConnected)
            {
                _loggedNotConnected = true;
                LumoraLogger.Debug($"[lnl] Send to {Identifier} skipped - peer not connected (leaving/timeout)");
            }
            return;
        }
        _loggedNotConnected = false;

        // Encrypted into this peer's own buffer and handed to LNL, which copies it, before the next frame.
        lock (_sendBufferLock)
        {
            byte[] encrypted;
            int count;
            try
            {
                encrypted = _crypto.EncryptReusing(data, length, ref _sendBuffer, out count);
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"[lnl] Send to {Identifier} failed before LNL crypto was ready ({ex.Message})");
                return;
            }

            SendRaw(encrypted, count, reliable, background);
        }
    }

    private readonly object _sendBufferLock = new();
    private byte[]? _sendBuffer;

    public void SendBulk(byte[] data, int length)
    {
        if (Peer == null || Peer.ConnectionState != ConnectionState.Connected)
            return;
        lock (_sendBufferLock)
        {
            byte[] encrypted;
            int count;
            try
            {
                encrypted = _crypto.EncryptReusing(data, length, ref _sendBuffer, out count);
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"[lnl] Bulk send to {Identifier} failed before LNL crypto was ready ({ex.Message})");
                return;
            }
            _outbox.SendBulk(encrypted, count);
        }
    }

    // Channel 0 foreground, 1 background; unreliable data too big for one packet goes reliable and unordered.
    private void SendRaw(byte[] data, int length, bool reliable, bool background)
        => _outbox.Send(data, length, reliable, background);

    // LNLPeer shares the listener's NetManager; the listener polls it and pumps every peer's outbox.
    public void Poll() { }

    internal void PumpOutbox() => _outbox.Pump();

    internal void InformOfNewData(byte[] data, int length)
    {
        ReceivedBytes += (ulong)length;
        bool wasEstablished = _crypto.IsEstablished;
        if (!_crypto.TryHandleIncoming(data, length, out var plaintext, out var response))
        {
            FailReason = "LNL crypto handshake failed";
            LumoraLogger.Warn($"[lnl] Closing {Identifier}: {FailReason}");
            Close();
            return;
        }

        if (response != null)
            SendRaw(response, response.Length, reliable: true, background: false);

        if (!wasEstablished && _crypto.IsEstablished)
        {
            LumoraLogger.Debug($"[lnl] Crypto established with {Identifier}");
            CryptoEstablished?.Invoke(this);
        }

        if (plaintext != null)
            DataReceived?.Invoke(plaintext, plaintext.Length);
    }

    internal void InformClosed()
    {
        if (IsOpen)
        {
            IsOpen = false;
            LumoraLogger.Debug($"[lnl] LNL Peer closed: {Identifier}");
            Closed?.Invoke(this);
        }
    }

    public void Dispose()
    {
        _emulator?.Stop();
        Close();
        _crypto.Dispose();
    }
}
