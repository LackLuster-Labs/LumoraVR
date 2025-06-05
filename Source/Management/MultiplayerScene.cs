using System;
using System.Collections.Generic;
using System.Linq;
using LumoraVR.Source.Helpers;
using LumoraVR.Source.Logging;
using LumoraVR.Source.Scene;
using LumoraVR.Source.Scene.RootObjects;
using LumoraVR.Source.Scene.UI;
using Godot;
using Godot.Collections;

namespace LumoraVR.Source.Management
{
    public partial class MultiplayerScene : Node
    {
        public static MultiplayerScene Instance;

        [Export] public CustomPlayerSpawner PlayerSpawner;
        [Export] public Node3D PlayerRoot;

        public System.Collections.Generic.Dictionary<int, PlayerInfo> PlayerList = new();
        public System.Collections.Generic.Dictionary<string, Prefab> Prefabs = new();

        private PlayerCharacterController _local;
        private CustomPlayerSpawner _customPlayerSpawner; // Cache reference to custom spawner if exists

        public PlayerCharacterController GetPlayer(int id)
        {
            // First try to get player via CustomPlayerSpawner if available
            if (_customPlayerSpawner != null)
            {
                var player = _customPlayerSpawner.GetPlayer(id);
                if (player != null)
                {
                    // Debug log only when player is found to reduce noise
                    Logger.Log($"GetPlayer({id}): Found player via CustomPlayerSpawner");
                    return player;
                }
            }

            // Fallback to old method
            if (PlayerRoot == null)
            {
                Logger.Error($"GetPlayer({id}): PlayerRoot is null");
                return null;
            }

            var tryFind = PlayerRoot.GetChildren().OfType<PlayerCharacterController>().FirstOrDefault(i => i.Authority == id);
            if (tryFind != null)
            {
                Logger.Log($"GetPlayer({id}): Found player via direct PlayerRoot search");
            }
            else
            {
                // Reduced logging level to avoid spam when called frequently
                Logger.Log($"GetPlayer({id}): Player not found");
            }
            return tryFind;
        }

        public PlayerCharacterController GetLocalPlayer()
        {
            if (IsInstanceValid(_local))
            {
                Logger.Log("GetLocalPlayer: Using cached local player");
                return _local;
            }

            Logger.Log("GetLocalPlayer: Local player not cached, searching...");
            if (Multiplayer?.MultiplayerPeer is not null)
            {
                var localId = Multiplayer.GetUniqueId();
                string message;
                if (_customPlayerSpawner is not null)
                {
                    Logger.Log($"GetLocalPlayer: Local ID is {localId}");
                    _local = _customPlayerSpawner.GetPlayer(localId);
                    message = "GetLocalPlayer: Found local player via CustomPlayerSpawner";
                }
                else
                {
                    _local = PlayerRoot.GetChildren()
        .OfType<PlayerCharacterController>()
        .FirstOrDefault(i => i.Authority == localId);
                    message = "GetLocalPlayer: Found local player via direct PlayerRoot search";
                }
                if (_local is not null)
                {
                    Logger.Log(message);
                }
            }
            return _local;
        }

        public override void _Ready()
        {
            base._Ready();

            try
            {
                // Check if Instance is already set to avoid duplicate initialization
                if (Instance != null && Instance != this)
                {
                    // If the existing instance is invalid or not in the tree, replace it
                    if (!GodotObject.IsInstanceValid(Instance) || !Instance.IsInsideTree())
                    {
                        Logger.Warn($"MultiplayerScene._Ready: Existing instance at {Instance.GetPath()} is invalid or not in tree, replacing with this instance at {GetPath()}");
                        Instance = this;
                    }
                    else
                    {
                        Logger.Warn($"MultiplayerScene._Ready: Another instance already exists at {Instance.GetPath()}, this instance at {GetPath()} will not override it");

                        // Don't return early - still set up this instance, just don't make it the global Instance
                        // This ensures this instance is still functional even if it's not the global singleton
                    }
                }
                else
                {
                    // Set this as the global instance
                    Instance = this;
                    Logger.Log($"MultiplayerScene._Ready: Set as global instance. Scene name = {Name}, Path = {GetPath()}");
                }

                SetupPlayerRoot();
                SetupCustomPlayerSpawner();

                // Comment out scene tree logging to reduce console spam
                // Logger.Log("Scene tree after setup:");
                // LogSceneTree(GetTree().Root);

                Logger.Log("MultiplayerScene initialized.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error initializing MultiplayerScene: {ex.Message}\nStack trace: {ex.StackTrace}");
            }
        }

        public override void _ExitTree()
        {
            base._ExitTree();

            // If this is the global instance, clear it when removed from tree
            if (Instance == this)
            {
                Logger.Log($"MultiplayerScene._ExitTree: Global instance at {GetPath()} is being removed from tree, clearing reference");
                Instance = null;
            }
        }

        public void InitializeForServer()
        {
            Logger.Log("MultiplayerScene initialized for server.");
        }

        private void SetupPlayerRoot()
        {
            Logger.Log("Setting up PlayerRoot...");

            // First check if it's already assigned in the editor
            if (PlayerRoot != null)
            {
                Logger.Log($"PlayerRoot already assigned in editor: {PlayerRoot.Name}, Path: {PlayerRoot.GetPath()}");
                return;
            }

            // Try to get from PlayerManager
            if (PlayerManager.Instance != null)
            {
                PlayerRoot = PlayerManager.Instance.PlayerRoot;
                Logger.Log($"PlayerRoot from PlayerManager: {(PlayerRoot != null ? PlayerRoot.Name + ", Path: " + PlayerRoot.GetPath() : "null")}");

                if (PlayerRoot != null)
                {
                    return;
                }
            }

            // Try to find in the scene tree
            PlayerRoot = GetNodeOrNull<Node3D>("%PlayerRoot");
            if (PlayerRoot != null)
            {
                Logger.Log($"Found PlayerRoot using %PlayerRoot: {PlayerRoot.Name}, Path: {PlayerRoot.GetPath()}");
                return;
            }

            // Try other common paths
            string[] possiblePaths = new[] {
                "/root/Scene/PlayerRoot",
                "/root/Root/PlayerRoot",
                "../PlayerRoot",
                "PlayerRoot"
            };

            foreach (var path in possiblePaths)
            {
                try
                {
                    PlayerRoot = GetNodeOrNull<Node3D>(path);
                    if (PlayerRoot != null)
                    {
                        Logger.Log($"Found PlayerRoot at path: {path}, Name: {PlayerRoot.Name}");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error checking path {path}: {ex.Message}");
                }
            }

            // Create a new PlayerRoot if not found
            Logger.Log("PlayerRoot not found, creating a new one");
            PlayerRoot = new Node3D { Name = "PlayerRoot" };
            AddChild(PlayerRoot);
            Logger.Log($"Created new PlayerRoot node, Path: {PlayerRoot.GetPath()}");
        }

        private void SetupCustomPlayerSpawner()
        {
            Logger.Log("Setting up CustomPlayerSpawner...");

            // Check if PlayerSpawner is already a CustomPlayerSpawner
            _customPlayerSpawner = PlayerSpawner as CustomPlayerSpawner;

            if (_customPlayerSpawner != null)
            {
                Logger.Log($"Found CustomPlayerSpawner in PlayerSpawner export: {_customPlayerSpawner.Name}, Path: {_customPlayerSpawner.GetPath()}");

                // Update its configuration
                if (PlayerRoot != null)
                {
                    _customPlayerSpawner.SpawnRootPath = PlayerRoot.GetPath();
                    _customPlayerSpawner.PlayerScene = PlayerCharacterController.PackedScene;
                    Logger.Log($"Updated CustomPlayerSpawner.SpawnRootPath to {PlayerRoot.GetPath()}");
                }
                else
                {
                    Logger.Error("Cannot update CustomPlayerSpawner: PlayerRoot is null");
                }
                return;
            }

            // Check if PlayerManager has a CustomPlayerSpawner
            if (PlayerManager.Instance?.PlayerSpawner != null)
            {
                _customPlayerSpawner = PlayerManager.Instance.PlayerSpawner;
                Logger.Log($"Using CustomPlayerSpawner from PlayerManager: {_customPlayerSpawner.Name}, Path: {_customPlayerSpawner.GetPath()}");

                // Update its configuration
                if (PlayerRoot != null)
                {
                    _customPlayerSpawner.SpawnRootPath = PlayerRoot.GetPath();
                    _customPlayerSpawner.PlayerScene = PlayerCharacterController.PackedScene;
                    Logger.Log($"Updated CustomPlayerSpawner.SpawnRootPath to {PlayerRoot.GetPath()}");
                }
                else
                {
                    Logger.Error("Cannot update CustomPlayerSpawner: PlayerRoot is null");
                }
                return;
            }

            // Try to find in the scene tree
            _customPlayerSpawner = GetNodeOrNull<CustomPlayerSpawner>("%CustomPlayerSpawner");
            if (_customPlayerSpawner != null)
            {
                Logger.Log($"Found CustomPlayerSpawner in scene tree: {_customPlayerSpawner.Name}, Path: {_customPlayerSpawner.GetPath()}");

                // Update its configuration
                if (PlayerRoot != null)
                {
                    _customPlayerSpawner.SpawnRootPath = PlayerRoot.GetPath();
                    _customPlayerSpawner.PlayerScene = PlayerCharacterController.PackedScene;
                    Logger.Log($"Updated CustomPlayerSpawner.SpawnRootPath to {PlayerRoot.GetPath()}");
                }
                else
                {
                    Logger.Error("Cannot update CustomPlayerSpawner: PlayerRoot is null");
                }
                return;
            }

            // Create a new CustomPlayerSpawner if needed
            if (PlayerRoot != null)
            {
                _customPlayerSpawner = new CustomPlayerSpawner
                {
                    Name = "CustomPlayerSpawner",
                    SpawnRootPath = PlayerRoot.GetPath(),
                    PlayerScene = PlayerCharacterController.PackedScene
                };
                AddChild(_customPlayerSpawner);

                // Connect to signals
                _customPlayerSpawner.PlayerSpawned += OnCustomPlayerSpawned;
                _customPlayerSpawner.PlayerRemoved += OnCustomPlayerRemoved;

                Logger.Log($"Created new CustomPlayerSpawner, Path: {_customPlayerSpawner.GetPath()}, SpawnRootPath: {PlayerRoot.GetPath()}");
            }
            else
            {
                Logger.Error("Cannot create CustomPlayerSpawner: PlayerRoot is null");
            }
        }

        private void OnCustomPlayerSpawned(PlayerCharacterController player)
        {
            // Handle player spawned event
            if (player.Authority == Multiplayer.GetUniqueId())
            {
                _local = player;
                Logger.Log($"Local player spawned with ID {player.Authority}, Path: {player.GetPath()}");
            }
            else
            {
                Logger.Log($"Remote player spawned with ID {player.Authority}, Path: {player.GetPath()}");
            }

            // Ensure player is in the player list
            if (!PlayerList.ContainsKey(player.Authority))
            {
                PlayerList[player.Authority] = new PlayerInfo { Name = $"Player {player.Authority}" };
                SendUpdatedPlayerList();
            }
        }

        private void OnCustomPlayerRemoved(int playerId)
        {
            // Handle player removed event
            if (playerId == Multiplayer.GetUniqueId())
            {
                _local = null;
                Logger.Log($"Local player with ID {playerId} removed");
            }
            else
            {
                Logger.Log($"Remote player with ID {playerId} removed");
            }

            // Remove from player list
            PlayerList.Remove(playerId);
            SendUpdatedPlayerList();
        }


        public void SendUpdatedPlayerList()
        {
            if (!IsMultiplayerAuthority()) return;

            var dict = new Dictionary();

            foreach (var playerPair in PlayerList)
            {
                dict[playerPair.Key] = new Dictionary
                {
                    {"name", playerPair.Value.Name },
                };
            }

            Rpc(MethodName.UpdatePlayerList, dict);

            UpdatePlayerNametags();
        }

        public void SendUpdatedPlayerListTo(int targetPeerId)
        {
            if (!IsMultiplayerAuthority()) return;

            var dict = new Dictionary();

            foreach (var playerPair in PlayerList)
            {
                dict[playerPair.Key] = new Dictionary
                {
                    {"name", playerPair.Value.Name },
                };
            }

            RpcId(targetPeerId, MethodName.UpdatePlayerList, dict);

            // Also ensure their player is spawned
            if (PlayerList.TryGetValue(targetPeerId, out _) && !IsPlayerSpawned(targetPeerId))
            {
                SpawnPlayer(targetPeerId);
            }
        }

        [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferChannel = SerializationHelpers.SessionControlChannel, CallLocal = false)]
        public void RequestPlayerList()
        {
            if (!IsMultiplayerAuthority()) return;

            int requesterId = Multiplayer.GetRemoteSenderId();
            Logger.Log($"Received player list request from peer {requesterId}");

            SendUpdatedPlayerListTo(requesterId);
        }

        private void UpdatePlayerNametags()
        {
            foreach (var player in PlayerList)
            {
                var controller = GetPlayer(player.Key);
                if (controller?.Nametag is Nameplate nameplate)
                {
                    nameplate.SetText(player.Value.Name);
                }
            }
        }

        [Rpc(CallLocal = false, TransferChannel = SerializationHelpers.WorldUpdateChannel,
            TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
        private void UpdatePlayerList(Dictionary playerList)
        {
            try
            {
                lock (PlayerList)
                {
                    // Store old player IDs to track which ones are removed
                    HashSet<int> oldPlayerIds = PlayerList.Keys.ToHashSet();

                    // Clear the player list but don't remove players yet
                    PlayerList.Clear();

                    // Process the new player list
                    foreach (var pair in playerList)
                    {
                        var valueDict = pair.Value.AsGodotDictionary();
                        int id = pair.Key.AsInt32();

                        // Create player info
                        var playerInfo = new PlayerInfo
                        {
                            Name = valueDict["name"].AsString(),
                        };

                        // Add to player list
                        PlayerList.Add(id, playerInfo);

                        // Mark as processed
                        if (oldPlayerIds.Contains(id))
                        {
                            oldPlayerIds.Remove(id);
                        }
                        else
                        {
                            // This is a new player in the list
                            Logger.Log($"New player in list: {id}");

                            // Check if the player is already spawned
                            if (!IsPlayerSpawned(id))
                            {
                                // Only spawn if we're the server or if this is our local player
                                if (IsMultiplayerAuthority() || id == Multiplayer.GetUniqueId())
                                {
                                    Logger.Log($"Spawning player {id} because they're in the player list but not spawned");
                                    SpawnPlayer(id);
                                }
                            }
                        }
                    }

                    // Update nametags for all players
                    UpdatePlayerNametags();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in UpdatePlayerList: {ex.Message}\nStack trace: {ex.StackTrace}");
            }
        }

        [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferChannel = SerializationHelpers.SessionControlChannel, CallLocal = false)]
        public void DisconnectPlayer()
        {
            if (!IsMultiplayerAuthority()) return;

            var id = Multiplayer.GetRemoteSenderId();
            Logger.Log($"Player {id} has disconnected");

            Multiplayer.MultiplayerPeer.DisconnectPeer(id);
        }

        [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferChannel = SerializationHelpers.SessionControlChannel, CallLocal = false)]
        public void SetPlayerName(string name)
        {
            if (!IsMultiplayerAuthority()) return;

            if (!PlayerList.TryGetValue(Multiplayer.GetRemoteSenderId(), out var player)) return;

            player.Name = name;
            SendUpdatedPlayerList();
        }

        public void SendAllPrefabs(int? user = null)
        {
            if (!IsMultiplayerAuthority()) return;

            foreach (var prefab in Prefabs) SendPrefab(prefab.Key, user);
        }

        public void SendPrefab(string prefabName, int? user = null)
        {
            if (user.HasValue) RpcId(user.Value, MethodName.RecievePrefab, prefabName);
            else Rpc(MethodName.RecievePrefab, prefabName);
        }

        [Rpc(TransferChannel = SerializationHelpers.PrefabChannel)]
        private void RecievePrefab(string prefabName)
        {
            // Handle receiving prefab
        }

        public void RequestPrefab(string prefabName)
        {
            // Handle requesting prefab
        }

        [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = SerializationHelpers.WorldUpdateChannel)]
        private void InternalSpawnPlayer(int authority, Vector3 position)
        {
            try
            {
                Logger.Log($"InternalSpawnPlayer: Spawning player with authority {authority} at position {position}");

                // Use the CustomPlayerSpawner if available
                if (_customPlayerSpawner != null)
                {
                    _customPlayerSpawner.SpawnPlayer(authority, position);
                    Logger.Log($"Player with authority {authority} spawned via CustomPlayerSpawner from RPC");
                    return;
                }

                // Check if PlayerRoot is valid
                if (PlayerRoot == null)
                {
                    Logger.Error("InternalSpawnPlayer: PlayerRoot is null, creating a new one");
                    PlayerRoot = new Node3D { Name = "PlayerRoot" };
                    AddChild(PlayerRoot);
                }

                // Fallback to manual instantiation
                var charController = PlayerCharacterController.PackedScene.Instantiate<PlayerCharacterController>();
                charController.SetPlayerAuthority(authority);
                charController.Name = authority.ToString();
                PlayerRoot.AddChild(charController);
                charController.GlobalPosition = position;
                Logger.Log($"Player with authority {authority} spawned at position {position} via fallback method, Path: {charController.GetPath()}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error spawning player with authority {authority}: {ex.Message}\nStack trace: {ex.StackTrace}");
            }
        }

        private void InternalRemovePlayer(int authority)
        {
            try
            {
                // Use the CustomPlayerSpawner if available
                if (_customPlayerSpawner != null)
                {
                    _customPlayerSpawner.RemovePlayer(authority);
                    Logger.Log($"Player with authority {authority} removed via CustomPlayerSpawner");
                    return;
                }

                // Fallback to manual removal
                if (PlayerRoot == null)
                {
                    Logger.Error("InternalRemovePlayer: PlayerRoot is null");
                    return;
                }

                var charController = PlayerRoot.GetChildren().FirstOrDefault(i => i is PlayerCharacterController cont && cont.Authority == authority);
                if (charController is null)
                {
                    Logger.Warn($"Attempted to remove player with authority {authority}, but they were not found.");
                    return;
                }
                PlayerRoot.RemoveChild(charController);
                charController.QueueFree();
                Logger.Log($"Player with authority {authority} removed via fallback method.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error removing player with authority {authority}: {ex.Message}\nStack trace: {ex.StackTrace}");
            }
        }

        public void SpawnPlayer(int authority, Vector3? position = null)
        {
            try
            {
                Logger.Log($"SpawnPlayer: Attempting to spawn player with authority {authority} at position {position ?? Vector3.Zero}");

                // Ensure PlayerRoot exists
                if (PlayerRoot == null)
                {
                    Logger.Error("SpawnPlayer: PlayerRoot is null, creating a new one");
                    PlayerRoot = new Node3D { Name = "PlayerRoot" };
                    AddChild(PlayerRoot);
                }

                // Consolidated player existence check - only use GetPlayer as the single source of truth
                var existingPlayer = GetPlayer(authority);
                if (existingPlayer != null)
                {
                    Logger.Log($"Player with authority {authority} already exists, not spawning again");
                    return;
                }

                // Special handling for LocalHome server
                bool isLocalHomeServer = ServerManager.CurrentServerType == ServerManager.ServerType.Local;
                bool isLocalClient = authority == Multiplayer.GetUniqueId();

                // Primary spawning path - always try CustomPlayerSpawner first
                if (_customPlayerSpawner != null)
                {
                    Vector3 spawnPos = position ?? Vector3.Zero;
                    Logger.Log($"Spawning player {authority} via CustomPlayerSpawner at {spawnPos}");

                    var player = _customPlayerSpawner.SpawnPlayer(authority, spawnPos);
                    if (player != null)
                    {
                        Logger.Log($"Player with authority {authority} spawned via CustomPlayerSpawner");

                        // For local player in LocalHome server, cache the reference
                        if (isLocalHomeServer && isLocalClient)
                        {
                            _local = player;
                            Logger.Log($"LocalHome server: Cached local player with authority {authority}");
                        }

                        // Add to player list if not there already
                        if (!PlayerList.ContainsKey(authority))
                        {
                            PlayerList[authority] = new PlayerInfo { Name = $"Player {authority}" };
                            SendUpdatedPlayerList();
                        }

                        // Send RPC to other clients if we have authority
                        if (IsMultiplayerAuthority())
                        {
                            Rpc(MethodName.InternalSpawnPlayer, authority, spawnPos);
                            Logger.Log($"SpawnPlayer RPC sent for authority {authority}");
                        }

                        return;
                    }
                    else
                    {
                        Logger.Error($"CustomPlayerSpawner failed to spawn player with authority {authority}, trying fallback methods");
                    }
                }

                // Fallback path #1: PlayerManager
                if (PlayerManager.Instance != null)
                {
                    Logger.Log($"Attempting to spawn player {authority} via PlayerManager fallback");
                    PlayerManager.Instance.SpawnPlayer(authority, position ?? Vector3.Zero);
                    Logger.Log($"Player with authority {authority} spawned via PlayerManager");

                    // Add to player list if not there already
                    if (!PlayerList.ContainsKey(authority))
                    {
                        PlayerList[authority] = new PlayerInfo { Name = $"Player {authority}" };
                        SendUpdatedPlayerList();
                    }

                    // Send RPC to other clients if we have authority
                    if (IsMultiplayerAuthority())
                    {
                        Rpc(MethodName.InternalSpawnPlayer, authority, position ?? Vector3.Zero);
                        Logger.Log($"SpawnPlayer RPC sent for authority {authority}");
                    }

                    return;
                }

                // Fallback path #2: Direct instantiation
                Logger.Log($"Attempting to spawn player {authority} via direct instantiation fallback");
                InternalSpawnPlayer(authority, position ?? Vector3.Zero);
                Logger.Log($"Player with authority {authority} spawned directly via fallback");

                // Add to player list if not there already
                if (!PlayerList.ContainsKey(authority))
                {
                    PlayerList[authority] = new PlayerInfo { Name = $"Player {authority}" };
                    SendUpdatedPlayerList();
                }

                // Send RPC to other clients if we have authority
                if (IsMultiplayerAuthority())
                {
                    Rpc(MethodName.InternalSpawnPlayer, authority, position ?? Vector3.Zero);
                    Logger.Log($"SpawnPlayer RPC sent for authority {authority}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in SpawnPlayer: {ex.Message}\nStack trace: {ex.StackTrace}");
            }
        }

        public void RemovePlayer(int authority)
        {
            if (IsMultiplayerAuthority() || (Multiplayer.MultiplayerPeer is not null && Multiplayer.GetRemoteSenderId() == 1))
            {
                try
                {
                    // Use the CustomPlayerSpawner
                    if (_customPlayerSpawner != null)
                    {
                        _customPlayerSpawner.RemovePlayer(authority);
                        Logger.Log($"Player with authority {authority} removed via CustomPlayerSpawner");
                    }
                    // Try PlayerSpawner as fallback
                    else if (PlayerSpawner != null && PlayerSpawner is CustomPlayerSpawner customSpawner)
                    {
                        customSpawner.RemovePlayer(authority);
                        Logger.Log($"Player with authority {authority} removed via fallback PlayerSpawner");
                    }
                    // Fallback
                    else
                    {
                        InternalRemovePlayer(authority);
                        Logger.Log($"Player with authority {authority} removed via fallback method");
                    }

                    // Remove from player list
                    PlayerList.Remove(authority);
                    SendUpdatedPlayerList();
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error in RemovePlayer: {ex.Message}\nStack trace: {ex.StackTrace}");
                }
            }
            else
            {
                Logger.Warn($"RemovePlayer call ignored due to lack of multiplayer authority for authority {authority}.");
            }
        }

        public bool IsPlayerRootValid()
        {
            if (PlayerRoot == null)
            {
                Logger.Warn("IsPlayerRootValid: PlayerRoot is null");
                return false;
            }
            return true;
        }

        public bool IsPlayerSpawned(int authority)
        {
            Logger.Log($"IsPlayerSpawned: Checking if player with authority {authority} is spawned");

            // First check using our cached CustomPlayerSpawner reference for consistency
            if (_customPlayerSpawner != null)
            {
                var result = _customPlayerSpawner.GetPlayer(authority) != null;
                Logger.Log($"IsPlayerSpawned: Player with authority {authority} is {(result ? "spawned" : "not spawned")} according to _customPlayerSpawner");
                return result;
            }

            // Fallback to PlayerSpawner if the cached reference is null
            if (PlayerSpawner != null && PlayerSpawner is CustomPlayerSpawner customSpawner)
            {
                var result = customSpawner.GetPlayer(authority) != null;
                Logger.Log($"IsPlayerSpawned: Player with authority {authority} is {(result ? "spawned" : "not spawned")} according to PlayerSpawner");
                return result;
            }

            // Last resort: check PlayerRoot directly
            if (!IsPlayerRootValid())
            {
                Logger.Error("IsPlayerSpawned: PlayerRoot is not valid");
                return false;
            }

            var player = GetPlayer(authority);
            var result2 = player != null;
            Logger.Log($"IsPlayerSpawned: Player with authority {authority} is {(result2 ? "spawned" : "not spawned")} according to direct search");
            return result2;
        }

        private void LogSceneTree(Node node, string indent = "")
        {
            Logger.Log(indent + node.Name + " (" + node.GetType().Name + ")");
            foreach (var child in node.GetChildren())
            {
                LogSceneTree(child, indent + "  ");
            }
        }
    }
}
