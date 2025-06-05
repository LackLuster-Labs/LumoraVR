using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Godot;
using LiteNetLib;
using LiteNetLib.Utils;

namespace LumoraVR.Source.Networking
{
    public partial class LiteNetLibMultiplayerPeer
    {
        private void NatPunchListenerOnNatIntroductionSuccess(IPEndPoint targetendpoint, NatAddressType type, string token)
        {
            if (!NatPunch) return;
            _punchthroughTimer.Reset();
            if (IsServer)
            {

            }
            else
            {
                if (_clientConnectionStatus == ConnectionStatus.Connecting) NetManager.Connect(targetendpoint, token);
            }
        }

        private void ListenerOnNetworkErrorEvent(IPEndPoint endpoint, SocketError socketerror) => GD.PrintErr(socketerror);

        private void UpdateIdToPeer()
        {
            _idToPeer = _peerToId.ToDictionary(i => i.Value, i => i.Key);
        }

        private void ListenerOnConnectionRequestEvent(ConnectionRequest request)
        {
            if (IsServer)
            {
                if (NetManager.ConnectedPeersCount >= MaxClients)
                {
                    //GD.Print("Rejecting, full lobby");
                    request.Reject("Lobby Full"u8.ToArray());
                    return;
                }
                if (_refuseNewConnections)
                {
                    request.Reject("Refusing new connections"u8.ToArray());
                    return;
                }
                if (NatPunch && request.RemoteEndPoint.Address.ToString() == "127.0.0.1")
                {
                    request.Reject("Localhost NAT punches are unsupported"u8.ToArray()); //TODO fix this properly, this wont work forever
                    return;
                }

                var usedIds = _idToPeer.Select(i => i.Key).ToArray();
                var unusedId = Random.Shared.Next(2, int.MaxValue);
                while (usedIds.Contains(unusedId)) unusedId = Random.Shared.Next(2, int.MaxValue);

                //GD.Print($"Client {unusedId} connected to server");

                var writer = new NetDataWriter();
                writer.Put(ControlSetLocalID);
                writer.Put(unusedId);

                var peer = request.Accept();
                peer.Send(writer, ControlChannel, DeliveryMethod.ReliableOrdered);

                _peerToId.Add(peer, unusedId);
                UpdateIdToPeer();
                EmitSignal(MultiplayerPeer.SignalName.PeerConnected, unusedId);
            }
            else
            {
                request.Reject("Not a server"u8.ToArray());
            }
        }

        private void ListenerOnPeerDisconnectedEvent(NetPeer peer, DisconnectInfo disconnectinfo)
        {
            if (IsServer)
            {
                if (_peerToId.TryGetValue(peer, out var peerId))
                {
                    if (disconnectinfo.Reason is not DisconnectReason.DisconnectPeerCalled) EmitSignal(MultiplayerPeer.SignalName.PeerDisconnected, peerId);

                    _peerToId.Remove(peer);
                    UpdateIdToPeer();
                }
            }
            else
            {
                var connectionFail = _clientConnectionStatus is ConnectionStatus.Connecting;
                //GD.Print($"Disconnected: {disconnectinfo.Reason}");

                _peerToId.Clear();
                _idToPeer.Clear();
                _clientConnectionStatus = ConnectionStatus.Disconnected;
                NetManager.Stop();
                _localNetworkId = -1;
                _packetQueue.Clear();

                if (_peerToId.TryGetValue(peer, out var peerId))
                {
                    EmitSignal(MultiplayerPeer.SignalName.PeerDisconnected, peerId);
                }
                if (connectionFail) EmitSignal(SignalName.ClientConnectionFail);
            }
        }

        private void ListenerOnPeerConnectedEvent(NetPeer peer)
        {
            GD.Print($"Peer {peer} connected to {(IsServer ? "server" : "client")}");

            if (IsServer)
            {

            }
            else
            {
                //GD.Print("Connected to server");
                //our only peer is the server
                _peerToId.Add(peer, 1);
                UpdateIdToPeer();
            }
        }

        private void ListenerOnNetworkReceiveEvent(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            // Increment the received packets counter
            PacketsReceived++;

            // Process the packet as before
            if (channel == ControlChannel)
            {
                if (IsServer)
                {
                    //GD.PrintErr("Received control packet, but I'm a server");
                    return;
                }
                try
                {
                    //GD.Print($"Got special control packet");
                    var controlCode = reader.GetByte();
                    switch (controlCode)
                    {
                        case ControlSetLocalID:
                            {
                                if (_localNetworkId == -1)
                                {
                                    GD.Print("Finished connecting");
                                    _localNetworkId = reader.GetInt();
                                    _clientConnectionStatus = ConnectionStatus.Connected;
                                    EmitSignal(MultiplayerPeer.SignalName.PeerConnected, 1);
                                    EmitSignal(SignalName.ClientConnectionSuccess);
                                }
                                return;
                            }
                    }
                    return;
                }
                catch
                {
                    return;
                }
            }
            _packetQueue.Enqueue(new Packet(peer, reader, channel, deliveryMethod));
        }
    }
}
