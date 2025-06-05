using System;
using System.Net.Http;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using LumoraVR.Source.Logging;
using LumoraVR.Source.Networking;
using LiteNetLib.Utils;
using LiteNetLib;
using Bones.Core;

namespace LumoraVR.Source.Management
{
    public partial class ClientManager
    {
        public void SetTargetWorldPath(string path)
        {
            _targetWorldPath = path;
        }
        public void DisconnectFromCurrentServer()
        {
            Node root = GetNode("/root/Root/WorldRoot");
            if (_peer?.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected)
                _multiplayerScene?.Rpc(MultiplayerScene.MethodName.DisconnectPlayer);
            _peer?.Close();
            Multiplayer.MultiplayerPeer = null;
        }

        public void JoinNatServer(string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                Logger.Error("Session identifier is null or empty. Cannot join NAT server.");
                return;
            }

            DisconnectFromCurrentServer();

            var token = $"client:{identifier}";
            GD.Print($"Attempting NAT punchthrough with token: {token}");

            _peer = new LiteNetLibMultiplayerPeer();
            _peer.CreateClientNat(SessionInfo.SessionServer.Address.ToString(),
                                SessionInfo.SessionServer.Port, token);
            Multiplayer.MultiplayerPeer = _peer;
            _isDirectConnection = true;

            RegisterPeerEvents();
        }

        public void JoinServer(string address, int port, string worldPath = null)
        {
            if (worldPath != null)
            {
                _targetWorldPath = worldPath;
            }

            DisconnectFromCurrentServer();

            Logger.Log($"Creating client peer to connect to {address}:{port}");
            _peer = new LiteNetLibMultiplayerPeer();
            _peer.CreateClient(address, port);
            Multiplayer.MultiplayerPeer = _peer;
            _isDirectConnection = true;

            RegisterPeerEvents();
            Logger.Log("Client peer created and events registered");
        }

        public void JoinNatServerRelay(string identifier)
        {
            DisconnectFromCurrentServer();

            _peer = new LiteNetLibMultiplayerPeer();
            // Connect to relay server using identifier as the connection key
            _peer.CreateClient(SessionInfo.RelayServer.Address.ToString(),
                              SessionInfo.RelayServer.Port,
                              $"Lum"); // Pass session identifier in the initial connection

            Multiplayer.MultiplayerPeer = _peer;
            _isDirectConnection = false;

            void PeerConnected(NetPeer peer)
            {
                NetDataWriter writer = new NetDataWriter();
                writer.Put($"session:{identifier}");
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
                _peer.Listener.PeerConnectedEvent -= PeerConnected;
            }

            _peer.Listener.PeerConnectedEvent += PeerConnected;

            RegisterPeerEvents();
        }

        private void RegisterPeerEvents()
        {
            _peer.PeerDisconnected += PeerOnPeerDisconnected;
            _peer.ClientConnectionSuccess += PeerOnClientConnectionSuccess;
            _peer.ClientConnectionFail += PeerOnClientConnectionFail;
        }

        private void PeerOnClientConnectionFail()
        {
            Logger.Error("Failed to connect to server");
            UnregisterPeerEvents();
            JoinLocalHome();
        }

        private void PeerOnClientConnectionSuccess()
        {
            Logger.Log("Successfully connected to server");
            MultiplayerScene.Instance?.Rpc(MultiplayerScene.MethodName.SetPlayerName, System.Environment.MachineName);
            UnregisterPeerEvents();

            if (_targetWorldPath != null)
            {
                WorldManager.Instance.LoadWorld(_targetWorldPath, false);
                _targetWorldPath = null;
            }
            EmitSignal(SignalName.OnConnectSucsess);
        }
        private void PeerOnPeerDisconnected(long id)
        {
            GD.Print($"{id} disconnected");
            if (id == 1) JoinLocalHome();
        }

        private void UnregisterPeerEvents()
        {
            _peer.ClientConnectionSuccess -= PeerOnClientConnectionSuccess;
            _peer.ClientConnectionFail -= PeerOnClientConnectionFail;
        }

        private async Task<SessionInfo> FetchServerInfo()
        {
            try
            {
                GD.Print("Trying to get session list");

                using var client = new System.Net.Http.HttpClient();
                var response = await client.GetStringAsync(SessionInfo.SessionList);

                GD.Print("Got the session list");
                GD.Print(response);

                var sessions = JsonSerializer.Deserialize<List<SessionInfo>>(response,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (sessions != null && sessions.Count != 0)
                {
                    foreach (var session in sessions)
                    {
                        GD.Print($"Session: {session.Name}, Identifier: {session.SessionIdentifier}");

                        if (string.IsNullOrEmpty(session.SessionIdentifier))
                        {
                            Logger.Error($"Session {session.Name} is missing an identifier. Skipping.");
                            continue;
                        }

                        return session;
                    }
                }

                Logger.Error("No valid sessions available in the API response.");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error fetching server info: {ex.Message}");
                return null;
            }
        }
    }
}
