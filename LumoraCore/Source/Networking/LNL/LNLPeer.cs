using System;
using System.Net;
using LiteNetLib;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.LNL;

/// <summary>
/// LiteNetLib server-side peer wrapper.
/// Represents a connected client from the server's perspective.
/// </summary>
public class LNLPeer : IConnection
{
    internal readonly NetManager Server;
    internal readonly NetPeer Peer;

    public bool IsOpen { get; private set; }
    public string FailReason { get; private set; }
    public IPAddress IP => Peer?.Address;
    public Uri Address { get; private set; }
    public string Identifier { get; private set; }
    public ulong ReceivedBytes { get; private set; }

    public event Action<IConnection> Closed;
    public event Action<IConnection> Connected;
    public event Action<IConnection> ConnectionFailed;
    public event Action<byte[], int> DataReceived;

    public LNLPeer(NetManager server, NetPeer peer)
    {
        Server = server;
        Peer = peer;
        IsOpen = true;
        Address = new Uri($"lnl://{peer.Address}:{peer.Port}");
        Identifier = $"LNL:{peer.Address}:{peer.Port}";

        AquaLogger.Log($"LNL Peer created: {Identifier}");
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

    public void Send(byte[] data, int length, bool reliable, bool background)
    {
        if (Peer == null || Peer.ConnectionState != ConnectionState.Connected)
        {
            AquaLogger.Warn($"Cannot send to {Identifier} - peer not connected");
            return;
        }

        DeliveryMethod method = reliable
            ? DeliveryMethod.ReliableOrdered
            : DeliveryMethod.Sequenced;

        byte channel = (byte)(background ? 1 : 0);

        if (method == DeliveryMethod.Sequenced &&
            Peer.GetMaxSinglePacketSize(method) < length)
        {
            method = DeliveryMethod.ReliableUnordered;
        }

        Peer.Send(data, 0, length, channel, method);
    }

    internal void InformOfNewData(byte[] data, int length)
    {
        ReceivedBytes += (ulong)length;
        DataReceived?.Invoke(data, length);
    }

    internal void InformClosed()
    {
        if (IsOpen)
        {
            IsOpen = false;
            AquaLogger.Log($"LNL Peer closed: {Identifier}");
            Closed?.Invoke(this);
        }
    }

    public void Dispose()
    {
        Close();
    }
}
