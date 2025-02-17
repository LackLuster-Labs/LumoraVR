using System;
using Godot;
using System.Threading.Tasks;
using Aquamarine.Source.Input;
using Aquamarine.Source.Logging;
using Aquamarine.Source.Networking;
using Bones.Core;

namespace Aquamarine.Source.Management
{
    public partial class ClientManager : Node
    {
        public static ClientManager Instance;
        public static bool ShowDebug = true;

        private XRInterface _xrInterface;
        private IInputProvider _input;
        private LiteNetLibMultiplayerPeer  _peer;
        [Export] private Node3D _inputRoot;
        [Export] private MultiplayerScene _multiplayerScene;

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
                

                SpawnLocalHome();
            }
            catch (Exception ex)
            {
                Logger.Error($"Error initializing ClientManager: {ex.Message}");
            }
        }

        private VoiceManager _voiceManager;
        private bool _voiceChatEnabled = false;


        private void InitializeVoiceChat()
        {
            // Create VoiceManager node if it doesn't exist
            _voiceManager = new VoiceManager();
            AddChild(_voiceManager);
            // Pass required dependencies
            _voiceManager.Initialize(_peer, _multiplayerScene);

            // Initialize voice settings
            _voiceChatEnabled = false;
            Logger.Log("Voice chat system initialized.");
        }


        public override void _Input(InputEvent @event)
        {
            base._Input(@event);

            if (@event.IsActionPressed("ToggleDebug"))
            {
                ShowDebug = !ShowDebug;
            }
        }

        private void SpawnLocalHome()
        {
            OS.CreateProcess(OS.GetExecutablePath(), ["--run-home-server", "--xr-mode", "off", "--headless"]);

            this.CreateTimer(0.5f, () =>
            {
                JoinServer("localhost", 6000);
            });
        }
    }
}
