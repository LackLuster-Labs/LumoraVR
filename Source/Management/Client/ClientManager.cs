using System;
using Godot;
using System.Threading.Tasks;
using Aquamarine.Source.Input;
using Aquamarine.Source.Logging;
using Aquamarine.Source.Networking;
using Aquamarine.Source.Management.World;
using Bones.Core;

namespace Aquamarine.Source.Management
{
    public partial class ClientManager : Node
    {
        public static ClientManager Instance;
        public static bool ShowDebug = true;

        private XRInterface _xrInterface;
        private IInputProvider _input;
        private LiteNetLibMultiplayerPeer _peer;

        [Export] private Node3D _inputRoot;
        [Export] private Node _worldSession;

        private MultiplayerScene _multiplayerScene;
        private bool _isDirectConnection = false;

        public override void _Ready()
        {
            Instance = this;

            try
            {
                InitializeLocalDatabase();
                InitializeLoginManager();
                InitializeInput();
                InitializeDiscordManager();

                // Find the MultiplayerScene component in the world session
                if (_worldSession != null)
                {
                    _multiplayerScene = _worldSession.GetNode<MultiplayerScene>(".");
                    if (_multiplayerScene == null)
                    {
                        Logger.Error("Could not find MultiplayerScene component in _worldSession");
                        return; // Exit early to prevent further errors
                    }

                    // Only proceed with server operations if we have a valid MultiplayerScene
                    FetchServerInfo();

                    // Connect to local server
                    ConnectToLocalServer();
                }
                else
                {
                    Logger.Error("_worldSession is null. Make sure it's properly assigned in the scene editor.");
                    return; // Exit early to prevent further errors
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error initializing ClientManager: {ex.Message}");
            }
        }

        public override void _Input(InputEvent @event)
        {
            base._Input(@event);

            if (@event.IsActionPressed("ToggleDebug"))
            {
                ShowDebug = !ShowDebug;
            }

            // Test world loading - for development purposes
            if (@event.IsActionPressed("ui_home"))
            {
                LoadWorld("local_home");
            }
            else if (@event.IsActionPressed("ui_end"))
            {
                LoadWorld("multiplayer_base");
            }
        }

        private void ConnectToLocalServer()
        {
            OS.CreateProcess(OS.GetExecutablePath(), ["--run-home-server", "--xr-mode", "off", "--headless"]);

            this.CreateTimer(0.5f, () =>
            {
                JoinServer("localhost", 6000);
            });
        }

    }
}
