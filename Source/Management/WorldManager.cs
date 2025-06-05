using LumoraVR.Source.Logging;
using Bones.Core;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Resources;
using System.Text;
using System.Threading.Tasks;

namespace LumoraVR.Source.Management
{
    public partial class WorldManager : Node
    {
        public static WorldManager Instance;

        [Export] private Node _worldContainer;
        [Export] private Control _loadingScreen;
        [Export] private ProgressBar _progressBar;

        private string _currentWorldPath = "";
        private PackedScene _loadedWorld;
        private bool _isLoading = false;
        private float[] _progress = new float[1];

        [Signal]
        public delegate void WorldLoadedEventHandler();
        public override void _Ready()
        {
            Instance = this;

            // Defer node finding to prevent errors if nodes don't exist yet
            CallDeferred(nameof(InitializeComponents));
        }

        private void InitializeComponents()
        {
            try
            {
                // Try to find components, but don't crash if they don't exist
                _worldContainer = GetNodeOrNull<Node>("/root/Root/WorldRoot");
                if (_worldContainer == null)
                {
                    Logger.Error("WorldRoot node not found! World loading may not work properly.");
                }

                _loadingScreen = GetNodeOrNull<Control>("/root/Root/HUDManager/LoadingMenu");
                if (_loadingScreen == null)
                {
                    Logger.Error("LoadingMenu not found! Loading screen will not be displayed.");
                }
                else
                {
                    _loadingScreen.Visible = false;

                    _progressBar = _loadingScreen.GetNodeOrNull<ProgressBar>("ProgressBar");
                    if (_progressBar == null)
                    {
                        Logger.Error("ProgressBar not found in LoadingMenu! Progress will not be displayed.");
                    }
                }

                Logger.Log("WorldManager components initialized.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error initializing WorldManager components: {ex.Message}");
            }
        }

        public void LoadWorld(string worldPath, bool disconnectFromCurrent = true)
        {
            if (_isLoading) return;

            _isLoading = true;
            _currentWorldPath = worldPath;

            if (disconnectFromCurrent && ClientManager.Instance != null)
            {
                ClientManager.Instance.DisconnectFromCurrentServer();
            }

            // Only show loading screen if it exists
            if (_loadingScreen != null)
            {
                _loadingScreen.Visible = true;
            }

            // Only update progress bar if it exists
            if (_progressBar != null)
            {
                _progressBar.Value = 0;
            }

            ResourceLoader.LoadThreadedRequest(worldPath, "", true, ResourceLoader.CacheMode.Reuse);
            Logger.Log($"Started loading world: {worldPath}");
        }

        public override void _Process(double delta)
        {
            if (_isLoading)
            {
                UpdateLoadingProgress();
            }
        }

        private void UpdateLoadingProgress()
        {
            // Create a Godot.Collections.Array for the progress parameter
            var progressArray = new Godot.Collections.Array();
            progressArray.Add(0.0f); // Initialize with 0 progress

            var status = ResourceLoader.LoadThreadedGetStatus(_currentWorldPath, progressArray);

            // Extract the progress value from the Godot array
            float progress = 0;
            if (progressArray.Count > 0)
            {
                progress = (float)progressArray[0];
            }

            // Only update progress bar if it exists
            if (_progressBar != null)
            {
                _progressBar.Value = progress * 100;
            }

            if (status == ResourceLoader.ThreadLoadStatus.Loaded)
            {
                FinishWorldLoading();
            }
            else if (status == ResourceLoader.ThreadLoadStatus.Failed)
            {
                HandleLoadError();
            }
        }

        private async void FinishWorldLoading()
        {
            _isLoading = false;

            try
            {
                _loadedWorld = ResourceLoader.LoadThreadedGet(_currentWorldPath) as PackedScene;

                if (_worldContainer == null)
                {
                    // Try one more time to find world container
                    _worldContainer = GetNodeOrNull<Node>("/root/Root/WorldRoot");

                    if (_worldContainer == null)
                    {
                        Logger.Error("WorldRoot node still not found! Cannot load world.");
                        return;
                    }
                }

                // First, clear existing world
                foreach (Node child in _worldContainer.GetChildren())
                {
                    child.QueueFree();
                }

                // Wait a frame to ensure all nodes are properly freed
                await ToSignal(GetTree(), "process_frame");

                // Instantiate the new world
                Node newWorld = _loadedWorld.Instantiate();

                // Ensure the world has a unique name to avoid conflicts
                if (newWorld is MultiplayerScene)
                {
                    // If it's a MultiplayerScene, make sure it has a unique name
                    newWorld.Name = "Scene";
                    Logger.Log($"Instantiated MultiplayerScene with name: {newWorld.Name}");
                }

                // Add the new world to the container
                _worldContainer.AddChild(newWorld);

                // Comment out scene tree logging to reduce console spam
                // Logger.Log("Scene tree after loading world:");
                // LogSceneTree(GetTree().Root, "");

                // Hide loading screen if it exists
                if (_loadingScreen != null)
                {
                    _loadingScreen.Visible = false;
                }

                // Initialize server components if needed
                if (ServerManager.CurrentServerType != ServerManager.ServerType.NotAServer)
                {
                    // First try to get the MultiplayerScene directly
                    MultiplayerScene multiplayerScene = null;

                    if (newWorld is MultiplayerScene directScene)
                    {
                        multiplayerScene = directScene;
                    }
                    else
                    {
                        // Try to find it in the children
                        multiplayerScene = FindNodeByType<MultiplayerScene>(newWorld);
                    }

                    if (multiplayerScene != null)
                    {
                        // Initialize the server components
                        multiplayerScene.InitializeForServer();
                        Logger.Log("Server multiplayer components initialized successfully.");

                        // Notify ServerManager about the new world
                        var serverManager = GetNode<ServerManager>("/root/ServerManager");
                        if (serverManager != null)
                        {
                            serverManager.OnWorldLoaded(newWorld);
                        }
                    }
                    else
                    {
                        Logger.Error("MultiplayerScene not found in the loaded world.");
                    }
                }

                Logger.Log($"World loaded successfully: {_currentWorldPath}");
                EmitSignal(SignalName.WorldLoaded);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading world: {ex.Message}\nStack trace: {ex.StackTrace}");

                // Hide loading screen if it exists
                if (_loadingScreen != null)
                {
                    _loadingScreen.Visible = false;
                }
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

        // Helper method to log the scene tree for debugging
        private void LogSceneTree(Node node, string indent)
        {
            Logger.Log(indent + node.Name + " (" + node.GetType().Name + ")");
            foreach (var child in node.GetChildren())
            {
                LogSceneTree(child, indent + "  ");
            }
        }
        private void HandleLoadError()
        {
            _isLoading = false;
            Logger.Error($"Filed to LoadWorld: {_currentWorldPath}");

            // Hide loading screen if it exists
            if (_loadingScreen != null)
            {
                _loadingScreen.Visible = false;
            }

            ClientManager.Instance?.JoinLocalHome();
        }

    }
}
