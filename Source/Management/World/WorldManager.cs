using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aquamarine.Source.Logging;

namespace Aquamarine.Source.Management.World
{
    public partial class WorldManager : Node
    {
        public static WorldManager Instance { get; private set; }

        [Signal]
        public delegate void WorldLoadingStartedEventHandler(string worldId);

        [Signal]
        public delegate void WorldLoadingProgressEventHandler(string worldId, float progress);

        [Signal]
        public delegate void WorldLoadingCompletedEventHandler(string worldId);

        [Signal]
        public delegate void WorldLoadingFailedEventHandler(string worldId, string error);

        [Export] public NodePath WorldContainerPath;
        [Export] public NodePath PlayerRootPath;
        [Export] public PackedScene LoadingWorldScene;
        [Export] public PackedScene LocalHomeScene;

        private Node _worldContainer;
        private Node _playerRoot;
        private Node _currentWorldInstance;
        private string _currentWorldId;
        private bool _isLoading = false;

        private Dictionary<string, PackedScene> _worldCache = new Dictionary<string, PackedScene>();

        private Dictionary<string, string> _builtInWorlds = new Dictionary<string, string>
        {
            { "local_home", "res://Scenes/World/LocalHome.tscn" },
            { "loading", "res://Scenes/World/LoadingWorld.tscn" },
            { "multiplayer_base", "res://Scenes/World/MultiplayerScene.tscn" }
        };

        public override void _Ready()
        {
            Instance = this;

            _worldContainer = GetNode(WorldContainerPath);
            _playerRoot = GetNode(PlayerRootPath);

            foreach (Node child in _worldContainer.GetChildren())
            {
                if (child != _playerRoot)
                {
                    _worldContainer.RemoveChild(child);
                    child.QueueFree();
                }
            }

            LoadWorld("local_home");
        }

        public bool LoadWorld(string worldId, bool forceReload = false)
        {
            if (_isLoading && !forceReload)
            {
                Logger.Warn($"Already loading a world. Cannot load {worldId}");
                return false;
            }

            if (worldId == _currentWorldId && !forceReload)
            {
                Logger.Log($"World {worldId} is already loaded");
                return true;
            }

            _isLoading = true;
            _currentWorldId = worldId;

            ShowLoadingWorld();

            _ = LoadWorldAsync(worldId);

            return true;
        }

        private void ShowLoadingWorld()
        {
            if (LoadingWorldScene != null)
            {
                ClearCurrentWorld();
                _currentWorldInstance = LoadingWorldScene.Instantiate();
                _worldContainer.AddChild(_currentWorldInstance);
            }
        }

        private async Task LoadWorldAsync(string worldId)
        {
            try
            {
                EmitSignal(SignalName.WorldLoadingStarted, worldId);
                PackedScene worldScene = null;

                if (_builtInWorlds.TryGetValue(worldId, out string worldPath))
                {
                    worldScene = ResourceLoader.Load<PackedScene>(worldPath);
                }
                else if (_worldCache.TryGetValue(worldId, out worldScene))
                {
                    // Use cached scene
                }
                else
                {
                    // Simulate loading time for testing
                    for (int i = 0; i <= 10; i++)
                    {
                        EmitSignal(SignalName.WorldLoadingProgress, worldId, i / 10.0f);
                        await Task.Delay(100);
                    }

                    // For now, just fall back to local home
                    worldScene = LocalHomeScene;
                    if (worldScene != null)
                    {
                        _worldCache[worldId] = worldScene;
                    }
                }

                if (worldScene != null)
                {
                    CallDeferred(nameof(DeferredSetWorld), worldScene);
                }
                else
                {
                    throw new Exception($"Failed to load world {worldId}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading world {worldId}: {ex.Message}");
                EmitSignal(SignalName.WorldLoadingFailed, worldId, ex.Message);

                if (worldId != "local_home")
                {
                    Logger.Log("Falling back to local home");
                    await LoadWorldAsync("local_home");
                }
                else
                {
                    Logger.Error("Failed to load local home. Application is in an unstable state.");
                }
            }
        }

        private void DeferredSetWorld(PackedScene worldScene)
        {
            ClearCurrentWorld();

            _currentWorldInstance = worldScene.Instantiate();
            _worldContainer.AddChild(_currentWorldInstance);

            _isLoading = false;

            EmitSignal(SignalName.WorldLoadingCompleted, _currentWorldId);
        }

        private void ClearCurrentWorld()
        {
            if (_currentWorldInstance != null && IsInstanceValid(_currentWorldInstance))
            {
                _worldContainer.RemoveChild(_currentWorldInstance);
                _currentWorldInstance.QueueFree();
                _currentWorldInstance = null;
            }
        }

        public void UnloadWorld(string worldId)
        {
            if (_worldCache.ContainsKey(worldId))
            {
                _worldCache.Remove(worldId);
            }
        }

        public void ClearWorldCache()
        {
            _worldCache.Clear();
        }
    }
}
