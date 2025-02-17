using Aquamarine.Source.Helpers;
using Aquamarine.Source.Input;
using Aquamarine.Source.Logging;
using Godot;
using System;
using System.Text;

namespace Aquamarine.Source.Management.HUD;

public partial class DebugOverlay : Control
{
    [Export] public RichTextLabel StatsText;
    [Export] public RichTextLabel ConsoleText;
    [Export] public LineEdit ConsoleInput;

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

    private static string DoBoolLabel(bool value) => value ? BoolTrueValue : BoolFalseValue;

    public override void _Ready()
    {
        base._Ready();
        Logger.OnPrettyLogMessageWritten += OnLogMessageWritten;
        ConsoleInput.TextSubmitted += OnConsoleInputSubmitted;
        Visible = false;
        Logger.Log("DebugOverlay initialized.");
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
                case "voice":
                    if (strings.Length < 2)
                    {
                        Logger.Warn("Usage: voice <on/off>");
                        return;
                    }
                    if (strings[1].ToLower() == "on")
                    {
                        ClientManager.Instance.ToggleVoiceChat(true);
                        Logger.Log("Voice chat enabled");
                    }
                    else if (strings[1].ToLower() == "off")
                    {
                        ClientManager.Instance.ToggleVoiceChat(false);
                        Logger.Log("Voice chat disabled");
                    }
                    break;
                default:
                    Logger.Warn($"Unknown command: {strings[0]}");
                    return;
            }
            Logger.Debug($"Ran Command: {newText}");
        }
        catch
        {
            Logger.Error($"Failed to run command: [{newText}] is your syntax malformed?");
        }
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
        _statsTextStringBuilder.AppendLine("Audio");
        _statsTextStringBuilder.AppendLine($"{BoolLabel} Voice Enabled: {DoBoolLabel(ClientManager.Instance.IsVoiceChatEnabled)}");
        if (ClientManager.Instance.IsVoiceChatEnabled)
        {
            var voiceManager = ClientManager.Instance.GetVoiceManager();
            if (voiceManager != null)
            {
                _statsTextStringBuilder.AppendLine($"{IntLabel} Active Speakers: {string.Format(IntValue, voiceManager.GetActiveSpeakerCount())}");
                _statsTextStringBuilder.AppendLine($"{FloatLabel} Input Level: {string.Format(FloatValue, voiceManager.GetInputLevel().ToString("F2"))}");
                _statsTextStringBuilder.AppendLine($"{FloatLabel} Voice Range: {string.Format(FloatValue, voiceManager.GetVoiceRange().ToString("F2"))}");
            }
        }
        _statsTextStringBuilder.AppendLine();

        _statsTextStringBuilder.AppendLine();

        _statsTextStringBuilder.AppendLine("Networking");
        _statsTextStringBuilder.AppendLine($"{IntLabel} Player Count: {string.Format(IntValue, MultiplayerScene.Instance.PlayerList.Count)}");
        _statsTextStringBuilder.AppendLine();

        var player = MultiplayerScene.Instance.GetLocalPlayer();

        _statsTextStringBuilder.AppendLine("Player");
        if (player is not null)
        {
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
