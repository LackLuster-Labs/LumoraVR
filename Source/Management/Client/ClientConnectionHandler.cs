using System;
using LumoraVR.Source.Helpers;
using LumoraVR.Source.Logging;
using Godot;

namespace LumoraVR.Source.Management.Client
{
    [GlobalClass]
    public partial class ClientConnectionHandler : Node
    {
        private MultiplayerScene _multiplayerScene;
        private CustomPlayerSync _playerSync;
        private bool _isConnected = false;

        public override void _Ready()
        {
            // Connect to network peer connected signal
            Multiplayer.PeerConnected += OnPeerConnected;
            Multiplayer.PeerDisconnected += OnPeerDisconnected;

            // Find the MultiplayerScene
            _multiplayerScene = FindMultiplayerScene();

            // Find the CustomPlayerSync
            _playerSync = GetNodeOrNull<CustomPlayerSync>("%CustomPlayerSync");
            if (_playerSync == null)
            {
                _playerSync = GetNodeOrNull<CustomPlayerSync>("../CustomPlayerSync");
            }

            Logger.Log("ClientConnectionHandler initialized");
        }

        private bool _playerListRequested = false;

        private void OnPeerConnected(long id)
        {
            Logger.Log($"Client: Peer connected: {id}");

            // If this is the server connecting to us, we're now connected to the server
            if (id == 1 && !_isConnected)
            {
                _isConnected = true;

                // Reset the player list requested flag when connecting to a new server
                _playerListRequested = false;

                // Create a timer to request player list after a short delay
                // to ensure we're fully connected
                var timer = new Timer
                {
                    WaitTime = 1.0f,
                    OneShot = true,
                    Autostart = true
                };
                AddChild(timer);
                timer.Timeout += () =>
                {
                    RequestPlayerList();
                    timer.QueueFree();
                };
            }
        }

        private void OnPeerDisconnected(long id)
        {
            Logger.Log($"Client: Peer disconnected: {id}");

            // If the server disconnected from us
            if (id == 1)
            {
                _isConnected = false;
                Logger.Log("Client: Disconnected from server");
            }
        }

        private void RequestPlayerList()
        {
            if (!_isConnected)
            {
                Logger.Log("Client: Cannot request player list - not connected to server");
                return;
            }

            // Check if we've already requested the player list
            if (_playerListRequested)
            {
                Logger.Log("Client: Player list already requested, not requesting again");
                return;
            }

            Logger.Log("Client: Requesting player list from server");

            // Always get a fresh reference to MultiplayerScene to avoid using a disposed object
            _multiplayerScene = null; // Clear any potentially disposed reference
            _multiplayerScene = FindMultiplayerScene();

            // Request player list from the server
            if (_multiplayerScene != null && IsInstanceValid(_multiplayerScene))
            {
                try
                {
                    // Check if the node is still in the tree
                    if (_multiplayerScene.IsInsideTree())
                    {
                        _multiplayerScene.Rpc(MultiplayerScene.MethodName.RequestPlayerList);
                        Logger.Log("Client: Player list request sent via MultiplayerScene");
                        _playerListRequested = true;
                        return;
                    }
                    else
                    {
                        Logger.Warn("Client: MultiplayerScene found but not in tree, cannot send RPC");
                    }
                }
                catch (ObjectDisposedException)
                {
                    Logger.Warn("Client: MultiplayerScene was disposed, getting a new reference");
                    _multiplayerScene = null;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Client: Error requesting player list: {ex.Message}");
                }
            }

            // If we get here, we need to try a direct approach
            try
            {
                // Try to find the current active scene directly
                var currentScene = GetTree().CurrentScene;
                if (currentScene is MultiplayerScene currentMultiplayerScene)
                {
                    _multiplayerScene = currentMultiplayerScene;
                    Logger.Log("Client: Using current scene as MultiplayerScene");
                    _multiplayerScene.Rpc(MultiplayerScene.MethodName.RequestPlayerList);
                    Logger.Log("Client: Player list request sent via current scene");
                    _playerListRequested = true;
                    return;
                }

                // Try direct path as last resort
                var scene = GetTree().Root.GetNodeOrNull("Root/WorldRoot/Scene");
                if (scene is MultiplayerScene multiplayerScene && IsInstanceValid(scene))
                {
                    _multiplayerScene = multiplayerScene;
                    Logger.Log("Client: Found MultiplayerScene at Root/WorldRoot/Scene");

                    if (scene.IsInsideTree())
                    {
                        _multiplayerScene.Rpc(MultiplayerScene.MethodName.RequestPlayerList);
                        Logger.Log("Client: Player list request sent via direct path MultiplayerScene");
                        _playerListRequested = true;
                        return;
                    }
                }

                // If all else fails, try to send the RPC directly without a MultiplayerScene reference
                Logger.Log("Client: Attempting to send RequestPlayerList RPC directly");
                Rpc(MultiplayerScene.MethodName.RequestPlayerList);
                Logger.Log("Client: Player list request sent directly");
                _playerListRequested = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Client: Failed to request player list: {ex.Message}");
            }
        }

        private MultiplayerScene FindMultiplayerScene()
        {
            try
            {
                // Try to find using MultiplayerScene.Instance first
                if (MultiplayerScene.Instance != null && IsInstanceValid(MultiplayerScene.Instance))
                {
                    // Make sure it's not disposed and still in the tree
                    try
                    {
                        if (MultiplayerScene.Instance.IsInsideTree())
                        {
                            Logger.Log("ClientConnectionHandler: Found valid MultiplayerScene using MultiplayerScene.Instance");
                            return MultiplayerScene.Instance;
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        Logger.Warn("ClientConnectionHandler: MultiplayerScene.Instance was disposed");
                    }
                }

                // Try to find the current active scene
                var currentScene = GetTree().CurrentScene;
                if (currentScene is MultiplayerScene currentMultiplayerScene)
                {
                    Logger.Log("ClientConnectionHandler: Current scene is a MultiplayerScene");
                    return currentMultiplayerScene;
                }

                // Try to find in the scene tree with unique name
                var multiplayerScene = GetNodeOrNull<MultiplayerScene>("%MultiplayerScene");
                if (multiplayerScene != null && IsInstanceValid(multiplayerScene))
                {
                    Logger.Log("ClientConnectionHandler: Found MultiplayerScene using %MultiplayerScene");
                    return multiplayerScene;
                }

                // Try to find at the most common path
                var sceneNode = GetTree().Root.GetNodeOrNull("Root/WorldRoot/Scene");
                if (sceneNode is MultiplayerScene sceneAsMultiplayer && IsInstanceValid(sceneNode))
                {
                    Logger.Log("ClientConnectionHandler: Found MultiplayerScene at Root/WorldRoot/Scene");
                    return sceneAsMultiplayer;
                }

                // Search in the entire scene tree as last resort
                var root = GetTree().Root;
                multiplayerScene = FindNodeByType<MultiplayerScene>(root);
                if (multiplayerScene != null && IsInstanceValid(multiplayerScene))
                {
                    Logger.Log($"ClientConnectionHandler: Found MultiplayerScene by searching scene tree at {multiplayerScene.GetPath()}");
                    return multiplayerScene;
                }

                // Try to find in other common locations
                string[] possiblePaths = new[] {
                    "/root/Root/WorldRoot/@Node@119", // Based on the logs, this is where it might be
                    "/root/Scene",
                    "/root/Root/Scene",
                    "../Scene",
                    "../../Scene"
                };

                foreach (var path in possiblePaths)
                {
                    try
                    {
                        var node = GetNodeOrNull(path);
                        if (node is MultiplayerScene scene && IsInstanceValid(node))
                        {
                            Logger.Log($"ClientConnectionHandler: Found MultiplayerScene at path: {path}");
                            return scene;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error checking path {path}: {ex.Message}");
                    }
                }

                Logger.Error("ClientConnectionHandler: Could not find MultiplayerScene");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"ClientConnectionHandler: Error in FindMultiplayerScene: {ex.Message}");
                return null;
            }
        }

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
    }
}
