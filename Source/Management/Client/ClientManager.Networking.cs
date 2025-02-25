using System;
using System.Net.Http;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Aquamarine.Source.Logging;
using Aquamarine.Source.Networking;
using Aquamarine.Source.Management.World;
using LiteNetLib.Utils;
using LiteNetLib;

namespace Aquamarine.Source.Management
{
    public partial class ClientManager : Node
    {
        // Connection states for more detailed tracking
        private enum ConnectionState
        {
            Disconnected,
            Connecting,
            Connected,
            Failed
        }

        private ConnectionState _currentConnectionState = ConnectionState.Disconnected;

        // Timeout mechanism for connections
        private const int CONNECTION_TIMEOUT_MS = 10000; // 10 seconds
        private System.Timers.Timer _connectionTimeoutTimer;

        private void InitializeConnectionTimeout()
        {
            _connectionTimeoutTimer = new System.Timers.Timer(CONNECTION_TIMEOUT_MS);
            _connectionTimeoutTimer.Elapsed += OnConnectionTimeout;
            _connectionTimeoutTimer.AutoReset = false;
        }
        public void JoinServer(string address, int port)
        {
            // Ensure clean disconnect from any existing connection
            DisconnectFromCurrentServer();

            try
            {
                // Set connection state
                _currentConnectionState = ConnectionState.Connecting;

                // Start connection timeout
                _connectionTimeoutTimer?.Start();

                // Establish direct connection
                _peer = new LiteNetLibMultiplayerPeer();
                _peer.CreateClient(address, port);

                Multiplayer.MultiplayerPeer = _peer;
                _isDirectConnection = true;

                // Register connection events
                RegisterPeerEvents();

                // Add connection success/failure handlers
                _peer.ClientConnectionSuccess += OnDirectConnectionSuccess;
                _peer.ClientConnectionFail += OnConnectionFailed;

                Logger.Log($"Attempting to connect to server at {address}:{port}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to initiate server connection: {ex.Message}");
                HandleConnectionFailure();
            }
        }

        private void OnDirectConnectionSuccess()
        {
            Logger.Log("Direct Connection successful.");
            _currentConnectionState = ConnectionState.Connected;
            _connectionTimeoutTimer?.Stop();

            // Set player name
            _multiplayerScene?.Rpc(MultiplayerScene.MethodName.SetPlayerName,
                System.Environment.MachineName);

            // Do NOT automatically load any world
            Logger.Log("Direct connection established. Waiting for world assignment.");

            // Cleanup event
            _peer.ClientConnectionSuccess -= OnDirectConnectionSuccess;
        }
        private void OnConnectionTimeout(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_currentConnectionState == ConnectionState.Connecting)
            {
                Logger.Error("Connection attempt timed out.");
                HandleConnectionFailure();
            }
        }

        private void HandleConnectionFailure()
        {
            _currentConnectionState = ConnectionState.Failed;

            // Stop any ongoing connection attempts
            _peer?.Close();
            Multiplayer.MultiplayerPeer = null;

            // Unregister any pending events
            UnregisterPeerEvents();

            // Fallback to local home
            LoadWorld("local_home");

            // Notify user (you might want to implement a more user-friendly notification)
            Logger.Error("Failed to establish connection. Returning to local home.");
        }

        private void DisconnectFromCurrentServer()
        {
            try
            {
                if (_peer?.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected)
                {
                    _multiplayerScene?.Rpc(MultiplayerScene.MethodName.DisconnectPlayer);
                }

                _peer?.Close();
                Multiplayer.MultiplayerPeer = null;
                _currentConnectionState = ConnectionState.Disconnected;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error during server disconnection: {ex.Message}");
            }
        }

        public void JoinNatServer(string identifier)
        {
            // Validate input
            if (string.IsNullOrEmpty(identifier))
            {
                Logger.Error("Session identifier is null or empty. Cannot join NAT server.");
                return;
            }

            // Ensure clean disconnect from any existing connection
            DisconnectFromCurrentServer();

            try
            {
                // Prepare connection
                var token = $"client:{identifier}";
                Logger.Log($"Attempting NAT punchthrough with token: {token}");

                _currentConnectionState = ConnectionState.Connecting;

                // Start connection timeout
                _connectionTimeoutTimer?.Start();

                // Establish connection
                _peer = new LiteNetLibMultiplayerPeer();
                _peer.CreateClientNat(
                    SessionInfo.SessionServer.Address.ToString(),
                    SessionInfo.SessionServer.Port,
                    token
                );

                Multiplayer.MultiplayerPeer = _peer;
                _isDirectConnection = true;

                // Register connection events
                RegisterPeerEvents();

                // Add explicit world loading after connection
                _peer.ClientConnectionSuccess += OnNatConnectionSuccess;
                _peer.ClientConnectionFail += OnConnectionFailed;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to initiate NAT server connection: {ex.Message}");
                HandleConnectionFailure();
            }
        }

        public void JoinNatServerRelay(string identifier)
        {
            // Validate input
            if (string.IsNullOrEmpty(identifier))
            {
                Logger.Error("Session identifier is null or empty. Cannot join NAT relay server.");
                return;
            }

            // Ensure clean disconnect from any existing connection
            DisconnectFromCurrentServer();

            try
            {
                _currentConnectionState = ConnectionState.Connecting;

                // Start connection timeout
                _connectionTimeoutTimer?.Start();

                // Establish connection
                _peer = new LiteNetLibMultiplayerPeer();
                _peer.CreateClient(
                    SessionInfo.RelayServer.Address.ToString(),
                    SessionInfo.RelayServer.Port,
                    $"Lum"
                );

                Multiplayer.MultiplayerPeer = _peer;
                _isDirectConnection = false;

                // Custom relay connection handling
                void PeerConnected(NetPeer peer)
                {
                    var writer = new NetDataWriter();
                    writer.Put($"session:{identifier}");
                    peer.Send(writer, DeliveryMethod.ReliableOrdered);
                    _peer.Listener.PeerConnectedEvent -= PeerConnected;
                }

                _peer.Listener.PeerConnectedEvent += PeerConnected;

                // Register connection events
                RegisterPeerEvents();

                // Add explicit world loading after connection
                _peer.ClientConnectionSuccess += OnRelayConnectionSuccess;
                _peer.ClientConnectionFail += OnConnectionFailed;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to initiate NAT relay connection: {ex.Message}");
                HandleConnectionFailure();
            }
        }

        private void OnNatConnectionSuccess()
        {
            Logger.Log("NAT Connection successful. Loading multiplayer scene.");
            _currentConnectionState = ConnectionState.Connected;
            _connectionTimeoutTimer?.Stop();

            // Set player name and load world
            _multiplayerScene?.Rpc(MultiplayerScene.MethodName.SetPlayerName,
                System.Environment.MachineName);

            LoadWorld("multiplayer_base");

            // Cleanup event
            _peer.ClientConnectionSuccess -= OnNatConnectionSuccess;
        }

        private void OnRelayConnectionSuccess()
        {
            Logger.Log("Relay Connection successful. Loading multiplayer scene.");
            _currentConnectionState = ConnectionState.Connected;
            _connectionTimeoutTimer?.Stop();

            // Set player name and load world
            _multiplayerScene?.Rpc(MultiplayerScene.MethodName.SetPlayerName,
                System.Environment.MachineName);

            LoadWorld("multiplayer_base");

            // Cleanup event
            _peer.ClientConnectionSuccess -= OnRelayConnectionSuccess;
        }

        private void OnConnectionFailed()
        {
            Logger.Error("Connection failed.");
            HandleConnectionFailure();
        }

        private void RegisterPeerEvents()
        {
            _peer.PeerDisconnected += PeerOnPeerDisconnected;
            _peer.ClientConnectionSuccess += PeerOnClientConnectionSuccess;
            _peer.ClientConnectionFail += PeerOnClientConnectionFail;
        }

        private void PeerOnClientConnectionFail()
        {
            Logger.Error("Client connection failed.");
            UnregisterPeerEvents();
            HandleConnectionFailure();
        }

        private void PeerOnClientConnectionSuccess()
        {
            Logger.Log("Client connection successful.");
            _multiplayerScene?.Rpc(MultiplayerScene.MethodName.SetPlayerName,
                System.Environment.MachineName);
            UnregisterPeerEvents();
        }

        private void PeerOnPeerDisconnected(long id)
        {
            Logger.Log($"Peer {id} disconnected");
            if (id == 1)
            {
                _currentConnectionState = ConnectionState.Disconnected;
                LoadWorld("local_home");
            }
        }

        private void UnregisterPeerEvents()
        {
            if (_peer != null)
            {
                _peer.ClientConnectionSuccess -= PeerOnClientConnectionSuccess;
                _peer.ClientConnectionFail -= PeerOnClientConnectionFail;
                _peer.PeerDisconnected -= PeerOnPeerDisconnected;
            }
        }

        private async Task<SessionInfo> FetchServerInfo()
        {
            try
            {
                Logger.Log("Attempting to retrieve session list");

                using var client = new System.Net.Http.HttpClient();
                var response = await client.GetStringAsync(SessionInfo.SessionList);

                Logger.Log("Session list retrieved successfully");
                Logger.Log(response);

                var sessions = JsonSerializer.Deserialize<List<SessionInfo>>(response,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (sessions != null && sessions.Count > 0)
                {
                    foreach (var session in sessions)
                    {
                        Logger.Log($"Found Session: {session.Name}, Identifier: {session.SessionIdentifier}");

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

        // Method to load world with additional error handling
        private void LoadWorld(string worldId)
        {
            try
            {
                var worldManager = GetNode<WorldManager>($"{_worldSession.GetPath()}/WorldManager");
                if (worldManager != null)
                {
                    // Force reload to ensure scene change
                    worldManager.LoadWorld(worldId, true);
                }
                else
                {
                    Logger.Error("Could not find WorldManager to load world");
                    // Fallback to local home if world manager not found
                    worldManager?.LoadWorld("local_home", true);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading world {worldId}: {ex.Message}");
                // Fallback to local home
                GetNode<WorldManager>($"{_worldSession.GetPath()}/WorldManager")?.LoadWorld("local_home", true);
            }
        }
    }
}
