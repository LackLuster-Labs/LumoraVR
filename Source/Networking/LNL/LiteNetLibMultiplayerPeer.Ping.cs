using System;
using System.Collections.Generic;
using Godot;
using LiteNetLib;

namespace LumoraVR.Source.Networking
{
    public partial class LiteNetLibMultiplayerPeer
    {
        // Ping tracking properties
        private long _lastPingUpdateTime = 0;
        private const int PingUpdateIntervalMs = 1000; // Update ping every second

        // Network statistics
        public int PacketsSent { get; private set; } = 0;
        public int PacketsReceived { get; private set; } = 0;

        // Ping information per peer
        private Dictionary<int, int> _peerPings = new Dictionary<int, int>();

        /// <summary>
        /// Get the ping time for a specific peer in milliseconds
        /// </summary>
        /// <param name="peerId">The ID of the peer</param>
        /// <returns>The ping time in milliseconds, or -1 if unknown</returns>
        public int GetPeerPing(int peerId)
        {
            if (_idToPeer.TryGetValue(peerId, out var peer))
            {
                return peer.Ping;
            }
            return -1;
        }

        /// <summary>
        /// Get the ping time for all connected peers
        /// </summary>
        /// <returns>Dictionary mapping peer IDs to ping times</returns>
        public Dictionary<int, int> GetAllPeerPings()
        {
            Dictionary<int, int> result = new Dictionary<int, int>();
            foreach (var pair in _idToPeer)
            {
                result[pair.Key] = pair.Value.Ping;
            }
            return result;
        }

        /// <summary>
        /// Gets the ping to the server if this is a client, or -1 if this is a server
        /// </summary>
        public int GetServerPing()
        {
            // Only clients have server ping
            if (IsServer) return -1;

            // Client only connects to the server, which is ID 1
            return GetPeerPing(1);
        }

        /// <summary>
        /// Modified network receive event to track incoming packets
        /// </summary>
        private void TrackNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            PacketsReceived++;

            // Continue with original packet processing
            if (channel == ControlChannel)
            {
                if (IsServer)
                {
                    return;
                }
                try
                {
                    var controlCode = reader.GetByte();
                    switch (controlCode)
                    {
                        case ControlSetLocalID:
                            {
                                if (_localNetworkId == -1)
                                {
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

        /// <summary>
        /// Update ping statistics
        /// </summary>
        private void UpdatePingStatistics()
        {
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (currentTime - _lastPingUpdateTime < PingUpdateIntervalMs) return;

            _lastPingUpdateTime = currentTime;

            // Update ping information for all peers
            foreach (var pair in _idToPeer)
            {
                _peerPings[pair.Key] = pair.Value.Ping;
            }
        }

        /// <summary>
        /// Resets all network statistics counters
        /// </summary>
        public void ResetNetworkStatistics()
        {
            PacketsSent = 0;
            PacketsReceived = 0;
        }

    }
}
