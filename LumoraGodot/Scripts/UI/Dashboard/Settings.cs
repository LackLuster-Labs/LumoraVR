// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using System;
using System.Collections.Generic;
using Lumora.CDN;
using Lumora.Core.Components.Avatar;
using Lumora.Source.Godot.UI;

namespace Lumora.Godot.UI;

/// <summary>
/// Settings page with Profile, Security, and Preferences tabs.
/// </summary>
public partial class Settings : Control
{
    // Tab buttons
    private Button? _tabProfile;
    private Button? _tabSecurity;
    private Button? _tabAudio;
    private Button? _tabVideo;
    private Button? _tabInterface;

    // Tab content
    private Control? _profileTab;
    private Control? _securityTab;
    private Control? _audioTab;
    private Control? _videoTab;
    private Control? _interfaceTab;

    // Interface tab dynamic controls
    private HSlider? _reticleSizeSlider;
    private Label? _reticleSizeValue;
    private HSlider? _mouseSensitivitySlider;
    private Label? _mouseSensitivityValue;
    private HSlider? _mouseSmoothingSlider;
    private Label? _mouseSmoothingValue;

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
    private OptionButton? _outputDeviceOption;
    private OptionButton? _inputDeviceOption;
    private OptionButton? _qualityOption;
    private OptionButton? _renderScaleOption;
    private OptionButton? _antiAliasingOption;
    private OptionButton? _shadowQualityOption;
    private CheckButton? _aoToggle;
    private CheckButton? _ssrToggle;
    private CheckButton? _fpsLimitToggle;
    private HSlider? _fpsLimitSlider;
    private Label? _fpsLimitValue;
    private CheckButton? _vsyncToggle;
    private CheckButton? _fullscreenToggle;
    private ProgressBar? _outputActivityBar;
    private ProgressBar? _inputActivityBar;
    private int _outputMeterBus = -1;
    private int _inputMeterBus = -1;
    private float _outputMeterLevel;
    private float _inputMeterLevel;
    private AudioStreamPlayer? _inputMonitorPlayer;
    private AudioEffectCapture? _inputCaptureEffect;
    private readonly Dictionary<ulong, bool> _shadowRestoreStates = new();
    private bool _sceneShadowsForcedOff;

    private const string InputMonitorBusName = "LumoraInputMonitor";
    private const float MeterAttackSpeed = 4.0f;
    private const float MeterReleaseSpeed = 1.8f;

    // Style resources for tab switching
    private StyleBox? _tabActiveStyle;
    private StyleBox? _tabNormalStyle;

    // Auth
    private LumoraClient? _client;
    private bool _has2FA;

    private string _currentTab = "Profile";

    private const string TAB_PROFILE = "Profile";
    private const string TAB_SECURITY = "Security";
    private const string TAB_AUDIO = "Audio";
    private const string TAB_VIDEO = "Video";
    private const string TAB_INTERFACE = "Interface";

    public override void _Ready()
    {
        // Get tab buttons
        _tabProfile = GetNodeOrNull<Button>("VBox/Header/TabProfile");
        _tabSecurity = GetNodeOrNull<Button>("VBox/Header/TabSecurity");
        _tabAudio = GetNodeOrNull<Button>("VBox/Header/TabAudio");
        _tabVideo = GetNodeOrNull<Button>("VBox/Header/TabVideo");

        // Get tab content containers
        _profileTab = GetNodeOrNull<Control>("VBox/ContentPanel/Margin/TabContainer/ProfileTab");
        _securityTab = GetNodeOrNull<Control>("VBox/ContentPanel/Margin/TabContainer/SecurityTab");
        _audioTab = GetNodeOrNull<Control>("VBox/ContentPanel/Margin/TabContainer/AudioTab");
        _videoTab = GetNodeOrNull<Control>("VBox/ContentPanel/Margin/TabContainer/VideoTab");

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

        // Get audio elements
        _masterVolumeSlider = GetNodeOrNull<HSlider>("VBox/ContentPanel/Margin/TabContainer/AudioTab/AudioContent/AudioSection/AudioMargin/AudioVBox/MasterVolumeHBox/MasterSlider");
        _musicVolumeSlider = GetNodeOrNull<HSlider>("VBox/ContentPanel/Margin/TabContainer/AudioTab/AudioContent/AudioSection/AudioMargin/AudioVBox/MusicVolumeHBox/MusicSlider");
        _masterVolumeValue = GetNodeOrNull<Label>("VBox/ContentPanel/Margin/TabContainer/AudioTab/AudioContent/AudioSection/AudioMargin/AudioVBox/MasterVolumeHBox/MasterValue");
        _musicVolumeValue = GetNodeOrNull<Label>("VBox/ContentPanel/Margin/TabContainer/AudioTab/AudioContent/AudioSection/AudioMargin/AudioVBox/MusicVolumeHBox/MusicValue");

        // Get video elements
        const string gfx = "VBox/ContentPanel/Margin/TabContainer/VideoTab/VideoContent/GraphicsSection/GraphicsMargin/GraphicsVBox";
        _qualityOption = GetNodeOrNull<OptionButton>($"{gfx}/QualityHBox/QualityOption");
        _renderScaleOption = GetNodeOrNull<OptionButton>($"{gfx}/RenderScaleHBox/RenderScaleOption");
        _antiAliasingOption = GetNodeOrNull<OptionButton>($"{gfx}/AntiAliasingHBox/AntiAliasingOption");
        _shadowQualityOption = GetNodeOrNull<OptionButton>($"{gfx}/ShadowQualityHBox/ShadowQualityOption");
        _aoToggle = GetNodeOrNull<CheckButton>($"{gfx}/AOHBox/AOToggle");
        _ssrToggle = GetNodeOrNull<CheckButton>($"{gfx}/SSRHBox/SSRToggle");
        _fpsLimitToggle = GetNodeOrNull<CheckButton>($"{gfx}/FPSLimitHBox/FPSLimitToggle");
        _fpsLimitSlider = GetNodeOrNull<HSlider>($"{gfx}/FPSLimitHBox/FPSLimitSlider");
        _fpsLimitValue = GetNodeOrNull<Label>($"{gfx}/FPSLimitHBox/FPSLimitValue");
        _vsyncToggle = GetNodeOrNull<CheckButton>($"{gfx}/VSyncHBox/VSyncToggle");
        _fullscreenToggle = GetNodeOrNull<CheckButton>($"{gfx}/FullscreenHBox/FullscreenToggle");

        BuildAudioDeviceRows();
        BuildInterfaceTab();
        ApplyToggleTheme(_aoToggle);
        ApplyToggleTheme(_ssrToggle);
        ApplyToggleTheme(_fpsLimitToggle);
        ApplyToggleTheme(_vsyncToggle);
        ApplyToggleTheme(_fullscreenToggle);

        // Get styles for tab switching
        _tabActiveStyle = _tabProfile?.GetThemeStylebox("normal");
        _tabNormalStyle = _tabSecurity?.GetThemeStylebox("normal");

        ConnectSignals();
        UpdateTabVisuals();
        SetProcess(true);
        ResolveMeterBuses();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        UpdateMeterBar(_outputActivityBar, ref _outputMeterLevel, _outputMeterBus, dt);
        UpdateInputMeterBar(dt);
    }

    private void ConnectSignals()
    {
        // Tab buttons
        _tabProfile?.Connect("pressed", Callable.From(() => SwitchTab(TAB_PROFILE)));
        _tabSecurity?.Connect("pressed", Callable.From(() => SwitchTab(TAB_SECURITY)));
        _tabAudio?.Connect("pressed", Callable.From(() => SwitchTab(TAB_AUDIO)));
        _tabVideo?.Connect("pressed", Callable.From(() => SwitchTab(TAB_VIDEO)));
        _tabInterface?.Connect("pressed", Callable.From(() => SwitchTab(TAB_INTERFACE)));

        // Profile actions
        _btnChangeAvatar?.Connect("pressed", Callable.From(OnChangeAvatarPressed));
        _btnSaveProfile?.Connect("pressed", Callable.From(OnSaveProfilePressed));

        // Security actions
        _btnChangePassword?.Connect("pressed", Callable.From(OnChangePasswordPressed));
        _btnEnable2FA?.Connect("pressed", Callable.From(OnEnable2FAPressed));

        // Preferences sliders
        _masterVolumeSlider?.Connect("value_changed", Callable.From<double>(OnMasterVolumeChanged));
        _musicVolumeSlider?.Connect("value_changed", Callable.From<double>(OnMusicVolumeChanged));
        _outputDeviceOption?.Connect("item_selected", Callable.From<long>(OnOutputDeviceSelected));
        _inputDeviceOption?.Connect("item_selected", Callable.From<long>(OnInputDeviceSelected));
        _qualityOption?.Connect("item_selected", Callable.From<long>(OnQualitySelected));
        _renderScaleOption?.Connect("item_selected", Callable.From<long>(OnRenderScaleSelected));
        _antiAliasingOption?.Connect("item_selected", Callable.From<long>(OnAntiAliasingSelected));
        _shadowQualityOption?.Connect("item_selected", Callable.From<long>(OnShadowQualitySelected));
        _aoToggle?.Connect("toggled", Callable.From<bool>(OnAOToggled));
        _ssrToggle?.Connect("toggled", Callable.From<bool>(OnSSRToggled));
        _fpsLimitToggle?.Connect("toggled", Callable.From<bool>(OnFPSLimitToggled));
        _fpsLimitSlider?.Connect("value_changed", Callable.From<double>(OnFPSLimitChanged));
        _vsyncToggle?.Connect("toggled", Callable.From<bool>(OnVSyncToggled));
        _fullscreenToggle?.Connect("toggled", Callable.From<bool>(OnFullscreenToggled));
    }

    private void SwitchTab(string tab)
    {
        if (_currentTab == tab) return;
        _currentTab = tab;

        if (_profileTab != null) _profileTab.Visible = tab == TAB_PROFILE;
        if (_securityTab != null) _securityTab.Visible = tab == TAB_SECURITY;
        if (_audioTab != null) _audioTab.Visible = tab == TAB_AUDIO;
        if (_videoTab != null) _videoTab.Visible = tab == TAB_VIDEO;
        if (_interfaceTab != null) _interfaceTab.Visible = tab == TAB_INTERFACE;

        UpdateTabVisuals();
    }

    private void UpdateTabVisuals()
    {
        UpdateTabButton(_tabProfile, _currentTab == TAB_PROFILE);
        UpdateTabButton(_tabSecurity, _currentTab == TAB_SECURITY);
        UpdateTabButton(_tabAudio, _currentTab == TAB_AUDIO);
        UpdateTabButton(_tabVideo, _currentTab == TAB_VIDEO);
        UpdateTabButton(_tabInterface, _currentTab == TAB_INTERFACE);
    }

    /// <summary>
    /// Builds the Interface tab at runtime so we can expose the new
    /// reticle / mouse settings without editing the .tscn structure.
    /// The tab is added as a sibling of the existing tabs and a header
    /// button is added next to TabVideo.
    /// </summary>
    private void BuildInterfaceTab()
    {
        var header = GetNodeOrNull<HBoxContainer>("VBox/Header");
        var tabContainer = GetNodeOrNull<Control>("VBox/ContentPanel/Margin/TabContainer");
        if (header == null || tabContainer == null || _tabVideo == null)
            return;

        // ----- header button -----
        _tabInterface = new Button();
        _tabInterface.Name = "TabInterface";
        _tabInterface.Text = "Interface";
        _tabInterface.CustomMinimumSize = _tabVideo.CustomMinimumSize;
        _tabInterface.SizeFlagsHorizontal = _tabVideo.SizeFlagsHorizontal;
        var videoFontSize = _tabVideo.GetThemeFontSize("font_size");
        if (videoFontSize > 0) _tabInterface.AddThemeFontSizeOverride("font_size", videoFontSize);
        header.AddChild(_tabInterface);

        // ----- tab content -----
        var scroll = new ScrollContainer();
        scroll.Name = "InterfaceTab";
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        scroll.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        scroll.Visible = false;
        tabContainer.AddChild(scroll);
        _interfaceTab = scroll;

        var content = new VBoxContainer();
        content.Name = "InterfaceContent";
        content.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        content.AddThemeConstantOverride("separation", 16);
        scroll.AddChild(content);

        // Reticle section
        var reticleSection = CreateInterfaceSection(content, "Reticle");

        _reticleSizeSlider = new HSlider { MinValue = 4, MaxValue = 32, Step = 1, Value = InterfaceSettings.ReticleSize };
        _reticleSizeValue = new Label { Text = $"{InterfaceSettings.ReticleSize:F0} px" };
        AddInterfaceRow(reticleSection, "Reticle Size", _reticleSizeSlider, _reticleSizeValue);
        _reticleSizeSlider.Connect("value_changed", Callable.From<double>(OnReticleSizeChanged));

        // Mouse section
        var mouseSection = CreateInterfaceSection(content, "Mouse");

        _mouseSensitivitySlider = new HSlider { MinValue = 0.1, MaxValue = 4.0, Step = 0.05, Value = InterfaceSettings.MouseSensitivity };
        _mouseSensitivityValue = new Label { Text = $"{InterfaceSettings.MouseSensitivity:F2}x" };
        AddInterfaceRow(mouseSection, "Sensitivity", _mouseSensitivitySlider, _mouseSensitivityValue);
        _mouseSensitivitySlider.Connect("value_changed", Callable.From<double>(OnMouseSensitivityChanged));

        _mouseSmoothingSlider = new HSlider { MinValue = 0.0, MaxValue = 0.9, Step = 0.05, Value = InterfaceSettings.MouseSmoothing };
        _mouseSmoothingValue = new Label { Text = InterfaceSettings.MouseSmoothing <= 0.001f ? "Off" : $"{InterfaceSettings.MouseSmoothing:F2}" };
        AddInterfaceRow(mouseSection, "Smoothing", _mouseSmoothingSlider, _mouseSmoothingValue);
        _mouseSmoothingSlider.Connect("value_changed", Callable.From<double>(OnMouseSmoothingChanged));
    }

    private VBoxContainer CreateInterfaceSection(Control parent, string title)
    {
        var section = new VBoxContainer();
        section.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        section.AddThemeConstantOverride("separation", 8);
        parent.AddChild(section);

        var titleLabel = new Label();
        titleLabel.Text = title;
        titleLabel.AddThemeFontSizeOverride("font_size", 14);
        titleLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.65f, 0.95f));
        section.AddChild(titleLabel);

        return section;
    }

    private void AddInterfaceRow(Control section, string label, Control control, Label? valueLabel)
    {
        var row = new HBoxContainer();
        row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddThemeConstantOverride("separation", 12);
        section.AddChild(row);

        var l = new Label { Text = label, CustomMinimumSize = new Vector2(140, 0) };
        l.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(l);

        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        control.CustomMinimumSize = new Vector2(0, 28);
        row.AddChild(control);

        if (valueLabel != null)
        {
            valueLabel.CustomMinimumSize = new Vector2(80, 0);
            valueLabel.HorizontalAlignment = HorizontalAlignment.Right;
            valueLabel.VerticalAlignment = VerticalAlignment.Center;
            row.AddChild(valueLabel);
        }
    }

    private void OnReticleSizeChanged(double value)
    {
        InterfaceSettings.ReticleSize = (float)value;
        if (_reticleSizeValue != null)
            _reticleSizeValue.Text = $"{value:F0} px";
    }

    private void OnMouseSensitivityChanged(double value)
    {
        InterfaceSettings.MouseSensitivity = (float)value;
        if (_mouseSensitivityValue != null)
            _mouseSensitivityValue.Text = $"{value:F2}x";
    }

    private void OnMouseSmoothingChanged(double value)
    {
        InterfaceSettings.MouseSmoothing = (float)value;
        if (_mouseSmoothingValue != null)
            _mouseSmoothingValue.Text = value <= 0.001 ? "Off" : $"{value:F2}";
    }

    /// <summary>
    /// Reviewer flagged the default Godot CheckButton as "double-indicator": the
    /// background tints AND the knob slides. We apply a flat transparent background
    /// so only the knob conveys state, matching modern OS toggles.
    /// </summary>
    private static void ApplyToggleTheme(CheckButton? toggle)
    {
        if (toggle == null) return;

        var transparent = new StyleBoxEmpty();
        toggle.AddThemeStyleboxOverride("normal", transparent);
        toggle.AddThemeStyleboxOverride("hover", transparent);
        toggle.AddThemeStyleboxOverride("pressed", transparent);
        toggle.AddThemeStyleboxOverride("focus", transparent);
        toggle.AddThemeStyleboxOverride("disabled", transparent);
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
        var manager = ResolveLocalAvatarManager();
        if (manager == null)
        {
            GD.PrintErr("Settings: No local avatar manager is available");
            return;
        }

        if (manager.AvailableAvatars.Count == 0)
        {
            manager.EquipDefaultAvatar();
            GD.Print("Settings: Switched to default avatar");
            return;
        }

        manager.CycleNextAvatar();

        var avatarName = manager.CurrentAvatar.Target?.SlotName?.Value;
        if (string.IsNullOrWhiteSpace(avatarName))
            avatarName = "Avatar";

        GD.Print($"Settings: Switched avatar to {avatarName}");
    }

    private void OnSaveProfilePressed()
    {
        // TODO: Save to LumoraClient
    }

    private async void OnChangePasswordPressed()
    {
        var currentPass = _currentPasswordInput?.Text ?? "";
        var newPass = _newPasswordInput?.Text ?? "";

        if (string.IsNullOrEmpty(currentPass) || string.IsNullOrEmpty(newPass))
        {
            return;
        }

        if (_client == null)
        {
            return;
        }

        if (_btnChangePassword != null)
            _btnChangePassword.Disabled = true;

        var result = await _client.ChangePassword(currentPass, newPass);

        if (result.Success)
        {
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
            return;
        }

        if (_btnEnable2FA != null)
            _btnEnable2FA.Disabled = true;

        if (_has2FA)
        {
            // Disable 2FA - would need a code input dialog in real implementation
            // For now just log - real implementation would show code input dialog
            // var result = await _client.Disable2FA(code);
        }
        else
        {
            // Enable 2FA
            var result = await _client.Enable2FA();

            if (result.Success && result.Data != null)
            {
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

        AudioServer.SetBusVolumeDb(AudioServer.GetBusIndex("Master"), LinearToDb((float)value / 100f));
    }

    private void OnMusicVolumeChanged(double value)
    {
        if (_musicVolumeValue != null)
            _musicVolumeValue.Text = $"{(int)value}%";

        var musicBus = AudioServer.GetBusIndex("Music");
        if (musicBus >= 0)
            AudioServer.SetBusVolumeDb(musicBus, LinearToDb((float)value / 100f));
    }

    private void OnQualitySelected(long index)
    {
        // Preset applies render scale, AA, and shadow quality together, then syncs dropdowns
        switch (index)
        {
            case 0: // Low
                ApplyRenderScale(0.5f);
                ApplyAntiAliasing(0);
                ApplyShadowQuality(0);
                ApplyAO(false);
                ApplySSR(false);
                SyncVideoControls(1, 0, 0, false, false);
                break;
            case 1: // Medium
                ApplyRenderScale(0.75f);
                ApplyAntiAliasing(1); // FXAA
                ApplyShadowQuality(1);
                ApplyAO(false);
                ApplySSR(false);
                SyncVideoControls(2, 1, 1, false, false);
                break;
            case 2: // High
                ApplyRenderScale(1.0f);
                ApplyAntiAliasing(2); // MSAA 2x
                ApplyShadowQuality(2);
                ApplyAO(true);
                ApplySSR(false);
                SyncVideoControls(2, 2, 2, true, false);
                break;
            case 3: // Ultra
                ApplyRenderScale(1.0f);
                ApplyAntiAliasing(3); // MSAA 4x
                ApplyShadowQuality(3);
                ApplyAO(true);
                ApplySSR(true);
                SyncVideoControls(2, 3, 3, true, true);
                break;
        }
    }

    private void SyncVideoControls(int renderScale, int aa, int shadows, bool ao, bool ssr)
    {
        if (_renderScaleOption != null) _renderScaleOption.Selected = renderScale;
        if (_antiAliasingOption != null) _antiAliasingOption.Selected = aa;
        if (_shadowQualityOption != null) _shadowQualityOption.Selected = shadows;
        if (_aoToggle != null) _aoToggle.ButtonPressed = ao;
        if (_ssrToggle != null) _ssrToggle.ButtonPressed = ssr;
    }

    private void OnRenderScaleSelected(long index)
    {
        float[] scales = { 0.5f, 0.67f, 1.0f, 1.25f, 1.5f };
        if (index >= 0 && index < scales.Length)
            ApplyRenderScale(scales[index]);
    }

    private void OnAntiAliasingSelected(long index) => ApplyAntiAliasing((int)index);

    private void OnShadowQualitySelected(long index) => ApplyShadowQuality((int)index);

    private void OnAOToggled(bool enabled) => ApplyAO(enabled);

    private void OnSSRToggled(bool enabled) => ApplySSR(enabled);

    private void ApplyRenderScale(float scale)
    {
        var viewport = GetViewport();
        if (viewport == null) return;
        viewport.Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear;
        viewport.Scaling3DScale = scale;
    }

    private void ApplyAntiAliasing(int index)
    {
        var viewport = GetViewport();
        if (viewport == null) return;
        switch (index)
        {
            case 0: // Off
                viewport.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled;
                viewport.Msaa3D = Viewport.Msaa.Disabled;
                viewport.UseTaa = false;
                break;
            case 1: // FXAA
                viewport.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Fxaa;
                viewport.Msaa3D = Viewport.Msaa.Disabled;
                viewport.UseTaa = false;
                break;
            case 2: // MSAA 2x
                viewport.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled;
                viewport.Msaa3D = Viewport.Msaa.Msaa2X;
                viewport.UseTaa = false;
                break;
            case 3: // MSAA 4x
                viewport.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled;
                viewport.Msaa3D = Viewport.Msaa.Msaa4X;
                viewport.UseTaa = false;
                break;
            case 4: // TAA
                viewport.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled;
                viewport.Msaa3D = Viewport.Msaa.Disabled;
                viewport.UseTaa = true;
                break;
        }
    }

    private void ApplyShadowQuality(int index)
    {
        int atlasSize = index switch
        {
            0 => 1024,  // Keep a valid atlas size; "Off" is handled by disabling light shadow casting.
            1 => 1024,  // Low
            2 => 2048,  // Medium
            3 => 4096,  // High
            _ => 2048
        };

        // This API changes directional shadow atlas resolution and depth format, not shadow enablement.
        RenderingServer.DirectionalShadowAtlasSetSize(atlasSize, false);

        if (index <= 0)
        {
            DisableSceneShadows();
            ConfigurePositionalShadowAtlas(0);
            return;
        }

        RestoreSceneShadows();
        ConfigurePositionalShadowAtlas(atlasSize);
    }

    private void ApplyShadowQualityLegacy(int index)
    {
        // Shadow atlas size controls shadow resolution globally
        int atlasSize = index switch
        {
            0 => 0,     // Off, disable shadow atlas
            1 => 1024,  // Low
            2 => 2048,  // Medium
            3 => 4096,  // High
            _ => 2048
        };
        RenderingServer.DirectionalShadowAtlasSetSize(atlasSize, index > 0);
    }

    private void ApplyAO(bool enabled)
    {
        var env = GetWorldEnvironment();
        if (env?.Environment != null)
            env.Environment.SsaoEnabled = enabled;
    }

    private void ApplySSR(bool enabled)
    {
        var env = GetWorldEnvironment();
        if (env?.Environment != null)
            env.Environment.SsrEnabled = enabled;
    }

    private WorldEnvironment? GetWorldEnvironment()
    {
        return GetTree().Root.FindChild("WorldEnvironment", true, false) as WorldEnvironment;
    }

    private void ConfigurePositionalShadowAtlas(int atlasSize)
    {
        var viewport = GetViewport();
        if (viewport == null) return;

        RenderingServer.ViewportSetPositionalShadowAtlasSize(viewport.GetViewportRid(), atlasSize, false);
    }

    private void DisableSceneShadows()
    {
        foreach (var light in EnumerateLights(GetTree().Root))
        {
            ulong instanceId = light.GetInstanceId();
            if (!_shadowRestoreStates.ContainsKey(instanceId))
                _shadowRestoreStates[instanceId] = light.ShadowEnabled;
            light.ShadowEnabled = false;
        }

        _sceneShadowsForcedOff = true;
    }

    private void RestoreSceneShadows()
    {
        if (!_sceneShadowsForcedOff)
            return;

        foreach (var light in EnumerateLights(GetTree().Root))
        {
            ulong instanceId = light.GetInstanceId();
            if (_shadowRestoreStates.TryGetValue(instanceId, out bool wasEnabled))
                light.ShadowEnabled = wasEnabled;
        }

        _shadowRestoreStates.Clear();
        _sceneShadowsForcedOff = false;
    }

    private static IEnumerable<Light3D> EnumerateLights(Node node)
    {
        if (node is Light3D light)
            yield return light;

        foreach (Node child in node.GetChildren())
        {
            foreach (var nestedLight in EnumerateLights(child))
                yield return nestedLight;
        }
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
        }
        else
        {
            Engine.MaxFps = 0; // Unlimited
            if (_fpsLimitValue != null)
                _fpsLimitValue.Text = "Off";
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
    }

    private void OnVSyncToggled(bool enabled)
    {
        DisplayServer.WindowSetVsyncMode(
            enabled ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled
        );
    }

    private void OnFullscreenToggled(bool enabled)
    {
        DisplayServer.WindowSetMode(
            enabled ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed
        );
    }

    private void BuildAudioDeviceRows()
    {
        var audioVBox = GetNodeOrNull<VBoxContainer>(
            "VBox/ContentPanel/Margin/TabContainer/AudioTab/AudioContent/AudioSection/AudioMargin/AudioVBox");
        if (audioVBox == null) return;

        _outputDeviceOption = BuildDeviceRow(audioVBox, "Output Device",
            AudioServer.GetOutputDeviceList(), AudioServer.OutputDevice, out _outputActivityBar);

        _inputDeviceOption = BuildDeviceRow(audioVBox, "Input Device",
            AudioServer.GetInputDeviceList(), AudioServer.InputDevice, out _inputActivityBar);

        ResolveMeterBuses();
    }

    private OptionButton BuildDeviceRow(VBoxContainer parent, string labelText,
        string[] devices, string currentDevice, out ProgressBar activityBar)
    {
        var block = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        block.AddThemeConstantOverride("separation", 6);

        var row = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        row.AddThemeConstantOverride("separation", 12);

        var label = new Label
        {
            Text = labelText,
            CustomMinimumSize = new Vector2(100, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.AddChild(label);

        var option = new OptionButton
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 32),
        };
        int selectIdx = 0;
        for (int i = 0; i < devices.Length; i++)
        {
            option.AddItem(devices[i]);
            if (devices[i] == currentDevice)
                selectIdx = i;
        }
        option.Select(selectIdx);
        ApplyDeviceOptionTheme(option);
        row.AddChild(option);
        block.AddChild(row);

        activityBar = CreateActivityBar();
        var meterWrap = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        meterWrap.AddThemeConstantOverride("margin_left", 112);
        meterWrap.AddChild(activityBar);
        block.AddChild(meterWrap);

        parent.AddChild(block);
        return option;
    }

    private void OnOutputDeviceSelected(long index)
    {
        var device = _outputDeviceOption?.GetItemText((int)index) ?? "Default";
        AudioServer.OutputDevice = device;
        ResolveMeterBuses();
    }

    private void OnInputDeviceSelected(long index)
    {
        var device = _inputDeviceOption?.GetItemText((int)index) ?? "Default";
        AudioServer.InputDevice = device;
        ResolveMeterBuses();
    }

    private void ApplyDeviceOptionTheme(OptionButton option)
    {
        var normal = new StyleBoxFlat
        {
            BgColor = new Color(0.11f, 0.12f, 0.20f, 1f),
            BorderColor = new Color(0.40f, 0.38f, 0.85f, 0.90f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };
        normal.SetCornerRadiusAll(8);

        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = new Color(0.14f, 0.16f, 0.28f, 1f);
        hover.BorderColor = new Color(0.52f, 0.50f, 0.95f, 1f);

        option.AddThemeStyleboxOverride("normal", normal);
        option.AddThemeStyleboxOverride("hover", hover);
        option.AddThemeStyleboxOverride("focus", hover);
        option.AddThemeColorOverride("font_color", new Color(0.93f, 0.93f, 0.98f, 1f));
        option.AddThemeColorOverride("font_hover_color", new Color(1f, 1f, 1f, 1f));
        option.AddThemeColorOverride("font_focus_color", new Color(1f, 1f, 1f, 1f));

        var popup = option.GetPopup();
        var popupPanel = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.11f, 0.18f, 0.98f),
            BorderColor = new Color(0.34f, 0.35f, 0.70f, 0.90f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };
        popupPanel.SetCornerRadiusAll(8);
        popup.AddThemeStyleboxOverride("panel", popupPanel);
        popup.AddThemeColorOverride("font_color", new Color(0.90f, 0.90f, 0.96f, 1f));
        popup.AddThemeColorOverride("font_hover_color", new Color(1f, 1f, 1f, 1f));
        popup.AddThemeColorOverride("font_selected_color", new Color(0.74f, 0.83f, 1f, 1f));
    }

    private static ProgressBar CreateActivityBar()
    {
        var meter = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 100,
            Value = 0,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 12),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "Device activity"
        };

        var bg = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.14f, 1f),
            BorderColor = new Color(0.30f, 0.31f, 0.46f, 0.8f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1
        };
        bg.SetCornerRadiusAll(5);

        var fill = new StyleBoxFlat
        {
            BgColor = new Color(0.20f, 0.82f, 0.54f, 0.95f)
        };
        fill.SetCornerRadiusAll(5);

        meter.AddThemeStyleboxOverride("background", bg);
        meter.AddThemeStyleboxOverride("fill", fill);
        return meter;
    }

    private void ResolveMeterBuses()
    {
        _outputMeterBus = AudioServer.GetBusIndex("Master");
        if (_outputMeterBus < 0 && AudioServer.BusCount > 0)
            _outputMeterBus = 0;

        if (_outputActivityBar != null)
        {
            _outputActivityBar.TooltipText = _outputMeterBus >= 0
                ? $"Output meter on bus: {AudioServer.GetBusName(_outputMeterBus)}"
                : "No output bus found";
        }

        // Prefer a dedicated microphone monitor path so input meter does not depend
        // on project-specific bus naming/routing.
        _inputMeterBus = -1;
        _inputCaptureEffect = null;
        EnsureInputMonitorPath();

        if (_inputActivityBar != null)
        {
            _inputActivityBar.TooltipText = _inputMeterBus >= 0
                ? $"Input meter on bus: {AudioServer.GetBusName(_inputMeterBus)}"
                : "No input monitoring bus found";
        }
    }

    private void EnsureInputMonitorPath()
    {
        try
        {
            // Attempt to enforce input-enabled setting at runtime for this project.
            bool enabledInput = ProjectSettings.GetSetting("audio/driver/enable_input", false).AsBool();
            if (!enabledInput)
            {
                ProjectSettings.SetSetting("audio/driver/enable_input", true);
            }

            if (!Source.Godot.AudioMixer.GetMixer().TryGetCaptureInfo(out _inputCaptureEffect, out var busid))
            {
                _inputMeterBus = -1;
                _inputCaptureEffect = null;
                return;
            }
            _inputMeterBus = busid.Value;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Settings: Failed to initialize input monitor path - {ex.Message}");
            _inputMeterBus = -1;
            _inputCaptureEffect = null;
        }
    }

    private static void UpdateMeterBar(ProgressBar? bar, ref float smoothedLevel, int busIndex, float delta)
    {
        if (bar == null)
            return;

        float target = 0f;
        if (busIndex >= 0)
        {
            int channels = Math.Max(1, AudioServer.GetBusChannels(busIndex));
            for (int ch = 0; ch < channels; ch++)
            {
                float left = AudioServer.GetBusPeakVolumeLeftDb(busIndex, ch);
                float right = AudioServer.GetBusPeakVolumeRightDb(busIndex, ch);
                target = Mathf.Max(target, Mathf.Max(DbToMeter(left), DbToMeter(right)));
            }
        }

        smoothedLevel = SmoothMeter(smoothedLevel, target, delta);
        bar.Value = smoothedLevel * 100f;
        bar.Modulate = smoothedLevel > 0.05f
            ? new Color(1f, 1f, 1f, 1f)
            : new Color(0.72f, 0.72f, 0.72f, 0.80f);
    }

    private void UpdateInputMeterBar(float delta)
    {
        if (_inputActivityBar == null)
            return;

        if (_inputMonitorPlayer != null && !_inputMonitorPlayer.Playing)
            _inputMonitorPlayer.Play();

        float target = 0f;
        bool capturedAnyFrames = false;

        if (_inputCaptureEffect != null)
        {
            int frames = _inputCaptureEffect.GetFramesAvailable();
            if (frames > 0)
            {
                capturedAnyFrames = true;
                int take = Math.Min(frames, 1024);
                var buffer = _inputCaptureEffect.GetBuffer(take);
                for (int i = 0; i < buffer.Length; i++)
                {
                    var s = buffer[i];
                    target = Mathf.Max(target, Mathf.Max(Mathf.Abs(s.X), Mathf.Abs(s.Y)));
                }
            }
        }

        // Fallback to bus-peak query when capture has no frames (or no capture effect).
        if (!capturedAnyFrames && _inputMeterBus >= 0)
        {
            int channels = Math.Max(1, AudioServer.GetBusChannels(_inputMeterBus));
            for (int ch = 0; ch < channels; ch++)
            {
                float left = AudioServer.GetBusPeakVolumeLeftDb(_inputMeterBus, ch);
                float right = AudioServer.GetBusPeakVolumeRightDb(_inputMeterBus, ch);
                target = Mathf.Max(target, Mathf.Max(DbToMeter(left), DbToMeter(right)));
            }
        }

        _inputMeterLevel = SmoothMeter(_inputMeterLevel, Mathf.Clamp(target, 0f, 1f), delta);
        _inputActivityBar.Value = _inputMeterLevel * 100f;
        _inputActivityBar.Modulate = _inputMeterLevel > 0.05f
            ? new Color(1f, 1f, 1f, 1f)
            : new Color(0.72f, 0.72f, 0.72f, 0.80f);
    }

    private static float SmoothMeter(float current, float target, float delta)
    {
        float speed = target > current ? MeterAttackSpeed : MeterReleaseSpeed;
        float alpha = 1f - Mathf.Exp(-speed * Mathf.Max(delta, 0f));
        return current + (target - current) * alpha;
    }

    private static float DbToMeter(float db)
    {
        if (float.IsNaN(db) || float.IsInfinity(db))
            return 0f;

        const float floorDb = -60f;
        if (db <= floorDb)
            return 0f;

        return Mathf.Clamp((db - floorDb) / -floorDb, 0f, 1f);
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

    private static AvatarManager? ResolveLocalAvatarManager()
    {
        var localUserRoot = global::Lumora.Core.Engine.Current?.WorldManager?.FocusedWorld?.LocalUser?.Root;
        if (localUserRoot == null)
            return null;

        var manager = localUserRoot.Slot.GetComponent<AvatarManager>() ?? localUserRoot.Slot.AttachComponent<AvatarManager>();
        manager.UserRoot.Target ??= localUserRoot;
        return manager;
    }
}
