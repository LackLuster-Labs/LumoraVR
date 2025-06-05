using System;
using System.Collections.Generic;
using LumoraVR.Source.Logging;
using LumoraVR.Source.Scene.RootObjects;
using Godot;

namespace LumoraVR.Source.Management
{
    [GlobalClass]
    public partial class CustomPlayerSync : Node
    {
        private CustomPlayerSpawner _playerSpawner;
        private Dictionary<int, PlayerSyncData> _playerSyncData = new Dictionary<int, PlayerSyncData>();
        private const float SyncInterval = 1.0f / 20.0f; // 20 Hz sync rate
        private float _timeSinceLastSync = 0;
        private bool _isServer;

        // Class to hold sync data for each player
        private class PlayerSyncData
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public Vector3 HeadPosition;
            public Quaternion HeadRotation;
            public Vector3 LeftHandPosition;
            public Quaternion LeftHandRotation;
            public Vector3 RightHandPosition;
            public Quaternion RightHandRotation;
            public Vector3 HipPosition;
            public Quaternion HipRotation;
            public Vector3 LeftFootPosition;
            public Quaternion LeftFootRotation;
            public Vector3 RightFootPosition;
            public Quaternion RightFootRotation;
            public float UserHeight;
            public Vector2 MovementInput;
            public byte MovementButtons;
            public double LastUpdateTime;
        }

        public override void _Ready()
        {
            try
            {
                // Find the CustomPlayerSpawner
                _playerSpawner = GetNodeOrNull<CustomPlayerSpawner>("%CustomPlayerSpawner");
                if (_playerSpawner == null)
                {
                    _playerSpawner = GetNodeOrNull<CustomPlayerSpawner>("../CustomPlayerSpawner");

                    // Try one more fallback - search in the scene tree
                    if (_playerSpawner == null)
                    {
                        _playerSpawner = FindNodeByType<CustomPlayerSpawner>(GetTree().Root);
                        if (_playerSpawner != null)
                        {
                            Logger.Log($"CustomPlayerSync: Found CustomPlayerSpawner by searching scene tree at {_playerSpawner.GetPath()}");
                        }
                    }
                }

                if (_playerSpawner != null)
                {
                    Logger.Log("CustomPlayerSync: Found CustomPlayerSpawner");

                    // Connect to player spawned/removed signals
                    _playerSpawner.PlayerSpawned += OnPlayerSpawned;
                    _playerSpawner.PlayerRemoved += OnPlayerRemoved;
                }
                else
                {
                    Logger.Error("CustomPlayerSync: Could not find CustomPlayerSpawner");
                }

                // Defer server role determination to _Process to ensure multiplayer is initialized
                _isServer = false; // Default to false, will be updated in _Process

                // Connect to network peer connected signal - only if multiplayer is available
                if (Multiplayer != null)
                {
                    Multiplayer.PeerConnected += OnPeerConnected;
                    Multiplayer.PeerDisconnected += OnPeerDisconnected;
                }

                Logger.Log("CustomPlayerSync initialized");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in CustomPlayerSync._Ready: {ex.Message}");
            }
        }

        // Helper method to find a node of a specific type in the scene tree
        private T FindNodeByType<T>(Node root) where T : class
        {
            // Check if the current node is of the desired type
            if (root is T result)
            {
                return result;
            }

            // Recursively search through all children
            foreach (var child in root.GetChildren())
            {
                var found = FindNodeByType<T>(child);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        public override void _Process(double delta)
        {
            try
            {
                // Check if multiplayer is initialized
                if (Multiplayer?.MultiplayerPeer != null)
                {
                    // Update server status if needed
                    if (!_isServer)
                    {
                        try
                        {
                            _isServer = Multiplayer.IsServer();
                            if (_isServer)
                            {
                                Logger.Log("CustomPlayerSync: Determined we are the server");
                            }
                        }
                        catch (Exception ex)
                        {
                            // Still not ready, will try again next frame
                            Logger.Log($"CustomPlayerSync: Not ready to determine server status: {ex.Message}");
                        }
                    }

                    _timeSinceLastSync += (float)delta;

                    // Only sync at the specified interval
                    if (_timeSinceLastSync >= SyncInterval)
                    {
                        _timeSinceLastSync = 0;

                        try
                        {
                            // Get local player ID
                            int localId = Multiplayer.GetUniqueId();

                            // If we have authority over any players, send their data
                            if (_playerSpawner != null)
                            {
                                foreach (var player in _playerSpawner.GetAllPlayers())
                                {
                                    // Only sync players we have authority over
                                    if (player.Value.Authority == localId)
                                    {
                                        // Send player data to all peers
                                        RpcId(1, MethodName.SyncPlayerData,
                                            player.Key,
                                            player.Value.GlobalPosition,
                                            player.Value.Velocity,
                                            player.Value.HeadPosition,
                                            player.Value.HeadRotation,
                                            player.Value.LeftHandPosition,
                                            player.Value.LeftHandRotation,
                                            player.Value.RightHandPosition,
                                            player.Value.RightHandRotation,
                                            player.Value.HipPosition,
                                            player.Value.HipRotation,
                                            player.Value.LeftFootPosition,
                                            player.Value.LeftFootRotation,
                                            player.Value.RightFootPosition,
                                            player.Value.RightFootRotation,
                                            player.Value.UserHeight,
                                            player.Value.MovementInput,
                                            player.Value.MovementButtons);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Error in CustomPlayerSync sync: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in CustomPlayerSync._Process: {ex.Message}");
            }

            // Apply interpolation for remote players
            foreach (var syncData in _playerSyncData)
            {
                // Skip if this is our local player
                if (syncData.Key == Multiplayer.GetUniqueId())
                    continue;

                // Find the player
                var player = _playerSpawner?.GetPlayer(syncData.Key);
                if (player != null)
                {
                    // Apply the sync data
                    player.GlobalPosition = syncData.Value.Position;
                    player.Velocity = syncData.Value.Velocity;
                    player.HeadPosition = syncData.Value.HeadPosition;
                    player.HeadRotation = syncData.Value.HeadRotation;
                    player.LeftHandPosition = syncData.Value.LeftHandPosition;
                    player.LeftHandRotation = syncData.Value.LeftHandRotation;
                    player.RightHandPosition = syncData.Value.RightHandPosition;
                    player.RightHandRotation = syncData.Value.RightHandRotation;
                    player.HipPosition = syncData.Value.HipPosition;
                    player.HipRotation = syncData.Value.HipRotation;
                    player.LeftFootPosition = syncData.Value.LeftFootPosition;
                    player.LeftFootRotation = syncData.Value.LeftFootRotation;
                    player.RightFootPosition = syncData.Value.RightFootPosition;
                    player.RightFootRotation = syncData.Value.RightFootRotation;
                    player.UserHeight = syncData.Value.UserHeight;
                    player.MovementInput = syncData.Value.MovementInput;
                    player.MovementButtons = syncData.Value.MovementButtons;
                }
            }
        }

        private void OnPlayerSpawned(PlayerCharacterController player)
        {
            // Initialize sync data for this player
            if (!_playerSyncData.ContainsKey(player.Authority))
            {
                _playerSyncData[player.Authority] = new PlayerSyncData
                {
                    Position = player.GlobalPosition,
                    Velocity = player.Velocity,
                    HeadPosition = player.HeadPosition,
                    HeadRotation = player.HeadRotation,
                    LeftHandPosition = player.LeftHandPosition,
                    LeftHandRotation = player.LeftHandRotation,
                    RightHandPosition = player.RightHandPosition,
                    RightHandRotation = player.RightHandRotation,
                    HipPosition = player.HipPosition,
                    HipRotation = player.HipRotation,
                    LeftFootPosition = player.LeftFootPosition,
                    LeftFootRotation = player.LeftFootRotation,
                    RightFootPosition = player.RightFootPosition,
                    RightFootRotation = player.RightFootRotation,
                    UserHeight = player.UserHeight,
                    MovementInput = player.MovementInput,
                    MovementButtons = player.MovementButtons,
                    LastUpdateTime = Time.GetTicksMsec() / 1000.0
                };

                Logger.Log($"CustomPlayerSync: Initialized sync data for player {player.Authority}");
            }
        }

        private void OnPlayerRemoved(int playerId)
        {
            // Remove sync data for this player
            if (_playerSyncData.ContainsKey(playerId))
            {
                _playerSyncData.Remove(playerId);
                Logger.Log($"CustomPlayerSync: Removed sync data for player {playerId}");
            }
        }

        private void OnPeerConnected(long id)
        {
            Logger.Log($"CustomPlayerSync: Peer connected: {id}");

            // Send existing player data to the new peer after a short delay
            // to ensure they're fully connected and ready
            if (_isServer)
            {
                // Create a one-shot timer to delay sending player data
                var timer = new Timer
                {
                    WaitTime = 0.5f,
                    OneShot = true,
                    Autostart = true
                };
                AddChild(timer);
                timer.Timeout += () =>
                {
                    SendAllPlayerDataTo((int)id);
                    timer.QueueFree();
                };
            }
        }

        // Method to send all existing player data to a newly connected peer
        public void SendAllPlayerDataTo(int targetPeerId)
        {
            if (!_isServer)
                return;

            Logger.Log($"CustomPlayerSync: Sending existing player data to peer {targetPeerId}");

            // Loop through all players and send their data to the new player
            foreach (var syncData in _playerSyncData)
            {
                // Skip if this is the target player (they don't need their own data)
                if (syncData.Key == targetPeerId)
                    continue;

                RpcId(targetPeerId, MethodName.ReceivePlayerData,
                    syncData.Key,
                    syncData.Value.Position,
                    syncData.Value.Velocity,
                    syncData.Value.HeadPosition,
                    syncData.Value.HeadRotation,
                    syncData.Value.LeftHandPosition,
                    syncData.Value.LeftHandRotation,
                    syncData.Value.RightHandPosition,
                    syncData.Value.RightHandRotation,
                    syncData.Value.HipPosition,
                    syncData.Value.HipRotation,
                    syncData.Value.LeftFootPosition,
                    syncData.Value.LeftFootRotation,
                    syncData.Value.RightFootPosition,
                    syncData.Value.RightFootRotation,
                    syncData.Value.UserHeight,
                    syncData.Value.MovementInput,
                    syncData.Value.MovementButtons);
            }
        }

        private void OnPeerDisconnected(long id)
        {
            // Remove sync data for this peer
            if (_playerSyncData.ContainsKey((int)id))
            {
                _playerSyncData.Remove((int)id);
                Logger.Log($"CustomPlayerSync: Removed sync data for disconnected peer {id}");
            }
        }

        [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
        private void SyncPlayerData(
            int playerId,
            Vector3 position,
            Vector3 velocity,
            Vector3 headPosition,
            Quaternion headRotation,
            Vector3 leftHandPosition,
            Quaternion leftHandRotation,
            Vector3 rightHandPosition,
            Quaternion rightHandRotation,
            Vector3 hipPosition,
            Quaternion hipRotation,
            Vector3 leftFootPosition,
            Quaternion leftFootRotation,
            Vector3 rightFootPosition,
            Quaternion rightFootRotation,
            float userHeight,
            Vector2 movementInput,
            byte movementButtons)
        {
            // Only the server should receive this RPC
            if (!_isServer)
                return;

            // Update the sync data
            if (!_playerSyncData.ContainsKey(playerId))
            {
                _playerSyncData[playerId] = new PlayerSyncData();
            }

            var syncData = _playerSyncData[playerId];
            syncData.Position = position;
            syncData.Velocity = velocity;
            syncData.HeadPosition = headPosition;
            syncData.HeadRotation = headRotation;
            syncData.LeftHandPosition = leftHandPosition;
            syncData.LeftHandRotation = leftHandRotation;
            syncData.RightHandPosition = rightHandPosition;
            syncData.RightHandRotation = rightHandRotation;
            syncData.HipPosition = hipPosition;
            syncData.HipRotation = hipRotation;
            syncData.LeftFootPosition = leftFootPosition;
            syncData.LeftFootRotation = leftFootRotation;
            syncData.RightFootPosition = rightFootPosition;
            syncData.RightFootRotation = rightFootRotation;
            syncData.UserHeight = userHeight;
            syncData.MovementInput = movementInput;
            syncData.MovementButtons = movementButtons;
            syncData.LastUpdateTime = Time.GetTicksMsec() / 1000.0;

            // Broadcast to all other peers
            foreach (var peerId in Multiplayer.GetPeers())
            {
                if (peerId != Multiplayer.GetRemoteSenderId() && peerId != 1) // Skip sender and server
                {
                    RpcId(peerId, MethodName.ReceivePlayerData,
                        playerId,
                        position,
                        velocity,
                        headPosition,
                        headRotation,
                        leftHandPosition,
                        leftHandRotation,
                        rightHandPosition,
                        rightHandRotation,
                        hipPosition,
                        hipRotation,
                        leftFootPosition,
                        leftFootRotation,
                        rightFootPosition,
                        rightFootRotation,
                        userHeight,
                        movementInput,
                        movementButtons);
                }
            }
        }

        [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
        private void ReceivePlayerData(
            int playerId,
            Vector3 position,
            Vector3 velocity,
            Vector3 headPosition,
            Quaternion headRotation,
            Vector3 leftHandPosition,
            Quaternion leftHandRotation,
            Vector3 rightHandPosition,
            Quaternion rightHandRotation,
            Vector3 hipPosition,
            Quaternion hipRotation,
            Vector3 leftFootPosition,
            Quaternion leftFootRotation,
            Vector3 rightFootPosition,
            Quaternion rightFootRotation,
            float userHeight,
            Vector2 movementInput,
            byte movementButtons)
        {
            // Skip if this is our local player
            if (playerId == Multiplayer.GetUniqueId())
                return;

            // Update the sync data
            if (!_playerSyncData.ContainsKey(playerId))
            {
                _playerSyncData[playerId] = new PlayerSyncData();
            }

            var syncData = _playerSyncData[playerId];
            syncData.Position = position;
            syncData.Velocity = velocity;
            syncData.HeadPosition = headPosition;
            syncData.HeadRotation = headRotation;
            syncData.LeftHandPosition = leftHandPosition;
            syncData.LeftHandRotation = leftHandRotation;
            syncData.RightHandPosition = rightHandPosition;
            syncData.RightHandRotation = rightHandRotation;
            syncData.HipPosition = hipPosition;
            syncData.HipRotation = hipRotation;
            syncData.LeftFootPosition = leftFootPosition;
            syncData.LeftFootRotation = leftFootRotation;
            syncData.RightFootPosition = rightFootPosition;
            syncData.RightFootRotation = rightFootRotation;
            syncData.UserHeight = userHeight;
            syncData.MovementInput = movementInput;
            syncData.MovementButtons = movementButtons;
            syncData.LastUpdateTime = Time.GetTicksMsec() / 1000.0;
        }
    }
}
