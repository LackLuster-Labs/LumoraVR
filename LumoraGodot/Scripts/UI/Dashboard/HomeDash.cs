// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using System;
using System.Collections.Generic;
using Lumora.CDN;

namespace Lumora.Godot.UI;

/// <summary>
/// Home dashboard UI controller for userspace.
/// Provides navigation between Home, Worlds, Friends, Inventory, and Settings.
/// </summary>
public partial class HomeDash : PanelContainer
{
    // Scene paths for content pages
    private const string WorldBrowserScenePath = "res://Scenes/UI/Dashboard/WorldBrowser.tscn";
    private const string InventoryBrowserScenePath = "res://Scenes/UI/Dashboard/InventoryBrowser.tscn";
    private const string SettingsScenePath = "res://Scenes/UI/Dashboard/Settings.tscn";
    private const string LoginOverlayScenePath = "res://Scenes/UI/Dashboard/LoginOverlay.tscn";

    // Navigation buttons
    private Button? _btnHome;
    private Button? _btnWorlds;
    private Button? _btnFriends;
    private Button? _btnGroups;
    private Button? _btnInventory;
    private Button? _btnSettings;
    private Button? _btnExit;

    // Login & Auth
    private PanelContainer? _userSectionPanel;
    private PanelContainer? _storageQuotaPanel;
    private LoginOverlay? _loginOverlay;
    private bool _isLoggedIn;
    private VBoxContainer? _storageContainer;
    private LumoraClient? _client;
    private UserProfile? _currentUser;

    // Quick action cards
    private Panel? _joinWorldCard;
    private Panel? _createWorldCard;
    private Panel? _avatarsCard;

    // Content containers
    private Panel? _mainContentPanel;
    private MarginContainer? _contentMargin;
    private VBoxContainer? _homeContent;
    private Control? _header;

    // Loaded content pages
    private readonly Dictionary<string, Control> _contentPages = new();
    private Control? _currentContentPage;

    // UI elements
    private Label? _usernameLabel;
    private Label? _patreonRoleLabel;
    private Label? _storageLabel;
    private ProgressBar? _storageProgress;
    private Label? _avatarIconLabel;
    private CenterContainer? _avatarContainer;
    private Label? _welcomeTitle;
    private Label? _statusText;
    private Panel? _statusDot;
    private Label? _versionLabel;
    private Label? _fpsValueLabel;

    // Current active tab
    private string _currentTab = "Home";

    // Cached nav-button styleboxes captured before any selection swap. We can't
    // re-read from the live button each call because UpdateActiveButton mutates
    // these in place. After a couple of tab switches the "pressed" reference
    // would point at the plain style and selection state visually disappears.
    private StyleBox? _navStyleActive;
    private StyleBox? _navStyleInactive;
    private StyleBox? _navStyleHover;

    public override void _Ready()
    {
        // Get navigation buttons
        _btnHome = GetNodeOrNull<Button>("VBox/StatusBar/StatusMargin/StatusContent/NavBarPanel/NavBarMargin/NavBar/BtnHome");
        _btnWorlds = GetNodeOrNull<Button>("VBox/StatusBar/StatusMargin/StatusContent/NavBarPanel/NavBarMargin/NavBar/BtnWorlds");
        _btnFriends = GetNodeOrNull<Button>("VBox/StatusBar/StatusMargin/StatusContent/NavBarPanel/NavBarMargin/NavBar/BtnFriends");
        _btnGroups = GetNodeOrNull<Button>("VBox/StatusBar/StatusMargin/StatusContent/NavBarPanel/NavBarMargin/NavBar/BtnGroups");
        _btnInventory = GetNodeOrNull<Button>("VBox/StatusBar/StatusMargin/StatusContent/NavBarPanel/NavBarMargin/NavBar/BtnInventory");
        _btnSettings = GetNodeOrNull<Button>("VBox/StatusBar/StatusMargin/StatusContent/NavBarPanel/NavBarMargin/NavBar/BtnSettings");
        _btnExit = GetNodeOrNull<Button>("VBox/StatusBar/StatusMargin/StatusContent/NavBarPanel/NavBarMargin/NavBar/BtnExit");

        // Capture the active/inactive/hover nav button styleboxes BEFORE any
        // selection swap mutates them. BtnHome's .tscn normal style is the
        // "selected" look; BtnWorlds carries the regular look. Hover is the
        // same on every nav button.
        _navStyleActive = _btnHome?.GetThemeStylebox("normal");
        _navStyleInactive = _btnWorlds?.GetThemeStylebox("normal");
        _navStyleHover = _btnWorlds?.GetThemeStylebox("hover");

        // Get content containers
        _header = GetNodeOrNull<Control>("VBox/Header");
        _mainContentPanel = GetNodeOrNull<Panel>("VBox/ContentArea/MainContent");
        _contentMargin = GetNodeOrNull<MarginContainer>("VBox/ContentArea/MainContent/ContentMargin");
        _homeContent = GetNodeOrNull<VBoxContainer>("VBox/ContentArea/MainContent/ContentMargin/ContentVBox");

        // Get quick action cards
        _joinWorldCard = GetNodeOrNull<Panel>("VBox/ContentArea/MainContent/ContentMargin/ContentVBox/QuickActions/JoinWorld");
        _createWorldCard = GetNodeOrNull<Panel>("VBox/ContentArea/MainContent/ContentMargin/ContentVBox/QuickActions/CreateWorld");
        _avatarsCard = GetNodeOrNull<Panel>("VBox/ContentArea/MainContent/ContentMargin/ContentVBox/QuickActions/BrowseAvatars");

        // Get user section elements
        _userSectionPanel = GetNodeOrNull<PanelContainer>("VBox/Header/HeaderContent/UserSectionPanel");
        _storageQuotaPanel = GetNodeOrNull<PanelContainer>("VBox/Header/HeaderContent/StorageQuotaPanel");
        _usernameLabel = GetNodeOrNull<Label>("VBox/Header/HeaderContent/UserSectionPanel/UserSection/UserInfoVBox/Username");
        _patreonRoleLabel = GetNodeOrNull<Label>("VBox/Header/HeaderContent/UserSectionPanel/UserSection/UserInfoVBox/PatreonRole");
        _storageContainer = GetNodeOrNull<VBoxContainer>("VBox/Header/HeaderContent/StorageQuotaPanel/StorageQuotaVBox/StorageContainer");
        _storageLabel = GetNodeOrNull<Label>("VBox/Header/HeaderContent/StorageQuotaPanel/StorageQuotaVBox/StorageContainer/StorageLabel");
        _storageProgress = GetNodeOrNull<ProgressBar>("VBox/Header/HeaderContent/StorageQuotaPanel/StorageQuotaVBox/StorageContainer/StorageProgress");
        _avatarIconLabel = GetNodeOrNull<Label>("VBox/Header/HeaderContent/UserSectionPanel/UserSection/AvatarContainer/AvatarCircle/AvatarIcon");
        _avatarContainer = GetNodeOrNull<CenterContainer>("VBox/Header/HeaderContent/UserSectionPanel/UserSection/AvatarContainer");

        // Get other labels
        _welcomeTitle = GetNodeOrNull<Label>("VBox/ContentArea/MainContent/ContentMargin/ContentVBox/WelcomeSection/WelcomeTitle");
        _statusText = GetNodeOrNull<Label>("VBox/StatusBar/StatusMargin/StatusContent/ConnectionPanel/ConnectionCenter/ConnectionStatus/StatusText");
        _statusDot = GetNodeOrNull<Panel>("VBox/StatusBar/StatusMargin/StatusContent/ConnectionPanel/ConnectionCenter/ConnectionStatus/DotContainer/StatusDot");
        _versionLabel = GetNodeOrNull<Label>("VBox/StatusBar/StatusMargin/StatusContent/VersionPanel/Version");
        _fpsValueLabel = GetNodeOrNull<Label>("VBox/Header/HeaderContent/FPSPanel/FPSVBox/FPSValue");

        SetVersion(global::Lumora.Core.BuildInfo.Version);

        // Connect button signals
        ConnectButtons();

        // Create LumoraClient
        var deviceId = OS.GetUniqueId();
        _client = new LumoraClient(deviceId);

        // Create login overlay
        CreateLoginOverlay();

        // Set initial logged out state
        SetLoggedOutState();
        UpdateHeaderVisibility();

    }

    public override void _Process(double delta)
    {
        // Update FPS counter with real value
        int fps = (int)Engine.GetFramesPerSecond();
        SetFPS(fps);
    }

    private void ConnectButtons()
    {
        _btnHome?.Connect("pressed", Callable.From(() => OnNavButtonPressed("Home")));
        _btnWorlds?.Connect("pressed", Callable.From(() => OnNavButtonPressed("Worlds")));
        _btnFriends?.Connect("pressed", Callable.From(() => OnNavButtonPressed("Friends")));
        _btnGroups?.Connect("pressed", Callable.From(() => OnNavButtonPressed("Groups")));
        _btnInventory?.Connect("pressed", Callable.From(() => OnNavButtonPressed("Inventory")));
        _btnSettings?.Connect("pressed", Callable.From(() => OnNavButtonPressed("Settings")));
        _btnExit?.Connect("pressed", Callable.From(OnExitPressed));

        // User section click to show login (when not logged in)
        if (_userSectionPanel != null)
        {
            _userSectionPanel.GuiInput += OnUserSectionInput;
        }

        // Connect card interactions (make them clickable)
        if (_joinWorldCard != null)
        {
            _joinWorldCard.GuiInput += (e) => OnCardInput(e, "JoinWorld");
        }
        if (_createWorldCard != null)
        {
            _createWorldCard.GuiInput += (e) => OnCardInput(e, "CreateWorld");
        }
        if (_avatarsCard != null)
        {
            _avatarsCard.GuiInput += (e) => OnCardInput(e, "Avatars");
        }
    }

    private void OnNavButtonPressed(string tab)
    {
        if (_currentTab == tab) return;

        _currentTab = tab;
        UpdateActiveButton();
        UpdateHeaderVisibility();
        SwitchContent(tab);

    }

    private void UpdateHeaderVisibility()
    {
        if (_header != null)
            _header.Visible = _currentTab == "Home";
    }

    private void SwitchContent(string tab)
    {
        // Hide current content
        if (_currentContentPage != null)
        {
            _currentContentPage.Visible = false;
        }
        if (_homeContent != null)
        {
            _homeContent.Visible = tab == "Home";
        }

        // Show or load the requested content
        if (tab == "Home")
        {
            _currentContentPage = null;
            return;
        }

        // Check if page already loaded
        if (_contentPages.TryGetValue(tab, out var existingPage))
        {
            existingPage.Visible = true;
            _currentContentPage = existingPage;
            return;
        }

        // Load the content page
        var page = LoadContentPage(tab);
        if (page != null)
        {
            _contentPages[tab] = page;
            _currentContentPage = page;
        }
    }

    private Control? LoadContentPage(string tab)
    {
        string? scenePath = tab switch
        {
            "Worlds" => WorldBrowserScenePath,
            "Inventory" => InventoryBrowserScenePath,
            "Settings" => SettingsScenePath,
            // Add more pages here as they're created
            // "Friends" => FriendsScenePath,
            _ => null
        };

        if (scenePath == null)
        {
            return null;
        }

        var packedScene = GD.Load<PackedScene>(scenePath);
        if (packedScene == null)
        {
            GD.PrintErr($"HomeDash: Failed to load scene '{scenePath}'");
            return null;
        }

        var page = packedScene.Instantiate<Control>();
        if (page == null)
        {
            GD.PrintErr($"HomeDash: Failed to instantiate scene '{scenePath}'");
            return null;
        }

        // Add to content area
        if (_contentMargin != null)
        {
            _contentMargin.AddChild(page);

            // Make it fill the container
            page.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            page.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            page.SizeFlagsVertical = SizeFlags.ExpandFill;
        }

        // Initialize Settings page with client and user data
        if (tab == "Settings" && page is Settings settingsPage)
        {
            if (_client != null)
                settingsPage.SetClient(_client);

            if (_currentUser != null)
                settingsPage.SetUserData(_currentUser.Username, "", _currentUser.TwoFactorEnabled);
        }

        if (tab == "Worlds" && page is WorldBrowser worldBrowser)
        {
            if (_client != null)
                worldBrowser.SetClient(_client);
        }

        // Initialize Inventory page
        if (tab == "Inventory" && page is InventoryBrowser inventoryPage)
        {
            if (_client != null)
                inventoryPage.SetClient(_client);

            inventoryPage.SetCurrentUser(_currentUser);
        }

        return page;
    }

    private void UpdateActiveButton()
    {
        Button?[] buttons = { _btnHome, _btnWorlds, _btnFriends, _btnGroups, _btnInventory, _btnSettings };
        string[] tabs = { "Home", "Worlds", "Friends", "Groups", "Inventory", "Settings" };

        if (_navStyleActive == null || _navStyleInactive == null)
            return;

        for (int i = 0; i < buttons.Length; i++)
        {
            var btn = buttons[i];
            if (btn == null) continue;

            bool isActive = tabs[i] == _currentTab;

            if (isActive)
            {
                // Active tab: use the bright "pressed" style for normal AND hover so
                // hovering the already-selected tab doesn't visually unselect it.
                btn.AddThemeStyleboxOverride("normal", _navStyleActive);
                btn.AddThemeStyleboxOverride("hover", _navStyleActive);
                btn.AddThemeStyleboxOverride("pressed", _navStyleActive);
                btn.AddThemeColorOverride("font_color", new Color(1, 1, 1, 1));
                btn.AddThemeColorOverride("font_hover_color", new Color(1, 1, 1, 1));
                btn.Modulate = new Color(1, 1, 1, 1);
            }
            else
            {
                btn.AddThemeStyleboxOverride("normal", _navStyleInactive);
                if (_navStyleHover != null)
                    btn.AddThemeStyleboxOverride("hover", _navStyleHover);
                btn.AddThemeStyleboxOverride("pressed", _navStyleActive);
                btn.AddThemeColorOverride("font_color", new Color(0.82f, 0.82f, 0.9f, 1));
                btn.AddThemeColorOverride("font_hover_color", new Color(1, 1, 1, 1));
                btn.Modulate = new Color(1, 1, 1, 1);
            }
        }
    }

    private void OnCardInput(InputEvent @event, string cardType)
    {
        if (@event is InputEventMouseButton mouseEvent && mouseEvent.Pressed && mouseEvent.ButtonIndex == MouseButton.Left)
        {
            OnCardPressed(cardType);
        }
    }

    private void OnCardPressed(string cardType)
    {
        switch (cardType)
        {
            case "JoinWorld":
                OnNavButtonPressed("Worlds");
                break;
            case "CreateWorld":
                // TODO: Show create world dialog
                break;
            case "Avatars":
                OnNavButtonPressed("Inventory");
                break;
        }
    }

    private void OnExitPressed()
    {
        // Quit cleanly from the dashboard.
        // In-editor this stops the running game, and in exports this closes the app.
        GD.Print("HomeDash: Exit pressed, quitting application");
        GetTree().Quit();
    }

    private void CreateLoginOverlay()
    {
        var packedScene = GD.Load<PackedScene>(LoginOverlayScenePath);
        if (packedScene == null)
        {
            GD.PrintErr("HomeDash: Failed to load LoginOverlay scene");
            return;
        }

        _loginOverlay = packedScene.Instantiate<LoginOverlay>();
        if (_loginOverlay == null)
        {
            GD.PrintErr("HomeDash: Failed to instantiate LoginOverlay");
            return;
        }

        _loginOverlay.Visible = false;
        _loginOverlay.OnLoginSuccess += OnLoginSuccess;
        _loginOverlay.OnCancel += OnLoginCancel;

        // Pass the client to the overlay
        if (_client != null)
            _loginOverlay.SetClient(_client);

        // Add to root so it overlays everything
        AddChild(_loginOverlay);
        _loginOverlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);

    }

    private void OnUserSectionInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseEvent && mouseEvent.Pressed && mouseEvent.ButtonIndex == MouseButton.Left)
        {
            if (_isLoggedIn)
            {
                // Already logged in - could show profile/account options
                // TODO: Show account dropdown or navigate to settings
            }
            else
            {
                _loginOverlay?.Show();
            }
        }
    }

    private void SetLoggedOutState()
    {
        _isLoggedIn = false;

        // Logged-out: hide avatar and reframe the panel as a clear "Sign In" CTA
        // so the empty state doesn't look like a broken profile card.
        if (_avatarContainer != null)
            _avatarContainer.Visible = false;

        if (_usernameLabel != null)
        {
            _usernameLabel.Text = "Sign In";
            _usernameLabel.AddThemeColorOverride("font_color", new Color(0.78f, 0.72f, 1f, 1f));
            _usernameLabel.AddThemeFontSizeOverride("font_size", 14);
            _usernameLabel.HorizontalAlignment = HorizontalAlignment.Center;
        }

        if (_patreonRoleLabel != null)
        {
            _patreonRoleLabel.Text = "Click to log in to your account";
            _patreonRoleLabel.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.65f, 1f));
            _patreonRoleLabel.AddThemeFontSizeOverride("font_size", 9);
            _patreonRoleLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _patreonRoleLabel.Visible = true;
        }

        if (_storageContainer != null)
            _storageContainer.Visible = false;
        if (_storageQuotaPanel != null)
            _storageQuotaPanel.Visible = false;
        if (_storageProgress != null)
            _storageProgress.Value = 0.0f;

        if (_welcomeTitle != null)
            _welcomeTitle.Text = "Welcome!";
    }

    private async void OnLoginSuccess()
    {
        _isLoggedIn = true;
        _loginOverlay?.Hide();

        // Show storage and patreon when logged in
        if (_patreonRoleLabel != null)
            _patreonRoleLabel.Visible = true;

        if (_storageContainer != null)
            _storageContainer.Visible = true;
        if (_storageQuotaPanel != null)
            _storageQuotaPanel.Visible = true;

        // Fetch user data from LumoraClient
        if (_client != null)
        {
            var result = await _client.GetCurrentUser();
            if (result.Success && result.Data != null)
            {
                _currentUser = result.Data;
                ApplyUserProfile(_currentUser);
                PushUserDataToLoadedPages();
            }
            else
            {
                GD.PrintErr($"HomeDash: Failed to fetch user profile - {result.Message}");
            }
        }

    }

    private void ApplyUserProfile(UserProfile profile)
    {
        SetUsername(profile.Username);

        // Set Patreon role if available
        if (profile.PatreonData != null && profile.PatreonData.IsActiveSupporter)
        {
            SetPatreonRole(profile.PatreonData.TierName ?? "Supporter");
        }
        else
        {
            SetPatreonRole("");
        }

        // Set storage usage
        if (profile.StorageQuota != null)
        {
            float usedGB = profile.StorageQuota.UsedMB / 1024f;
            float totalGB = profile.StorageQuota.QuotaMB / 1024f;
            SetStorageUsage(usedGB, totalGB);
        }

    }

    private void PushUserDataToLoadedPages()
    {
        if (_contentPages.TryGetValue("Settings", out var settingsControl) && settingsControl is Settings settingsPage)
        {
            if (_client != null)
                settingsPage.SetClient(_client);

            if (_currentUser != null)
                settingsPage.SetUserData(_currentUser.Username, "", _currentUser.TwoFactorEnabled);
        }

        if (_contentPages.TryGetValue("Inventory", out var inventoryControl) && inventoryControl is InventoryBrowser inventoryPage)
        {
            if (_client != null)
                inventoryPage.SetClient(_client);

            inventoryPage.SetCurrentUser(_currentUser);
        }

        if (_contentPages.TryGetValue("Worlds", out var worldsControl) && worldsControl is WorldBrowser worldBrowser)
        {
            if (_client != null)
                worldBrowser.SetClient(_client);
        }
    }

    private void OnLoginCancel()
    {
        _loginOverlay?.Hide();
    }

    /// <summary>
    /// Get the LumoraClient for use by child pages.
    /// </summary>
    public LumoraClient? GetClient() => _client;

    /// <summary>
    /// Get the current user profile.
    /// </summary>
    public UserProfile? GetCurrentUserProfile() => _currentUser;

    /// <summary>
    /// Set the displayed username.
    /// </summary>
    public void SetUsername(string username)
    {
        if (_avatarContainer != null)
            _avatarContainer.Visible = true;

        if (_usernameLabel != null)
        {
            _usernameLabel.Text = username;
            // Clear any sign-in-state overrides applied by SetLoggedOutState.
            _usernameLabel.RemoveThemeColorOverride("font_color");
            _usernameLabel.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.95f, 1f));
            _usernameLabel.AddThemeFontSizeOverride("font_size", 11);
            _usernameLabel.HorizontalAlignment = HorizontalAlignment.Left;
        }
        if (_patreonRoleLabel != null)
        {
            _patreonRoleLabel.AddThemeFontSizeOverride("font_size", 9);
            _patreonRoleLabel.AddThemeColorOverride("font_color", new Color(0.47f, 0.37f, 0.94f, 0.9f));
            _patreonRoleLabel.HorizontalAlignment = HorizontalAlignment.Left;
        }
        if (_welcomeTitle != null)
        {
            _welcomeTitle.Text = $"Welcome back, {username}!";
        }
        if (_avatarIconLabel != null)
        {
            _avatarIconLabel.Text = string.IsNullOrEmpty(username) ? "?" : username[0].ToString().ToUpper();
        }
    }

    /// <summary>
    /// Set the Patreon role display.
    /// </summary>
    public void SetPatreonRole(string role)
    {
        if (_patreonRoleLabel != null)
        {
            _patreonRoleLabel.Text = role;
            _patreonRoleLabel.Visible = !string.IsNullOrEmpty(role);
        }
    }

    /// <summary>
    /// Set storage usage display.
    /// </summary>
    /// <param name="usedGB">Used storage in GB</param>
    /// <param name="totalGB">Total storage in GB</param>
    public void SetStorageUsage(float usedGB, float totalGB)
    {
        if (_storageLabel != null)
        {
            _storageLabel.Text = $"Storage: {usedGB:F1} GB / {totalGB:F0} GB";
        }
        if (_storageProgress != null)
        {
            float percent = totalGB > 0 ? usedGB / totalGB : 0;
            _storageProgress.Value = Mathf.Clamp(percent, 0f, 1f);
        }
    }

    /// <summary>
    /// Set the connection status display.
    /// </summary>
    public void SetConnectionStatus(string status, bool isConnected = true)
    {
        if (_statusText != null)
        {
            _statusText.Text = status;
        }
        if (_statusDot != null)
        {
            _statusDot.SelfModulate = isConnected
                ? new Color(0.3f, 0.85f, 0.5f)
                : new Color(0.85f, 0.3f, 0.3f);
        }
    }

    /// <summary>
    /// Set the version label text.
    /// </summary>
    public void SetVersion(string version)
    {
        if (_versionLabel != null)
        {
            _versionLabel.Text = $"Lumora v{version}";
        }
    }

    /// <summary>
    /// Set the FPS display value.
    /// </summary>
    public void SetFPS(int fps)
    {
        if (_fpsValueLabel != null)
        {
            _fpsValueLabel.Text = fps.ToString();
            // Color code: green for good, yellow for medium, red for low
            if (fps >= 60)
                _fpsValueLabel.Modulate = new Color(0.3f, 0.85f, 0.5f);
            else if (fps >= 30)
                _fpsValueLabel.Modulate = new Color(0.9f, 0.75f, 0.2f);
            else
                _fpsValueLabel.Modulate = new Color(0.9f, 0.4f, 0.35f);
        }
    }
}
