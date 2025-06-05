using LumoraVR.Source.Helpers;
using LumoraVR.Source.Input;
using LumoraVR.Source.Logging;
using LumoraVR.Source.Networking;
using LumoraVR.Source.Scene.RootObjects;
using Godot;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LumoraVR.Source.Management.HUD;

public partial class DebugOverlay : Control
{
    [Export] public RichTextLabel StatsText;
    [Export] public RichTextLabel ConsoleText;
    [Export] public LineEdit ConsoleInput;

    // UI Controls for settings tab
    [Export] public TabContainer TabContainer;
    [Export] public CheckBox VSyncCheckBox;
    [Export] public CheckBox DebugLinesCheckBox;
    [Export] public CheckBox ShadowsCheckBox;
    [Export] public CheckBox AmbientOcclusionCheckBox;
    [Export] public CheckBox SSReflectionsCheckBox;
    [Export] public CheckBox SSAOCheckBox;
    [Export] public CheckBox BloomCheckBox;
    [Export] public SpinBox MaxFPSSpinBox;

    private WorldEnvironment _worldEnvironment;
    private bool _settingsInitialized = false;

    private readonly StringBuilder _statsTextStringBuilder = new();
    private readonly StringBuilder _consoleTextStringBuilder = new();

    private const string BoolLabel = "  [[color=silver]bool[/color]]";
    private const string IntLabel = "  [[color=lime_green]int[/color]]";
    private const string FloatLabel = "  [[color=deep_sky_blue]float[/color]]";
    private const string Vector2Label = "  [[color=deep_sky_blue]vec2[/color]]";
    private const string Vector3Label = "  [[color=deep_sky_blue]vec3[/color]]";
    private const string StringLabel = "  [[color=red]string[/color]]";

    private const string BoolTrueValue = "[color=pale_green]true[/color]";
    private const string BoolFalseValue = "[color=indian_red]false[/color]";
    private const string IntValue = "[color=pale_green]{0}[/color]";
    private const string FloatValue = "[color=sky_blue]{0}[/color]";
    private const string Vector2Value = "([color=sky_blue]{0},{1}[/color])";
    private const string Vector3Value = "([color=sky_blue]{0},{1},{2}[/color])";
    private const string StringValue = "[color=indian_red]\"{0}\"[/color]";

    private WeakReference<PlayerCharacterController> playerref;
    private readonly PeriodicTimer _playerTimer = new(new(0, 0, 10));
    private CancellationTokenSource _cts;

    private static string DoBoolLabel(bool value) => value ? BoolTrueValue : BoolFalseValue;
    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == (int)NotificationPredelete)
        {
            Logger.OnPrettyLogMessageWritten -= OnLogMessageWritten;
            ConsoleInput.TextSubmitted -= OnConsoleInputSubmitted;
            _cts?.Cancel();
        }
    }
    public override void _Ready()
    {
        base._Ready();
        _cts = new();
        Logger.OnPrettyLogMessageWritten += OnLogMessageWritten;
        ConsoleInput.TextSubmitted += OnConsoleInputSubmitted;
        Visible = false;
        Logger.Log("DebugOverlay initialized.");

        // Initialize settings on first showing
        this.VisibilityChanged += OnVisibilityChanged;
        Task.Run(async () =>
        {
            await _playerTimer.WaitForNextTickAsync();
            while (!_cts.Token.IsCancellationRequested)
            {
                if ((playerref is not null ? !playerref.TryGetTarget(out var _) : true) && MultiplayerScene.Instance is MultiplayerScene mm)
                {
                    mm.RunOnNodeAsync(() =>
                    {
                        var thing = MultiplayerScene.Instance?.GetLocalPlayer();
                        if (thing is not null)
                            playerref = new(thing);
                    });
                }
                await _playerTimer.WaitForNextTickAsync();
            }
        });
    }

    private void OnVisibilityChanged()
    {
        if (Visible && !_settingsInitialized)
        {
            InitializeSettings();
        }
    }

    private void InitializeSettings()
    {
        try
        {
            // Find the WorldEnvironment - search in the scene tree
            try
            {
                // Try to find in common locations
                string[] possiblePaths = new[] {
                    "../WorldEnvironment",
                    "../../WorldEnvironment",
                    "/root/Root/WorldRoot/Scene/WorldEnvironment",
                    "/root/Scene/WorldEnvironment"
                };

                foreach (var path in possiblePaths)
                {
                    try
                    {
                        var node = GetNodeOrNull(path);
                        if (node is WorldEnvironment worldEnv)
                        {
                            _worldEnvironment = worldEnv;
                            Logger.Log($"Found WorldEnvironment at path: {path}");
                            break;
                        }
                    }
                    catch
                    {
                        // Continue to next path
                    }
                }

                // If not found by path, search the entire scene tree
                if (_worldEnvironment == null)
                {
                    _worldEnvironment = FindNodeByType<WorldEnvironment>(GetTree().Root);
                    if (_worldEnvironment != null)
                    {
                        Logger.Log($"Found WorldEnvironment by searching scene tree at {_worldEnvironment.GetPath()}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error finding WorldEnvironment: {ex.Message}");
            }

            try
            {
                // Init settings with current values
                if (VSyncCheckBox != null)
                    VSyncCheckBox.ButtonPressed = DisplayServer.WindowGetVsyncMode() != DisplayServer.VSyncMode.Disabled;

                if (DebugLinesCheckBox != null)
                    DebugLinesCheckBox.ButtonPressed = ClientManager.ShowDebug;

                if (MaxFPSSpinBox != null)
                    MaxFPSSpinBox.Value = Engine.MaxFps;

                if (_worldEnvironment != null && _worldEnvironment.Environment != null)
                {
                    var env = _worldEnvironment.Environment;

                    if (ShadowsCheckBox != null)
                        ShadowsCheckBox.ButtonPressed = env.SsrEnabled;

                    if (AmbientOcclusionCheckBox != null)
                        AmbientOcclusionCheckBox.ButtonPressed = env.SsaoEnabled;

                    if (SSReflectionsCheckBox != null)
                        SSReflectionsCheckBox.ButtonPressed = env.SsrEnabled;

                    if (SSAOCheckBox != null)
                        SSAOCheckBox.ButtonPressed = env.SsaoEnabled;

                    if (BloomCheckBox != null)
                        BloomCheckBox.ButtonPressed = env.GlowEnabled;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error initializing UI settings: {ex.Message}");
            }

            _settingsInitialized = true;
            Logger.Log("Debug settings initialized");
        }
        catch (Exception ex)
        {
            Logger.Error($"Error initializing debug settings: {ex.Message}");
        }
    }

    // Settings handlers
    public void OnVSyncToggled(bool toggled)
    {
        DisplayServer.WindowSetVsyncMode(toggled ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        Logger.Log($"VSync {(toggled ? "enabled" : "disabled")}");
    }

    public void OnDebugLinesToggled(bool toggled)
    {
        ClientManager.ShowDebug = toggled;
        Logger.Log($"Debug lines {(toggled ? "enabled" : "disabled")}");
    }

    public void OnMaxFPSChanged(double value)
    {
        int fps = (int)value;
        Engine.MaxFps = fps;
        Logger.Log($"Max FPS set to {fps}");
    }

    public void OnShadowsToggled(bool toggled)
    {
        if (_worldEnvironment != null && _worldEnvironment.Environment != null)
        {
            _worldEnvironment.Environment.SsrEnabled = toggled;
            Logger.Log($"Shadows {(toggled ? "enabled" : "disabled")}");
        }
    }

    public void OnAmbientOcclusionToggled(bool toggled)
    {
        if (_worldEnvironment != null && _worldEnvironment.Environment != null)
        {
            _worldEnvironment.Environment.SsaoEnabled = toggled;
            Logger.Log($"Ambient Occlusion {(toggled ? "enabled" : "disabled")}");
        }
    }

    public void OnSSReflectionsToggled(bool toggled)
    {
        if (_worldEnvironment != null && _worldEnvironment.Environment != null)
        {
            _worldEnvironment.Environment.SsrEnabled = toggled;
            Logger.Log($"Screen Space Reflections {(toggled ? "enabled" : "disabled")}");
        }
    }

    public void OnSSAOToggled(bool toggled)
    {
        if (_worldEnvironment != null && _worldEnvironment.Environment != null)
        {
            _worldEnvironment.Environment.SsaoEnabled = toggled;
            Logger.Log($"SSAO {(toggled ? "enabled" : "disabled")}");
        }
    }

    public void OnBloomToggled(bool toggled)
    {
        if (_worldEnvironment != null && _worldEnvironment.Environment != null)
        {
            _worldEnvironment.Environment.GlowEnabled = toggled;
            Logger.Log($"Bloom {(toggled ? "enabled" : "disabled")}");
        }
    }

    public void OnResetButtonPressed()
    {
        // Reset to default settings
        VSyncCheckBox.ButtonPressed = true;
        DebugLinesCheckBox.ButtonPressed = true;
        MaxFPSSpinBox.Value = 60;

        ShadowsCheckBox.ButtonPressed = true;
        AmbientOcclusionCheckBox.ButtonPressed = true;
        SSReflectionsCheckBox.ButtonPressed = true;
        SSAOCheckBox.ButtonPressed = true;
        BloomCheckBox.ButtonPressed = true;
        // Apply all the reset settings
        OnVSyncToggled(true);
        OnDebugLinesToggled(true);
        OnMaxFPSChanged(60);
        OnShadowsToggled(true);
        OnAmbientOcclusionToggled(true);
        OnSSReflectionsToggled(true);
        OnSSAOToggled(true);
        OnBloomToggled(true);

        Logger.Log("Settings reset to defaults");
    }

    public void OnApplyButtonPressed()
    {
        // Apply all current settings
        OnVSyncToggled(VSyncCheckBox.ButtonPressed);
        OnDebugLinesToggled(DebugLinesCheckBox.ButtonPressed);
        OnMaxFPSChanged(MaxFPSSpinBox.Value);
        OnShadowsToggled(ShadowsCheckBox.ButtonPressed);
        OnAmbientOcclusionToggled(AmbientOcclusionCheckBox.ButtonPressed);
        OnSSReflectionsToggled(SSReflectionsCheckBox.ButtonPressed);
        OnSSAOToggled(SSAOCheckBox.ButtonPressed);
        OnBloomToggled(BloomCheckBox.ButtonPressed);

        Logger.Log("Settings applied");
    }

    private async void OnConsoleInputSubmitted(string newText)
    {
        ConsoleInput.Clear();
        var strings = newText.Split(' ');  // Remove .ToLower() cus password shenanigans 
        if (strings.Length == 0) return;
        var player = MultiplayerScene.Instance.GetLocalPlayer();
        try
        {
            switch (strings[0])
            {
                case "clear":
                    for (int i = 0; i < _recentEntries.Length; i++)
                    {
                        _recentEntries[i] = string.Empty;
                    }
                    break;
                case "login":
                    if (strings.Length < 3)
                    {
                        Logger.Warn("Usage: login <username> <password> [2fa_code]");
                        return;
                    }

                    var loginResult = await LoginManager.Instance.LoginAsync(
                        strings[1],
                        strings[2],
                        strings.Length > 3 ? strings[3] : null
                    );

                    if (loginResult.Requires2FA)
                    {
                        Logger.Warn("2FA code required. Please use: login <username> <password> <2fa_code>");
                        return;
                    }

                    if (loginResult.Success)
                    {
                        Logger.Log($"Successfully logged in as {strings[1]}");
                    }
                    else
                    {
                        Logger.Error($"Login failed: {loginResult.Error}");
                    }
                    break;

                case "register":
                    if (strings.Length < 4)
                    {
                        Logger.Warn("Usage: register <username> <email> <password>");
                        return;
                    }

                    var registerResponse = await LoginManager.Instance.RegisterAsync(strings[1], strings[2], strings[3]);
                    if (registerResponse.Success)
                    {
                        Logger.Log($"Successfully registered user {strings[1]}");
                        if (!string.IsNullOrEmpty(registerResponse.Message))
                        {
                            Logger.Log(registerResponse.Message);
                        }
                    }
                    else
                    {
                        Logger.Error($"Registration failed: {registerResponse.Message}");
                    }
                    break;

                case "logout":
                    LoginManager.Instance.Logout();
                    Logger.Log("Logged out successfully");
                    break;
                case "connect":
                    if (strings.Length < 3)
                    {
                        Logger.Warn("Usage: connect <{direct/nat/relay}> <{ip:port}/{identifier}>");
                        return;
                    }
                    var method = strings[1];
                    if (method.StartsWith('d')) //direct
                    {
                        var split = strings[2].Split(":");
                        if (split.Length != 2 || !int.TryParse(split[1], out var port)) return;
                        ClientManager.Instance.JoinServer(split[0], port);
                    }
                    else if (method.StartsWith('n')) //nat
                    {
                        ClientManager.Instance.JoinNatServer(strings[2]);
                    }
                    else if (method.StartsWith('r')) //relay
                    {
                        ClientManager.Instance.JoinNatServerRelay(strings[2]);
                    }
                    break;
                case "respawn":
                    player?.Respawn();
                    break;
                case "exit":
                    GetTree().Quit();
                    break;
                case "settings":
                    TabContainer.CurrentTab = 1; // Switch to settings tab
                    Logger.Log("Switched to settings tab");
                    break;
                case "debug":
                    TabContainer.CurrentTab = 0; // Switch to debug tab
                    Logger.Log("Switched to debug tab");
                    break;
                case "vsync":
                    if (strings.Length > 1 && (strings[1] == "on" || strings[1] == "1" || strings[1] == "true"))
                    {
                        VSyncCheckBox.ButtonPressed = true;
                        OnVSyncToggled(true);
                    }
                    else if (strings.Length > 1 && (strings[1] == "off" || strings[1] == "0" || strings[1] == "false"))
                    {
                        VSyncCheckBox.ButtonPressed = false;
                        OnVSyncToggled(false);
                    }
                    else
                    {
                        Logger.Log($"VSync is currently {(VSyncCheckBox.ButtonPressed ? "enabled" : "disabled")}");
                        Logger.Log("Usage: vsync [on|off]");
                    }
                    break;
                case "fps":
                    if (strings.Length > 1 && int.TryParse(strings[1], out int fps))
                    {
                        MaxFPSSpinBox.Value = fps;
                        OnMaxFPSChanged(fps);
                    }
                    else
                    {
                        Logger.Log($"Max FPS is currently {Engine.MaxFps}");
                        Logger.Log("Usage: fps <number>");
                    }
                    break;
                case "help":
                    Logger.Log("Available commands:");
                    Logger.Log("  clear - Clear the console");
                    Logger.Log("  login <username> <password> [2fa_code] - Log in to the server");
                    Logger.Log("  register <username> <email> <password> - Create a new account");
                    Logger.Log("  logout - Log out from the current account");
                    Logger.Log("  connect {direct/nat/relay} {ip:port/identifier} - Connect to a server");
                    Logger.Log("  respawn - Respawn the player");
                    Logger.Log("  settings - Switch to the settings tab");
                    Logger.Log("  debug - Switch to the debug tab");
                    Logger.Log("  vsync [on|off] - Toggle vsync");
                    Logger.Log("  fps <number> - Set maximum FPS");
                    Logger.Log("  exit - Quit the application");
                    break;
                default:
                    Logger.Warn($"Unknown command: {strings[0]}");
                    return;
            }
            Logger.Debug($"Ran Command: {newText}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to run command: [{newText}] - {ex.Message}");
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

    private string[] _recentEntries = new string[24];
    private void OnLogMessageWritten(string message)
    {
        for (var i = _recentEntries.Length - 1; i > 0; i--)
        {
            _recentEntries[i] = _recentEntries[i - 1];
        }
        _recentEntries[0] = message;
    }

    public override void _Process(double delta)
    {
        base._Process(delta);

        _statsTextStringBuilder.Clear();
        _statsTextStringBuilder.AppendLine("[code]");

        _statsTextStringBuilder.AppendLine("Game");
        _statsTextStringBuilder.AppendLine($"{IntLabel} FPS: {string.Format(IntValue, Engine.GetFramesPerSecond())}");
        _statsTextStringBuilder.AppendLine($"{IntLabel} Physics TPS: {string.Format(IntValue, Engine.PhysicsTicksPerSecond)}");
        _statsTextStringBuilder.AppendLine($"{BoolLabel} VSync: {DoBoolLabel(DisplayServer.WindowGetVsyncMode() != DisplayServer.VSyncMode.Disabled)}");
        _statsTextStringBuilder.AppendLine($"{IntLabel} Max FPS: {string.Format(IntValue, Engine.MaxFps)}");
        _statsTextStringBuilder.AppendLine();

        _statsTextStringBuilder.AppendLine("Account");
        _statsTextStringBuilder.AppendLine($"{BoolLabel} Logged In: {DoBoolLabel(LoginManager.Instance.IsLoggedIn)}");
        if (LoginManager.Instance.IsLoggedIn)
        {
            var profile = LoginManager.Instance.GetUserProfile();
            if (profile != null)
            {
                _statsTextStringBuilder.AppendLine($"{StringLabel} Username: {string.Format(StringValue, profile.Username)}");
                _statsTextStringBuilder.AppendLine($"{StringLabel} User ID: {string.Format(StringValue, profile.Id)}");
                _statsTextStringBuilder.AppendLine($"{BoolLabel} Verified: {DoBoolLabel(profile.IsVerified)}");
                _statsTextStringBuilder.AppendLine($"{BoolLabel} 2FA Enabled: {DoBoolLabel(profile.TwoFactorEnabled)}");
                _statsTextStringBuilder.AppendLine($"{StringLabel} Name Color: {string.Format(StringValue, profile.NameColor)}");

                if (profile.PatreonData != null)
                {
                    _statsTextStringBuilder.AppendLine($"\n  Patreon Status");
                    _statsTextStringBuilder.AppendLine($"{BoolLabel} Active Supporter: {DoBoolLabel(profile.PatreonData.IsActiveSupporter)}");
                    _statsTextStringBuilder.AppendLine($"{IntLabel} Support Months: {string.Format(IntValue, profile.PatreonData.TotalSupportMonths)}");
                    _statsTextStringBuilder.AppendLine($"{IntLabel} Last Tier (¢): {string.Format(IntValue, profile.PatreonData.LastTierCents)}");
                }
            }
            else
            {
                _statsTextStringBuilder.AppendLine($"{StringLabel} Username: {string.Format(StringValue, LoginManager.Instance.GetCurrentUsername())}");
            }
        }
        _statsTextStringBuilder.AppendLine();

        _statsTextStringBuilder.AppendLine("Networking");
        //_statsTextStringBuilder.AppendLine($"{IntLabel} Player Count: {string.Format(IntValue, MultiplayerScene.Instance.PlayerList.Count)}");

        // Add network stats
        var multiplayerPeer = Multiplayer.MultiplayerPeer as LiteNetLibMultiplayerPeer;
        if (multiplayerPeer != null)
        {
            var serverPing = multiplayerPeer.GetServerPing();
            _statsTextStringBuilder.AppendLine($"{IntLabel} Ping: {string.Format(serverPing >= 0 ? IntValue : "[color=indian_red]Unknown[/color]", serverPing)}");
            _statsTextStringBuilder.AppendLine($"{IntLabel} Packets Sent: {string.Format(IntValue, multiplayerPeer.PacketsSent)}");
            _statsTextStringBuilder.AppendLine($"{IntLabel} Packets Received: {string.Format(IntValue, multiplayerPeer.PacketsReceived)}");

            // Show detailed peer pings if in server mode
            if (multiplayerPeer._IsServer() && ClientManager.ShowDebug)
            {
                var peerPings = multiplayerPeer.GetAllPeerPings();
                if (peerPings.Count > 0)
                {
                    _statsTextStringBuilder.AppendLine("\n  Peer Pings");
                    foreach (var peerPing in peerPings)
                    {
                        var playerInfo = MultiplayerScene.Instance.PlayerList.TryGetValue(peerPing.Key, out var info) ? info : null;
                        var playerName = playerInfo?.Name ?? $"Player {peerPing.Key}";
                        _statsTextStringBuilder.AppendLine($"  {IntLabel} {playerName}: {string.Format(IntValue, peerPing.Value)}");
                    }
                }
            }
        }

        _statsTextStringBuilder.AppendLine();

        _statsTextStringBuilder.AppendLine("Player");
        if (playerref?.TryGetTarget(out PlayerCharacterController player) ?? false)
        {
            _statsTextStringBuilder.AppendLine($"{IntLabel} Authority ID: {string.Format(IntValue, player.Authority)}");
            _statsTextStringBuilder.AppendLine($"{IntLabel} Local ID: {string.Format(IntValue, Multiplayer.GetUniqueId())}");
            _statsTextStringBuilder.AppendLine($"{BoolLabel} Is Local Player: {DoBoolLabel(player.Authority == Multiplayer.GetUniqueId())}");
            _statsTextStringBuilder.AppendLine($"{Vector2Label} Movement: {string.Format(Vector2Value, InputManager.Movement.X.ToString("F2"), InputManager.Movement.Y.ToString("F2"))}");
            _statsTextStringBuilder.AppendLine($"{Vector3Label} Velocity: {string.Format(Vector3Value, player.Velocity.X.ToString("F2"), player.Velocity.Y.ToString("F2"), player.Velocity.Z.ToString("F2"))}");
            _statsTextStringBuilder.AppendLine($"{BoolLabel} Crouching: {DoBoolLabel(InputButton.Crouch.Held())}");
            _statsTextStringBuilder.AppendLine($"{BoolLabel} Sprinting: {DoBoolLabel(InputButton.Sprint.Held())}");
            _statsTextStringBuilder.AppendLine($"{BoolLabel} Jumping: {DoBoolLabel(InputButton.Jump.Held())}");
            _statsTextStringBuilder.AppendLine($"{BoolLabel} Grounded: {DoBoolLabel(player.IsOnFloor())}");
        }
        else
            _statsTextStringBuilder.AppendLine("  No player found.");
        _statsTextStringBuilder.AppendLine();

        _statsTextStringBuilder.AppendLine("[/code]");
        StatsText.Text = _statsTextStringBuilder.ToString();

        _consoleTextStringBuilder.Clear();
        _consoleTextStringBuilder.AppendLine("[code]");
        foreach (var t in _recentEntries) _consoleTextStringBuilder.AppendLine(t);
        _consoleTextStringBuilder.AppendLine("[/code]");
        ConsoleText.Text = _consoleTextStringBuilder.ToString();
    }
}
