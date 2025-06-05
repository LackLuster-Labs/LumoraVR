using LumoraVR.Source.Logging;
using LumoraVR.Source.Scene.RootObjects;
using Godot;
using System;
using System.Linq;

namespace LumoraVR.Source.Management
{
    public partial class PlayerManager : Node
    {
        public static PlayerManager Instance;
        [Export] public Node3D PlayerRoot;
        [Export] public CustomPlayerSpawner PlayerSpawner;

        public override void _Ready()
        {
            Instance = this;
            try
            {
                // Find the PlayerRoot
                PlayerRoot = GetNodeOrNull<Node3D>("%PlayerRoot");
                if (PlayerRoot == null)
                {
                    // Try to find PlayerRoot in the scene
                    PlayerRoot = GetNodeOrNull<Node3D>("/root/Scene/Level/PlayerRoot");

                    if (PlayerRoot == null)
                    {
                        // Create a new PlayerRoot if it doesn't exist
                        PlayerRoot = new Node3D { Name = "PlayerRoot" };
                        AddChild(PlayerRoot);
                        Logger.Log("Created new PlayerRoot node");
                    }
                    else
                    {
                        Logger.Log("Found PlayerRoot in scene");
                    }
                }
                else
                {
                    Logger.Log("PlayerManager initialized with PlayerRoot: " + PlayerRoot.Name);
                }

                // Set up CustomPlayerSpawner
                SetupCustomPlayerSpawner();
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in PlayerManager._Ready(): {ex.Message}");
            }
        }

        private void SetupCustomPlayerSpawner()
        {
            try
            {
                // If PlayerSpawner is already assigned in editor, use that
                if (PlayerSpawner != null)
                {
                    // Make sure it's properly configured
                    if (PlayerRoot != null)
                    {
                        PlayerSpawner.SpawnRootPath = PlayerRoot.GetPath();
                        PlayerSpawner.PlayerScene = PlayerCharacterController.PackedScene;
                    }
                    Logger.Log("Using pre-assigned CustomPlayerSpawner");
                    return;
                }

                // Try to find existing CustomPlayerSpawner in scene
                PlayerSpawner = GetNodeOrNull<CustomPlayerSpawner>("%CustomPlayerSpawner");
                if (PlayerSpawner != null)
                {
                    if (PlayerRoot != null)
                    {
                        PlayerSpawner.SpawnRootPath = PlayerRoot.GetPath();
                        PlayerSpawner.PlayerScene = PlayerCharacterController.PackedScene;
                    }
                    Logger.Log("Found existing CustomPlayerSpawner in scene");
                    return;
                }

                // Check if MultiplayerScene already has a CustomPlayerSpawner
                if (MultiplayerScene.Instance?.PlayerSpawner is CustomPlayerSpawner customSpawner)
                {
                    PlayerSpawner = customSpawner;
                    Logger.Log("Using CustomPlayerSpawner from MultiplayerScene");
                    return;
                }

                // Create a new CustomPlayerSpawner
                PlayerSpawner = new CustomPlayerSpawner
                {
                    Name = "CustomPlayerSpawner",
                    SpawnRootPath = PlayerRoot.GetPath(),
                    PlayerScene = PlayerCharacterController.PackedScene
                };
                AddChild(PlayerSpawner);
                Logger.Log("Created new CustomPlayerSpawner");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting up CustomPlayerSpawner: {ex.Message}");
            }
        }

        public void SpawnPlayer(int authority, Vector3 position)
        {
            try
            {
                if (PlayerRoot == null)
                {
                    Logger.Error($"Cannot spawn player with authority {authority}: PlayerRoot is null");
                    return;
                }

                // If we have a CustomPlayerSpawner, use it
                if (PlayerSpawner != null)
                {
                    PlayerSpawner.SpawnPlayer(authority, position);
                    Logger.Log($"Player with authority {authority} spawned via CustomPlayerSpawner at position {position}.");
                    return;
                }

                // Fallback to original implementation
                var charController = PlayerCharacterController.PackedScene.Instantiate<PlayerCharacterController>();
                charController.SetPlayerAuthority(authority);
                charController.Name = authority.ToString();
                PlayerRoot.CallDeferred(Node.MethodName.AddChild, charController);
                CallDeferred(nameof(SetPlayerPosition), charController, position);
                Logger.Log($"Player with authority {authority} spawn requested at position {position} via legacy method.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error spawning player with authority {authority}: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes a player with the specified authority ID
        /// </summary>
        public void RemovePlayer(int authority)
        {
            try
            {
                // If we have a CustomPlayerSpawner, use it
                if (PlayerSpawner != null)
                {
                    PlayerSpawner.RemovePlayer(authority);
                    Logger.Log($"Player with authority {authority} removed via CustomPlayerSpawner.");
                    return;
                }

                // Fallback to direct removal
                if (PlayerRoot == null)
                {
                    Logger.Error($"Cannot remove player with authority {authority}: PlayerRoot is null");
                    return;
                }

                var player = GetPlayer(authority);
                if (player != null)
                {
                    PlayerRoot.RemoveChild(player);
                    player.QueueFree();
                    Logger.Log($"Player with authority {authority} removed via legacy method.");
                }
                else
                {
                    Logger.Warn($"Player with authority {authority} not found for removal.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error removing player with authority {authority}: {ex.Message}");
            }
        }

        private void SetPlayerPosition(PlayerCharacterController player, Vector3 position)
        {
            if (IsInstanceValid(player))
            {
                player.GlobalPosition = position;
                Logger.Log($"Player position set to {position}");
            }
        }

        public PlayerCharacterController GetPlayer(int id)
        {
            // If we have a CustomPlayerSpawner, use its tracking
            if (PlayerSpawner != null)
            {
                var player = PlayerSpawner.GetPlayer(id);
                if (player != null)
                {
                    return player;
                }
            }

            // Fallback to original implementation
            if (PlayerRoot == null)
            {
                Logger.Warn($"Cannot get player with id {id}: PlayerRoot is null");
                return null;
            }
            return PlayerRoot.GetChildren().OfType<PlayerCharacterController>().FirstOrDefault(i => i.Authority == id);
        }

        /// <summary>
        /// Gets the local player controller
        /// </summary>
        public PlayerCharacterController GetLocalPlayer()
        {
            return GetPlayer(Multiplayer.GetUniqueId());
        }

        /// <summary>
        /// Checks if a player with the specified ID exists
        /// </summary>
        public bool PlayerExists(int id)
        {
            return GetPlayer(id) != null;
        }
    }
}
