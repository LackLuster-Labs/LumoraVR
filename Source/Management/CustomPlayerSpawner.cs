using System;
using System.Collections.Generic;
using System.Linq;
using LumoraVR.Source.Logging;
using LumoraVR.Source.Scene.RootObjects;
using Bones.Core;
using Godot;

namespace LumoraVR.Source.Management
{
    [GlobalClass]
    public partial class CustomPlayerSpawner : Node
    {
        [Export] public NodePath SpawnRootPath;
        [Export] public PackedScene PlayerScene { get; set; }

        private Node _spawnRootNode;
        private Dictionary<int, PlayerCharacterController> _players = new();

        // Optional - can be used to apply different spawn locations
        [Export] public Node3D[] SpawnPoints { get; set; }
        private int _currentSpawnPointIndex = 0;

        [Signal]
        public delegate void PlayerSpawnedEventHandler(PlayerCharacterController player);

        [Signal]
        public delegate void PlayerRemovedEventHandler(int playerId);

        public override void _Ready()
        {
            // Get the spawn root node
            if (!string.IsNullOrEmpty(SpawnRootPath))
            {
                try
                {
                    _spawnRootNode = GetNode(SpawnRootPath);
                    Logger.Log($"CustomPlayerSpawner: Using SpawnRootPath: {SpawnRootPath}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"CustomPlayerSpawner: Error getting node at path {SpawnRootPath}: {ex.Message}");

                    // Try to find PlayerRoot in common locations
                    _spawnRootNode = FindPlayerRoot();
                    if (_spawnRootNode != null)
                    {
                        Logger.Log($"CustomPlayerSpawner: Found PlayerRoot at {_spawnRootNode.GetPath()}");
                        SpawnRootPath = _spawnRootNode.GetPath();
                    }
                    else
                    {
                        _spawnRootNode = this; // Use self as spawn root if none found
                        Logger.Log("CustomPlayerSpawner: No PlayerRoot found, using self as spawn root");
                    }
                }
            }
            else
            {
                // Try to find PlayerRoot in common locations
                _spawnRootNode = FindPlayerRoot();
                if (_spawnRootNode != null)
                {
                    Logger.Log($"CustomPlayerSpawner: Found PlayerRoot at {_spawnRootNode.GetPath()}");
                    SpawnRootPath = _spawnRootNode.GetPath();
                }
                else
                {
                    _spawnRootNode = this; // Use self as spawn root if none found
                    Logger.Log("CustomPlayerSpawner: No SpawnRootPath set and no PlayerRoot found, using self as spawn root");
                }
            }

            // Validate player scene 
            if (PlayerScene == null)
            {
                PlayerScene = ResourceLoader.Load<PackedScene>("res://Scenes/Objects/RootObjects/PlayerCharacterController.tscn");
                if (PlayerScene == null)
                {
                    Logger.Error("CustomPlayerSpawner: No player scene specified and default scene couldn't be loaded.");
                }
                else
                {
                    Logger.Log("CustomPlayerSpawner: Loaded default player scene");
                }
            }

            Logger.Log("CustomPlayerSpawner initialized.");
        }

        private Node FindPlayerRoot()
        {
            // Try to find PlayerRoot in common locations
            string[] possiblePaths = new[] {
                "/root/Scene/PlayerRoot",
                "/root/Root/WorldRoot/Scene/PlayerRoot",
                "/root/Root/Scene/PlayerRoot",
                "/root/Root/PlayerRoot",
                "../PlayerRoot",
                "PlayerRoot",
                "%PlayerRoot"
            };

            foreach (var path in possiblePaths)
            {
                try
                {
                    if (path.StartsWith("%"))
                    {
                        // Try to find by unique name
                        var node = GetNode(path);
                        if (node != null)
                        {
                            return node;
                        }
                    }
                    else
                    {
                        // Try to find by path
                        var node = GetNodeOrNull(path);
                        if (node != null)
                        {
                            return node;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error checking path {path}: {ex.Message}");
                }
            }

            // Try to find in the scene tree by name
            var root = GetTree().Root;
            var playerRoot = FindNodeByName(root, "PlayerRoot");
            if (playerRoot != null)
            {
                return playerRoot;
            }

            return null;
        }

        private Node FindNodeByName(Node root, string name)
        {
            if (root.Name == name)
            {
                return root;
            }

            foreach (var child in root.GetChildren())
            {
                var result = FindNodeByName(child, name);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        /// <summary>
        /// Spawn a player for the given peer ID
        /// </summary>
        public PlayerCharacterController SpawnPlayer(int peerId, Vector3 position = default)
        {
            if (PlayerScene == null)
            {
                Logger.Error($"Cannot spawn player for ID {peerId}: No player scene available");
                return null;
            }

            if (_spawnRootNode == null)
            {
                Logger.Error($"Cannot spawn player for ID {peerId}: Spawn root node is null");
                return null;
            }

            // Check if player already exists in the tracking dictionary
            if (_players.TryGetValue(peerId, out var existingPlayer) && IsInstanceValid(existingPlayer))
            {
                Logger.Log($"Player with ID {peerId} already exists in tracking dictionary, returning existing player");
                return existingPlayer;
            }

            // Check if player exists in the scene but not in our tracking dictionary
            if (_spawnRootNode != null)
            {
                var foundPlayer = _spawnRootNode.GetChildren()
                    .OfType<PlayerCharacterController>()
                    .FirstOrDefault(p => p.Authority == peerId);

                if (foundPlayer != null && IsInstanceValid(foundPlayer))
                {
                    Logger.Log($"Player with ID {peerId} found in scene but not in tracking dictionary, adding to tracking");
                    _players[peerId] = foundPlayer;
                    return foundPlayer;
                }
            }

            try
            {
                var player = PlayerScene.Instantiate<PlayerCharacterController>();
                if (player == null)
                {
                    Logger.Error($"Failed to instantiate player for ID {peerId}: Invalid scene type");
                    return null;
                }

                // Set up player properties
                player.SetPlayerAuthority(peerId);
                player.Name = peerId.ToString();

                // Handle spawn position
                Vector3 spawnPosition = position;
                if (spawnPosition == Vector3.Zero && SpawnPoints != null && SpawnPoints.Length > 0)
                {
                    // Use spawn points if available and position was not specified
                    spawnPosition = GetNextSpawnPoint()?.GlobalPosition ?? Vector3.Zero;
                }

                // Add to scene
                _spawnRootNode.AddChild(player);
                player.GlobalPosition = spawnPosition;

                // Track the player
                _players[peerId] = player;

                Logger.Log($"Player {peerId} spawned at position {spawnPosition}");
                EmitSignal(SignalName.PlayerSpawned, player);

                return player;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error spawning player for ID {peerId}: {ex.Message}\nStack trace: {ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Spawn a player and then call a setup action when it's ready
        /// </summary>
        public PlayerCharacterController SpawnPlayerWithSetup(int peerId, Vector3 position, Action<PlayerCharacterController> setupAction)
        {
            var player = SpawnPlayer(peerId, position);

            if (player != null && setupAction != null)
            {
                // Create a one-shot timer to delay setup
                var timer = new Timer
                {
                    WaitTime = 0.1f,
                    OneShot = true,
                    Autostart = true
                };
                AddChild(timer);
                timer.Timeout += () =>
                {
                    if (IsInstanceValid(player))
                    {
                        setupAction(player);
                    }
                    timer.QueueFree();
                };
            }

            return player;
        }

        /// <summary>
        /// Remove a player with the given peer ID
        /// </summary>
        public void RemovePlayer(int peerId)
        {
            try
            {
                if (!_players.TryGetValue(peerId, out var player))
                {
                    Logger.Log($"RemovePlayer: Player {peerId} not found in tracking dictionary");

                    // Try to find the player in the spawn root's children as a fallback
                    if (_spawnRootNode != null)
                    {
                        var foundPlayer = _spawnRootNode.GetChildren()
                            .OfType<PlayerCharacterController>()
                            .FirstOrDefault(p => p.Authority == peerId);

                        if (foundPlayer != null)
                        {
                            Logger.Log($"RemovePlayer: Found player {peerId} in spawn root children despite not being tracked");
                            player = foundPlayer;
                        }
                        else
                        {
                            Logger.Log($"RemovePlayer: Player {peerId} not found in spawn root children either");
                            return;
                        }
                    }
                    else
                    {
                        return; // No player and no spawn root
                    }
                }

                try
                {
                    // Remove from tracking
                    _players.Remove(peerId);

                    // Remove from scene safely
                    if (player != null && IsInstanceValid(player))
                    {
                        // First clean up any assets and child objects to avoid dangling references
                        if (player is PlayerCharacterController charController)
                        {
                            if (charController.Avatar != null && IsInstanceValid(charController.Avatar))
                            {
                                // Clean up avatar
                                foreach (var childObj in charController.Avatar.ChildObjects.Values.ToList())
                                {
                                    if (childObj != null && IsInstanceValid(childObj.Self))
                                    {
                                        childObj.Self.QueueFree();
                                    }
                                }
                                charController.Avatar.ChildObjects.Clear();
                                charController.Avatar.AssetProviders.Clear();
                                charController.Avatar.QueueFree();
                            }
                        }

                        player.GetParent().RemoveChild(player);
                        player.QueueFree();
                        Logger.Log($"Player {peerId} removed successfully");
                    }
                    else
                    {
                        Logger.Warn($"Player {peerId} was tracked but not valid/attached to scene");
                    }

                    EmitSignal(SignalName.PlayerRemoved, peerId);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error removing player for ID {peerId}: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in RemovePlayer: {ex.Message}");
            }
        }

        /// <summary>
        /// Get a player by peer ID
        /// </summary>
        public PlayerCharacterController GetPlayer(int peerId)
        {
            // First check the tracking dictionary
            if (_players.TryGetValue(peerId, out var player) && IsInstanceValid(player))
            {
                return player;
            }

            // Fallback: try to find in the spawn root's children
            if (_spawnRootNode != null)
            {
                var foundPlayer = _spawnRootNode.GetChildren()
                    .OfType<PlayerCharacterController>()
                    .FirstOrDefault(p => p.Authority == peerId);

                if (foundPlayer != null)
                {
                    // Update the tracking dictionary for future lookups
                    _players[peerId] = foundPlayer;
                    return foundPlayer;
                }
            }

            return null;
        }

        /// <summary>
        /// Get all currently spawned players
        /// </summary>
        public IReadOnlyDictionary<int, PlayerCharacterController> GetAllPlayers()
        {
            // If spawn root exists, refresh the player cache first
            if (_spawnRootNode != null)
            {
                var foundPlayers = _spawnRootNode.GetChildren()
                    .OfType<PlayerCharacterController>()
                    .ToDictionary(p => p.Authority);

                // Add players from spawn root that aren't already tracked
                foreach (var kvp in foundPlayers)
                {
                    if (!_players.ContainsKey(kvp.Key))
                    {
                        _players[kvp.Key] = kvp.Value;
                    }
                }

                // Remove players from tracking that are no longer in the scene
                var keysToRemove = _players.Keys
                    .Where(k => !foundPlayers.ContainsKey(k))
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    _players.Remove(key);
                }
            }

            return _players;
        }

        /// <summary>
        /// Clear all spawned players
        /// </summary>
        public void ClearAllPlayers()
        {
            foreach (var peerId in _players.Keys.ToArray())
            {
                RemovePlayer(peerId);
            }

            _players.Clear();
            Logger.Log("All players cleared");
        }

        /// <summary>
        /// Update the spawn root node path (useful if it changes during runtime)
        /// </summary>
        public void SetSpawnRoot(NodePath path)
        {
            SpawnRootPath = path;
            _spawnRootNode = GetNode(path);
            Logger.Log($"CustomPlayerSpawner: Updated spawn root to {path}");
        }

        private Node3D GetNextSpawnPoint()
        {
            if (SpawnPoints == null || SpawnPoints.Length == 0)
            {
                return null;
            }

            var spawnPoint = SpawnPoints[_currentSpawnPointIndex];
            _currentSpawnPointIndex = (_currentSpawnPointIndex + 1) % SpawnPoints.Length;
            return spawnPoint;
        }
    }
}
