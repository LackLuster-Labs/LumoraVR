using Aquamarine.Source.Input;
using Aquamarine.Source.Logging;
using Godot;

namespace Aquamarine.Source.Management
{
    public partial class ClientManager
    {
        private void InitializeLocalDatabase()
        {
            if (LocalDatabase.Instance == null)
            {
                Logger.Log("LocalDatabase instance not found. Creating one...");

                var database = new LocalDatabase();
                AddChild(database);
                Logger.Log("LocalDatabase initialized successfully.");
            }
        }

        private void InitializeLoginManager()
        {
            if (LoginManager.Instance == null)
            {
                Logger.Log("LoginManager instance not found. Creating one...");

                var loginManager = new LoginManager();
                AddChild(loginManager);

                loginManager.OnLoginStatusChanged += (isLoggedIn) =>
                {
                    if (isLoggedIn)
                        Logger.Log("User logged in successfully.");
                    else
                        Logger.Log("User logged out.");
                };

                Logger.Log("LoginManager initialized successfully.");
            }
        }

        private void InitializeDiscordManager()
        {
            if (DiscordManager.Instance == null)
            {
                Logger.Log("DiscordManager instance not found. Creating one...");

                var discordManager = new DiscordManager();
                AddChild(discordManager);
                discordManager.InitializeDiscord();
            }

            DiscordManager.Instance.UpdatePresence("Starting Game", "Main Menu", "lumoravralpha", "Lumora VR");
        }

        private void InitializeInput()
        {
            _xrInterface = XRServer.FindInterface("OpenXR");

            if (IsInstanceValid(_xrInterface) && _xrInterface.IsInitialized())
            {
                DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
                GetViewport().UseXR = true;

                var vrInput = VRInput.PackedScene.Instantiate<VRInput>();
                _input = vrInput;
                _inputRoot.AddChild(vrInput);
                Logger.Log("XR interface initialized successfully.");
            }
            else
            {
                var desktopInput = DesktopInput.PackedScene.Instantiate<DesktopInput>();
                _input = desktopInput;
                _inputRoot.AddChild(desktopInput);
                Logger.Log("Desktop interface initialized successfully.");
            }

            // Initialize voice chat after input is set up
            InitializeVoiceChat();
        }
    }
}
