using Godot;
using System;
using Lumora.CDN;

namespace Lumora.Godot.UI;

/// <summary>
/// Settings page with Profile, Security, and Preferences tabs.
/// </summary>
public partial class Settings : Control
{
    // Tab buttons
    private Button? _tabProfile;
    private Button? _tabSecurity;
    private Button? _tabPreferences;

    // Tab content
    private Control? _profileTab;
    private Control? _securityTab;
    private Control? _preferencesTab;

    // Profile tab elements
    private Label? _avatarLabel;
    private TextEdit? _bioInput;
    private Button? _btnChangeAvatar;
    private Button? _btnSaveProfile;

    // Security tab elements
    private LineEdit? _currentPasswordInput;
    private LineEdit? _newPasswordInput;
    private Button? _btnChangePassword;
    private Label? _twoFactorStatus;
    private Button? _btnEnable2FA;

    // Preferences tab elements
    private HSlider? _masterVolumeSlider;
    private HSlider? _musicVolumeSlider;
    private Label? _masterVolumeValue;
    private Label? _musicVolumeValue;
    private OptionButton? _qualityOption;
    private CheckButton? _fpsLimitToggle;
    private HSlider? _fpsLimitSlider;
    private Label? _fpsLimitValue;
    private CheckButton? _vsyncToggle;
    private CheckButton? _fullscreenToggle;

    // Style resources for tab switching
    private StyleBox? _tabActiveStyle;
    private StyleBox? _tabNormalStyle;

    // Auth
    private LumoraClient? _client;
    private bool _has2FA;

    private string _currentTab = "Profile";

    public override void _Ready()
    {
        // Get tab buttons
        _tabProfile = GetNodeOrNull<Button>("VBox/Header/TabProfile");
        _tabSecurity = GetNodeOrNull<Button>("VBox/Header/TabSecurity");
        _tabPreferences = GetNodeOrNull<Button>("VBox/Header/TabPreferences");

        // Get tab content containers
        _profileTab = GetNodeOrNull<Control>("VBox/ContentPanel/Margin/TabContainer/ProfileTab");
        _securityTab = GetNodeOrNull<Control>("VBox/ContentPanel/Margin/TabContainer/SecurityTab");
        _preferencesTab = GetNodeOrNull<Control>("VBox/ContentPanel/Margin/TabContainer/PreferencesTab");

        // Get profile elements
        _avatarLabel = GetNodeOrNull<Label>("VBox/ContentPanel/Margin/TabContainer/ProfileTab/ProfileContent/AvatarSection/AvatarMargin/AvatarHBox/AvatarPreview/AvatarLabel");
        _bioInput = GetNodeOrNull<TextEdit>("VBox/ContentPanel/Margin/TabContainer/ProfileTab/ProfileContent/BioSection/BioInput");
        _btnChangeAvatar = GetNodeOrNull<Button>("VBox/ContentPanel/Margin/TabContainer/ProfileTab/ProfileContent/AvatarSection/AvatarMargin/AvatarHBox/AvatarInfo/BtnChangeAvatar");
        _btnSaveProfile = GetNodeOrNull<Button>("VBox/ContentPanel/Margin/TabContainer/ProfileTab/ProfileContent/SaveButton");

        // Get security elements
        _currentPasswordInput = GetNodeOrNull<LineEdit>("VBox/ContentPanel/Margin/TabContainer/SecurityTab/SecurityContent/PasswordSection/PasswordMargin/PasswordVBox/CurrentPassword");
        _newPasswordInput = GetNodeOrNull<LineEdit>("VBox/ContentPanel/Margin/TabContainer/SecurityTab/SecurityContent/PasswordSection/PasswordMargin/PasswordVBox/NewPassword");
        _btnChangePassword = GetNodeOrNull<Button>("VBox/ContentPanel/Margin/TabContainer/SecurityTab/SecurityContent/PasswordSection/PasswordMargin/PasswordVBox/BtnChangePassword");
        _twoFactorStatus = GetNodeOrNull<Label>("VBox/ContentPanel/Margin/TabContainer/SecurityTab/SecurityContent/TwoFactorSection/TwoFactorMargin/TwoFactorHBox/TwoFactorInfo/TwoFactorStatus");
        _btnEnable2FA = GetNodeOrNull<Button>("VBox/ContentPanel/Margin/TabContainer/SecurityTab/SecurityContent/TwoFactorSection/TwoFactorMargin/TwoFactorHBox/BtnEnable2FA");

        // Get preferences elements
        _masterVolumeSlider = GetNodeOrNull<HSlider>("VBox/ContentPanel/Margin/TabContainer/PreferencesTab/PreferencesContent/AudioSection/AudioMargin/AudioVBox/MasterVolumeHBox/MasterSlider");
        _musicVolumeSlider = GetNodeOrNull<HSlider>("VBox/ContentPanel/Margin/TabContainer/PreferencesTab/PreferencesContent/AudioSection/AudioMargin/AudioVBox/MusicVolumeHBox/MusicSlider");
        _masterVolumeValue = GetNodeOrNull<Label>("VBox/ContentPanel/Margin/TabContainer/PreferencesTab/PreferencesContent/AudioSection/AudioMargin/AudioVBox/MasterVolumeHBox/MasterValue");
        _musicVolumeValue = GetNodeOrNull<Label>("VBox/ContentPanel/Margin/TabContainer/PreferencesTab/PreferencesContent/AudioSection/AudioMargin/AudioVBox/MusicVolumeHBox/MusicValue");
        _qualityOption = GetNodeOrNull<OptionButton>("VBox/ContentPanel/Margin/TabContainer/PreferencesTab/PreferencesContent/GraphicsSection/GraphicsMargin/GraphicsVBox/QualityHBox/QualityOption");
        _fpsLimitToggle = GetNodeOrNull<CheckButton>("VBox/ContentPanel/Margin/TabContainer/PreferencesTab/PreferencesContent/GraphicsSection/GraphicsMargin/GraphicsVBox/FPSLimitHBox/FPSLimitToggle");
        _fpsLimitSlider = GetNodeOrNull<HSlider>("VBox/ContentPanel/Margin/TabContainer/PreferencesTab/PreferencesContent/GraphicsSection/GraphicsMargin/GraphicsVBox/FPSLimitHBox/FPSLimitSlider");
        _fpsLimitValue = GetNodeOrNull<Label>("VBox/ContentPanel/Margin/TabContainer/PreferencesTab/PreferencesContent/GraphicsSection/GraphicsMargin/GraphicsVBox/FPSLimitHBox/FPSLimitValue");
        _vsyncToggle = GetNodeOrNull<CheckButton>("VBox/ContentPanel/Margin/TabContainer/PreferencesTab/PreferencesContent/GraphicsSection/GraphicsMargin/GraphicsVBox/VSyncHBox/VSyncToggle");
        _fullscreenToggle = GetNodeOrNull<CheckButton>("VBox/ContentPanel/Margin/TabContainer/PreferencesTab/PreferencesContent/GraphicsSection/GraphicsMargin/GraphicsVBox/FullscreenHBox/FullscreenToggle");

        // Get styles for tab switching
        _tabActiveStyle = _tabProfile?.GetThemeStylebox("normal");
        _tabNormalStyle = _tabSecurity?.GetThemeStylebox("normal");

        ConnectSignals();
        UpdateTabVisuals();

        GD.Print("Settings: Initialized");
    }

    private void ConnectSignals()
    {
        // Tab buttons
        _tabProfile?.Connect("pressed", Callable.From(() => SwitchTab("Profile")));
        _tabSecurity?.Connect("pressed", Callable.From(() => SwitchTab("Security")));
        _tabPreferences?.Connect("pressed", Callable.From(() => SwitchTab("Preferences")));

        // Profile actions
        _btnChangeAvatar?.Connect("pressed", Callable.From(OnChangeAvatarPressed));
        _btnSaveProfile?.Connect("pressed", Callable.From(OnSaveProfilePressed));

        // Security actions
        _btnChangePassword?.Connect("pressed", Callable.From(OnChangePasswordPressed));
        _btnEnable2FA?.Connect("pressed", Callable.From(OnEnable2FAPressed));

        // Preferences sliders
        _masterVolumeSlider?.Connect("value_changed", Callable.From<double>(OnMasterVolumeChanged));
        _musicVolumeSlider?.Connect("value_changed", Callable.From<double>(OnMusicVolumeChanged));
        _qualityOption?.Connect("item_selected", Callable.From<long>(OnQualitySelected));
        _fpsLimitToggle?.Connect("toggled", Callable.From<bool>(OnFPSLimitToggled));
        _fpsLimitSlider?.Connect("value_changed", Callable.From<double>(OnFPSLimitChanged));
        _vsyncToggle?.Connect("toggled", Callable.From<bool>(OnVSyncToggled));
        _fullscreenToggle?.Connect("toggled", Callable.From<bool>(OnFullscreenToggled));
    }

    private void SwitchTab(string tab)
    {
        if (_currentTab == tab) return;
        _currentTab = tab;

        // Update visibility
        if (_profileTab != null) _profileTab.Visible = tab == "Profile";
        if (_securityTab != null) _securityTab.Visible = tab == "Security";
        if (_preferencesTab != null) _preferencesTab.Visible = tab == "Preferences";

        UpdateTabVisuals();
        GD.Print($"Settings: Switched to {tab} tab");
    }

    private void UpdateTabVisuals()
    {
        UpdateTabButton(_tabProfile, _currentTab == "Profile");
        UpdateTabButton(_tabSecurity, _currentTab == "Security");
        UpdateTabButton(_tabPreferences, _currentTab == "Preferences");
    }

    private void UpdateTabButton(Button? button, bool isActive)
    {
        if (button == null) return;

        if (isActive && _tabActiveStyle != null)
        {
            button.AddThemeStyleboxOverride("normal", _tabActiveStyle);
            button.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.95f, 1f));
        }
        else if (_tabNormalStyle != null)
        {
            button.AddThemeStyleboxOverride("normal", _tabNormalStyle);
            button.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.75f, 1f));
        }
    }

    private void OnChangeAvatarPressed()
    {
        GD.Print("Settings: Change avatar pressed");
        // TODO: Open avatar picker
    }

    private void OnSaveProfilePressed()
    {
        var bio = _bioInput?.Text ?? "";

        GD.Print($"Settings: Save profile - Bio length: {bio.Length}");
        // TODO: Save to LumoraClient
    }

    private async void OnChangePasswordPressed()
    {
        var currentPass = _currentPasswordInput?.Text ?? "";
        var newPass = _newPasswordInput?.Text ?? "";

        if (string.IsNullOrEmpty(currentPass) || string.IsNullOrEmpty(newPass))
        {
            GD.Print("Settings: Password fields cannot be empty");
            return;
        }

        if (_client == null)
        {
            GD.Print("Settings: Not connected to server");
            return;
        }

        if (_btnChangePassword != null)
            _btnChangePassword.Disabled = true;

        GD.Print("Settings: Change password requested");

        var result = await _client.ChangePassword(currentPass, newPass);

        if (result.Success)
        {
            GD.Print("Settings: Password changed successfully");
        }
        else
        {
            GD.PrintErr($"Settings: Password change failed - {result.Message}");
        }

        // Clear fields after attempt
        if (_currentPasswordInput != null) _currentPasswordInput.Text = "";
        if (_newPasswordInput != null) _newPasswordInput.Text = "";

        if (_btnChangePassword != null)
            _btnChangePassword.Disabled = false;
    }

    private async void OnEnable2FAPressed()
    {
        if (_client == null)
        {
            GD.Print("Settings: Not connected to server");
            return;
        }

        if (_btnEnable2FA != null)
            _btnEnable2FA.Disabled = true;

        if (_has2FA)
        {
            // Disable 2FA - would need a code input dialog in real implementation
            GD.Print("Settings: Disable 2FA requested");
            // For now just log - real implementation would show code input dialog
            // var result = await _client.Disable2FA(code);
        }
        else
        {
            // Enable 2FA
            GD.Print("Settings: Enable 2FA requested");
            var result = await _client.Enable2FA();

            if (result.Success && result.Data != null)
            {
                GD.Print("Settings: 2FA setup initiated - QR code ready");
                // TODO: Show QR code dialog with result.Data.QrCode and result.Data.RecoveryCodes
                // After user verifies with code, call _client.Verify2FA(code)
            }
            else
            {
                GD.PrintErr($"Settings: 2FA setup failed - {result.Message}");
            }
        }

        if (_btnEnable2FA != null)
            _btnEnable2FA.Disabled = false;
    }

    private void OnMasterVolumeChanged(double value)
    {
        if (_masterVolumeValue != null)
            _masterVolumeValue.Text = $"{(int)value}%";

        // TODO: Apply volume setting
        AudioServer.SetBusVolumeDb(AudioServer.GetBusIndex("Master"), LinearToDb((float)value / 100f));
    }

    private void OnMusicVolumeChanged(double value)
    {
        if (_musicVolumeValue != null)
            _musicVolumeValue.Text = $"{(int)value}%";

        // TODO: Apply music volume setting
        var musicBus = AudioServer.GetBusIndex("Music");
        if (musicBus >= 0)
            AudioServer.SetBusVolumeDb(musicBus, LinearToDb((float)value / 100f));
    }

    private void OnQualitySelected(long index)
    {
        string quality = index switch
        {
            0 => "Low",
            1 => "Medium",
            2 => "High",
            _ => "Medium"
        };
        GD.Print($"Settings: Quality set to {quality}");
        // TODO: Apply graphics quality setting
    }

    private void OnFPSLimitToggled(bool enabled)
    {
        if (_fpsLimitSlider != null)
            _fpsLimitSlider.Editable = enabled;

        if (enabled)
        {
            int fps = (int)(_fpsLimitSlider?.Value ?? 60);
            Engine.MaxFps = fps;
            if (_fpsLimitValue != null)
                _fpsLimitValue.Text = $"{fps} FPS";
            GD.Print($"Settings: FPS limit enabled at {fps}");
        }
        else
        {
            Engine.MaxFps = 0; // Unlimited
            if (_fpsLimitValue != null)
                _fpsLimitValue.Text = "Off";
            GD.Print("Settings: FPS limit disabled (unlimited)");
        }
    }

    private void OnFPSLimitChanged(double value)
    {
        // Only apply if toggle is on
        if (_fpsLimitToggle?.ButtonPressed != true)
            return;

        int fps = (int)value;
        if (_fpsLimitValue != null)
            _fpsLimitValue.Text = $"{fps} FPS";

        Engine.MaxFps = fps;
        GD.Print($"Settings: FPS limit set to {fps}");
    }

    private void OnVSyncToggled(bool enabled)
    {
        DisplayServer.WindowSetVsyncMode(
            enabled ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled
        );
        GD.Print($"Settings: VSync {(enabled ? "enabled" : "disabled")}");
    }

    private void OnFullscreenToggled(bool enabled)
    {
        DisplayServer.WindowSetMode(
            enabled ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed
        );
        GD.Print($"Settings: Fullscreen {(enabled ? "enabled" : "disabled")}");
    }

    private static float LinearToDb(float linear)
    {
        return linear > 0 ? 20f * Mathf.Log(linear) / Mathf.Log(10) : -80f;
    }

    /// <summary>
    /// Set the LumoraClient for API calls.
    /// </summary>
    public void SetClient(LumoraClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Set user data to populate the settings fields.
    /// </summary>
    public void SetUserData(string username, string bio, bool has2FA)
    {
        _has2FA = has2FA;

        if (_bioInput != null)
            _bioInput.Text = bio;

        if (_avatarLabel != null && !string.IsNullOrEmpty(username))
            _avatarLabel.Text = username[0].ToString().ToUpper();

        if (_twoFactorStatus != null)
        {
            _twoFactorStatus.Text = has2FA ? "Enabled" : "Not enabled";
            _twoFactorStatus.AddThemeColorOverride("font_color",
                has2FA ? new Color(0.3f, 0.8f, 0.5f, 1f) : new Color(0.9f, 0.5f, 0.5f, 1f));
        }

        if (_btnEnable2FA != null)
            _btnEnable2FA.Text = has2FA ? "Disable" : "Enable";
    }
}
