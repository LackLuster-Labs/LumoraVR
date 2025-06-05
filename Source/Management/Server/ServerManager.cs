using System;
using LumoraVR.Source.Logging;
using Godot;
using LiteNetLib;
using LumoraVR.Source.Networking;

namespace LumoraVR.Source.Management
{
    public partial class ServerManager : Node
    {
        [Export] public bool Advertise = true;
        // We'll use this field but find it dynamically at runtime
        private MultiplayerScene _multiplayerScene;

        public LiteNetLibMultiplayerPeer MultiplayerPeer;

        public NetManager SessionListManager;
        public EventBasedNetListener SessionListListener;
        public NetPeer MainServer;

        private string _sessionSecret;
        private bool _running = true;

        private int Port = 7000;
        private const int MaxConnections = 20;
        private const string SessionApiUrl = "https://api.xlinka.com/sessions";

        private string _publicIp;
        private string _worldName = "My World";
        private string _worldIdentifier = "placeholder";

        // Debug UI references
        private Label _serverTypeLabel;
        private Label _fpsLabel;
        private Label _playersLabel;
        private Label _portLabel;
        private Label _uptimeLabel;

        // Server stats
        private float _startTime;
        private float _elapsedTime;
        private int _frameCount;
        private float _fpsUpdateTimer;
        private float _currentFps;

        public override void _Process(double delta)
        {
            base._Process(delta);
            if (ServerManager.CurrentServerType == ServerType.Local && MultiplayerPeer != null)
            {
                MultiplayerPeer.Poll(); // Poll network events manually
            }
            SessionListManager?.PollEvents();

            // Update server stats
            _elapsedTime = (float)(Time.GetTicksMsec() / 1000.0) - _startTime;
            _frameCount++;
            _fpsUpdateTimer += (float)delta;

            // Update FPS every 0.5 seconds
            if (_fpsUpdateTimer >= 0.5f)
            {
                _currentFps = _frameCount / _fpsUpdateTimer;
                _frameCount = 0;
                _fpsUpdateTimer = 0;

                // Update debug UI
                UpdateDebugLabels();
            }
        }

        private void UpdateDebugLabels()
        {
            if (_fpsLabel != null)
            {
                _fpsLabel.Text = $"FPS: {_currentFps:F1}";
            }

            if (_uptimeLabel != null)
            {
                TimeSpan uptime = TimeSpan.FromSeconds(_elapsedTime);
                _uptimeLabel.Text = $"Uptime: {uptime:hh\\:mm\\:ss}";
            }

            if (_playersLabel != null && _multiplayerScene != null)
            {
                int playerCount = _multiplayerScene.PlayerList?.Count ?? 0;
                _playersLabel.Text = $"Players: {playerCount}";

                // Add player names if there are any
                if (playerCount > 0)
                {
                    _playersLabel.Text += " (";
                    int count = 0;
                    foreach (var player in _multiplayerScene.PlayerList)
                    {
                        _playersLabel.Text += player.Value.Name;
                        if (++count < playerCount)
                            _playersLabel.Text += ", ";
                    }
                    _playersLabel.Text += ")";
                }
            }
        }

        public override void _Ready()
        {
            try
            {
                // Initialize debug UI references
                _serverTypeLabel = GetNodeOrNull<Label>("%ServerTypeLabel");
                _fpsLabel = GetNodeOrNull<Label>("%FpsLabel");
                _playersLabel = GetNodeOrNull<Label>("%PlayersLabel");
                _portLabel = GetNodeOrNull<Label>("%PortLabel");
                _uptimeLabel = GetNodeOrNull<Label>("%UptimeLabel");

                // Initialize server stats
                _startTime = (float)(Time.GetTicksMsec() / 1000.0);
                _frameCount = 0;
                _fpsUpdateTimer = 0;
                _currentFps = 0;

                var serverType = ServerManager.CurrentServerType;

                // Initialize MultiplayerPeer
                MultiplayerPeer = new LiteNetLibMultiplayerPeer();
                // get port
                if ((ArgumentCache.Instance?.Arguments.TryGetValue("port", out string portstring) ?? false) &&
                    int.TryParse(portstring, out int parsed))
                    Port = parsed;

                // Update port label
                if (_portLabel != null)
                {
                    _portLabel.Text = $"Port: {Port}";
                }

                // Update server type label
                if (_serverTypeLabel != null)
                {
                    _serverTypeLabel.Text = $"Server Type: {serverType}";
                }

                Error error;
                switch (serverType)
                {
                    case ServerType.Local:
                        // Start local server
                        error = MultiplayerPeer.CreateServer(Port, 1);

                        // Check if server started successfully before setting it as the multiplayer peer
                        if (error == Error.Ok)
                        {
                            // Only set the multiplayer peer after it's properly initialized
                            Multiplayer.MultiplayerPeer = MultiplayerPeer;

                            // Now connect event handlers
                            MultiplayerPeer.PeerConnected += OnPeerConnected;
                            MultiplayerPeer.PeerDisconnected += OnPeerDisconnected;

                            Logger.Log($"Local server started on port {Port}.");

                            // Switch scene
                            WorldManager.Instance?.LoadWorld("res://Scenes/World/LocalHome.tscn");
                            Logger.Log("LocalHome Loaded.");

                            // Defer MultiplayerScene initialization until scene is fully loaded
                            CallDeferred(nameof(InitializeMultiplayerScene));
                        }
                        else
                        {
                            Logger.Error($"Failed to start local server: {error}");
                        }
                        break;
                    case ServerType.Standard:
                        // Start standard server
                        error = MultiplayerPeer.CreateServerNat(Port, MaxConnections);

                        // Check if server started successfully before setting it as the multiplayer peer
                        if (error == Error.Ok)
                        {
                            string worlduri;
                            if (ArgumentCache.Instance?.Arguments.TryGetValue("worlduri", out string uri) ?? false)
                                worlduri = uri;
                            else
                                worlduri = "res://Scenes/World/MultiplayerScene.tscn";
                            // Only set the multiplayer peer after it's properly initialized
                            Multiplayer.MultiplayerPeer = MultiplayerPeer;

                            // Now connect event handlers
                            MultiplayerPeer.PeerConnected += OnPeerConnected;
                            MultiplayerPeer.PeerDisconnected += OnPeerDisconnected;

                            Logger.Log($"Server started on port {Port} with max connections {MaxConnections}.");
                            WorldManager.Instance?.LoadWorld(worlduri);
                            // Defer MultiplayerScene initialization
                            CallDeferred(nameof(InitializeMultiplayerScene));

                            // Initialize session manager
                            SessionListListener = new EventBasedNetListener();
                            SessionListManager = new NetManager(SessionListListener)
                            {
                                IPv6Enabled = true,
                                PingInterval = 10000,
                            };
                            SessionListListener.NetworkReceiveEvent += SessionListListenerOnNetworkReceiveEvent;
                            SessionListListener.PeerDisconnectedEvent += SessionListListenerOnPeerDisconnectedEvent;
                            if (Helpers.SimpleIpHelpers.GetAvailablePortUdp(10) is not int port) throw new Exception("Failed to find available port (shit)");
                            SessionListManager.Start();
                            ConnectToSessionServer();
                        }
                        else
                        {
                            Logger.Error($"Failed to start standard server: {error}");
                        }
                        break;
                    default:
                        Logger.Error("Server type not recognized.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error initializing ServerManager: {ex}");
            }

            // Verify PlayerRoot
            if (PlayerManager.Instance?.PlayerRoot == null)
            {
                Logger.Error("PlayerRoot is not initialized in PlayerManager! Check Autoload settings.");
            }
            else
            {
                Logger.Log("PlayerRoot initialized successfully.");
            }
        }

        // Deferred method to initialize MultiplayerScene
        private void InitializeMultiplayerScene()
        {
            try
            {
                // First try direct access to MultiplayerScene
                var multiplayerScene = GetTree().CurrentScene.GetNodeOrNull<MultiplayerScene>("MultiplayerScene");

                if (multiplayerScene == null)
                {
                    // If not found directly, try deep search
                    multiplayerScene = FindMultiplayerSceneInChildren(GetTree().CurrentScene);
                }

                // If still not found, try to find it directly in the scene tree
                if (multiplayerScene == null)
                {
                    // Check if this is the LocalHome scene
                    if (GetTree().CurrentScene.Name == "Scene" && GetTree().CurrentScene.SceneFilePath.Contains("LocalHome.tscn"))
                    {
                        // For LocalHome.tscn, the scene itself is the MultiplayerScene
                        if (GetTree().CurrentScene is MultiplayerScene localHomeScene)
                        {
                            multiplayerScene = localHomeScene;
                            Logger.Log("Found MultiplayerScene: LocalHome scene is itself a MultiplayerScene");
                        }
                    }

                    // Try to find it at a common path
                    if (multiplayerScene == null)
                    {
                        var scene = GetTree().Root.GetNodeOrNull("Root/WorldRoot/Scene");
                        if (scene is MultiplayerScene sceneAsMultiplayer)
                        {
                            multiplayerScene = sceneAsMultiplayer;
                            Logger.Log("Found MultiplayerScene at Root/WorldRoot/Scene");
                        }
                    }
                }

                if (multiplayerScene != null)
                {
                    _multiplayerScene = multiplayerScene;
                    multiplayerScene.InitializeForServer();
                    Logger.Log("MultiplayerScene found and initialized for server.");

                    // Force spawn local player for LocalHome
                    if (ServerManager.CurrentServerType == ServerType.Local)
                    {
                        // Create a timer to spawn the local player after a short delay
                        var timer = new Timer
                        {
                            WaitTime = 0.5f,
                            OneShot = true,
                            Autostart = true
                        };
                        AddChild(timer);
                        timer.Timeout += () =>
                        {
                            // Check if player already exists
                            bool playerExists = false;
                            if (_multiplayerScene != null)
                            {
                                var existingPlayer = _multiplayerScene.GetPlayer(1);
                                playerExists = existingPlayer != null;

                                if (playerExists)
                                {
                                    Logger.Log("LocalHome: Server player (ID 1) already exists, not spawning again");
                                }
                                else
                                {
                                    // Spawn a player for the server itself (ID 1)
                                    _multiplayerScene.SpawnPlayer(1);
                                    Logger.Log("LocalHome: Spawned player for server (ID 1)");
                                }
                            }
                            timer.QueueFree();
                        };
                    }
                }
                else
                {
                    Logger.Error("MultiplayerScene not found in the loaded world.");

                    // Setup listener for scene changes
                    GetTree().Root.ChildEnteredTree += OnChildEnteredTree;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error initializing MultiplayerScene: {ex.Message}");
            }
        }

        // Helper method to recursively search for MultiplayerScene in children
        private MultiplayerScene FindMultiplayerSceneInChildren(Node node)
        {
            // First check if the current node has the MultiplayerScene script attached
            if (node is MultiplayerScene scene)
            {
                Logger.Log($"Found MultiplayerScene on node: {node.Name}, Path: {node.GetPath()}");
                return scene;
            }

            // Then check all children recursively
            foreach (var child in node.GetChildren())
            {
                var result = FindMultiplayerSceneInChildren(child);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        private void OnSceneChanged()
        {
            try
            {
                var multiplayerScene = GetTree().CurrentScene.GetNodeOrNull<MultiplayerScene>("MultiplayerScene");
                if (multiplayerScene == null)
                {
                    multiplayerScene = FindMultiplayerSceneInChildren(GetTree().CurrentScene);
                }

                if (multiplayerScene != null)
                {
                    _multiplayerScene = multiplayerScene;
                    multiplayerScene.InitializeForServer();
                    Logger.Log("MultiplayerScene initialized for server after scene change.");
                }
                else
                {
                    Logger.Error("MultiplayerScene not found in the loaded world after scene change.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in OnSceneChanged: {ex.Message}");
            }
        }

        private void OnChildEnteredTree(Node node)
        {
            if (node is MultiplayerScene multiplayerScene)
            {
                _multiplayerScene = multiplayerScene;
                multiplayerScene.InitializeForServer();
                Logger.Log("MultiplayerScene initialized through ChildEnteredTree event.");
                GetTree().Root.ChildEnteredTree -= OnChildEnteredTree;
            }
        }

        // Call this method when switching worlds at runtime
        public void OnWorldLoaded(Node worldNode)
        {
            try
            {
                var multiplayerScene = worldNode.GetNodeOrNull<MultiplayerScene>("MultiplayerScene");
                if (multiplayerScene == null)
                {
                    multiplayerScene = FindMultiplayerSceneInChildren(worldNode);
                }

                if (multiplayerScene != null)
                {
                    _multiplayerScene = multiplayerScene;
                    multiplayerScene.InitializeForServer();
                    Logger.Log("MultiplayerScene initialized in newly loaded world.");
                }
                else
                {
                    Logger.Error("MultiplayerScene not found in newly loaded world.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in OnWorldLoaded: {ex.Message}");
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
    }
}
