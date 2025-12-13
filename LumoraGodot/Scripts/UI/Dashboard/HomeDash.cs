using Godot;
using System;
using System.Collections.Generic;

namespace Lumora.Godot.UI;

/// <summary>
/// Home dashboard UI controller for userspace.
/// Provides navigation between Home, Worlds, Friends, Inventory, and Settings.
/// </summary>
public partial class HomeDash : Control
{
    // Scene paths for content pages
    private const string WorldBrowserScenePath = "res://Scenes/UI/Dashboard/WorldBrowser.tscn";

    // Navigation buttons
    private Button? _btnHome;
    private Button? _btnWorlds;
    private Button? _btnFriends;
    private Button? _btnGroups;
    private Button? _btnInventory;
    private Button? _btnSettings;
    private Button? _btnExit;

    // Quick action cards
    private Panel? _joinWorldCard;
    private Panel? _createWorldCard;
    private Panel? _avatarsCard;

    // Content containers
    private Panel? _mainContentPanel;
    private MarginContainer? _contentMargin;
    private VBoxContainer? _homeContent;

    // Loaded content pages
    private readonly Dictionary<string, Control> _contentPages = new();
    private Control? _currentContentPage;

    // UI elements
    private Label? _usernameLabel;
    private Label? _patreonRoleLabel;
    private Label? _storageLabel;
    private Panel? _storageBarFill;
    private Label? _avatarIconLabel;
    private Label? _welcomeTitle;
    private Label? _statusText;
    private Label? _versionLabel;
    private Label? _fpsValueLabel;

    // Current active tab
    private string _currentTab = "Home";

    public override void _Ready()
    {
        // Get navigation buttons
        _btnHome = GetNodeOrNull<Button>("MainContainer/VBox/ContentArea/Sidebar/SidebarMargin/NavButtons/BtnHome");
        _btnWorlds = GetNodeOrNull<Button>("MainContainer/VBox/ContentArea/Sidebar/SidebarMargin/NavButtons/BtnWorlds");
        _btnFriends = GetNodeOrNull<Button>("MainContainer/VBox/ContentArea/Sidebar/SidebarMargin/NavButtons/BtnFriends");
        _btnGroups = GetNodeOrNull<Button>("MainContainer/VBox/ContentArea/Sidebar/SidebarMargin/NavButtons/BtnGroups");
        _btnInventory = GetNodeOrNull<Button>("MainContainer/VBox/ContentArea/Sidebar/SidebarMargin/NavButtons/BtnInventory");
        _btnSettings = GetNodeOrNull<Button>("MainContainer/VBox/ContentArea/Sidebar/SidebarMargin/NavButtons/BtnSettings");
        _btnExit = GetNodeOrNull<Button>("MainContainer/VBox/ContentArea/Sidebar/SidebarMargin/NavButtons/BtnExit");

        // Get content containers
        _mainContentPanel = GetNodeOrNull<Panel>("MainContainer/VBox/ContentArea/MainContent");
        _contentMargin = GetNodeOrNull<MarginContainer>("MainContainer/VBox/ContentArea/MainContent/ContentMargin");
        _homeContent = GetNodeOrNull<VBoxContainer>("MainContainer/VBox/ContentArea/MainContent/ContentMargin/ContentVBox");

        // Get quick action cards
        _joinWorldCard = GetNodeOrNull<Panel>("MainContainer/VBox/ContentArea/MainContent/ContentMargin/ContentVBox/QuickActions/JoinWorld");
        _createWorldCard = GetNodeOrNull<Panel>("MainContainer/VBox/ContentArea/MainContent/ContentMargin/ContentVBox/QuickActions/CreateWorld");
        _avatarsCard = GetNodeOrNull<Panel>("MainContainer/VBox/ContentArea/MainContent/ContentMargin/ContentVBox/QuickActions/BrowseAvatars");

        // Get user section elements
        _usernameLabel = GetNodeOrNull<Label>("MainContainer/VBox/Header/HeaderContent/UserSectionPanel/UserSectionMargin/UserSection/UserInfoVBox/Username");
        _patreonRoleLabel = GetNodeOrNull<Label>("MainContainer/VBox/Header/HeaderContent/UserSectionPanel/UserSectionMargin/UserSection/UserInfoVBox/PatreonRole");
        _storageLabel = GetNodeOrNull<Label>("MainContainer/VBox/Header/HeaderContent/UserSectionPanel/UserSectionMargin/UserSection/UserInfoVBox/StorageContainer/StorageLabel");
        _storageBarFill = GetNodeOrNull<Panel>("MainContainer/VBox/Header/HeaderContent/UserSectionPanel/UserSectionMargin/UserSection/UserInfoVBox/StorageContainer/StorageBarBg/StorageBarFill");
        _avatarIconLabel = GetNodeOrNull<Label>("MainContainer/VBox/Header/HeaderContent/UserSectionPanel/UserSectionMargin/UserSection/AvatarContainer/AvatarCircle/AvatarIcon");

        // Get other labels
        _welcomeTitle = GetNodeOrNull<Label>("MainContainer/VBox/ContentArea/MainContent/ContentMargin/ContentVBox/WelcomeSection/WelcomeTitle");
        _statusText = GetNodeOrNull<Label>("MainContainer/VBox/StatusBar/StatusContent/ConnectionPanel/ConnectionCenter/ConnectionStatus/StatusText");
        _versionLabel = GetNodeOrNull<Label>("MainContainer/VBox/StatusBar/StatusContent/VersionPanel/VersionCenter/Version");
        _fpsValueLabel = GetNodeOrNull<Label>("MainContainer/VBox/Header/HeaderContent/FPSPanel/FPSMargin/FPSVBox/FPSValue");

        // Connect button signals
        ConnectButtons();

        GD.Print("HomeDash: Initialized");
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
        SwitchContent(tab);

        GD.Print($"HomeDash: Navigated to {tab}");
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
            // Add more pages here as they're created
            // "Friends" => FriendsScenePath,
            // "Inventory" => InventoryScenePath,
            _ => null
        };

        if (scenePath == null)
        {
            GD.Print($"HomeDash: No content page for tab '{tab}' yet");
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

        GD.Print($"HomeDash: Loaded content page '{tab}'");
        return page;
    }

    private void UpdateActiveButton()
    {
        // Update button visual states based on current tab
        Button?[] buttons = { _btnHome, _btnWorlds, _btnFriends, _btnGroups, _btnInventory, _btnSettings };
        string[] tabs = { "Home", "Worlds", "Friends", "Groups", "Inventory", "Settings" };

        // Get style references from _btnHome (which has the pressed style as default)
        var pressedStyle = _btnHome?.GetThemeStylebox("normal");
        var normalStyle = _btnWorlds?.GetThemeStylebox("normal");

        for (int i = 0; i < buttons.Length; i++)
        {
            if (buttons[i] == null) continue;

            bool isActive = tabs[i] == _currentTab;

            // Swap the normal style to show active state
            if (isActive && pressedStyle != null)
            {
                buttons[i]!.AddThemeStyleboxOverride("normal", pressedStyle);
                buttons[i]!.Modulate = new Color(1, 1, 1, 1);
            }
            else if (normalStyle != null)
            {
                buttons[i]!.AddThemeStyleboxOverride("normal", normalStyle);
                buttons[i]!.Modulate = new Color(0.85f, 0.85f, 0.85f, 1);
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
        GD.Print($"HomeDash: Card pressed - {cardType}");

        switch (cardType)
        {
            case "JoinWorld":
                // TODO: Show world browser
                OnNavButtonPressed("Worlds");
                break;
            case "CreateWorld":
                // TODO: Show create world dialog
                break;
            case "Avatars":
                // TODO: Show avatar selector
                OnNavButtonPressed("Inventory");
                break;
        }
    }

    private void OnExitPressed()
    {
        GD.Print("HomeDash: Exit pressed");
        // TODO: Implement exit confirmation or directly quit
        // GetTree().Quit();
    }

    /// <summary>
    /// Set the displayed username.
    /// </summary>
    public void SetUsername(string username)
    {
        if (_usernameLabel != null)
        {
            _usernameLabel.Text = username;
        }
        if (_welcomeTitle != null)
        {
            _welcomeTitle.Text = $"Welcome back, {username}!";
        }
        if (_avatarIconLabel != null)
        {
            // Set first letter of username as avatar placeholder
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
        if (_storageBarFill != null)
        {
            float percent = totalGB > 0 ? usedGB / totalGB : 0;
            // Storage bar bg is 120px wide, fill should be proportional (with 1px margin)
            float fillWidth = Mathf.Clamp(percent * 118f, 2f, 118f);
            _storageBarFill.CustomMinimumSize = new Vector2(fillWidth, 4);
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
            // TODO: Update status dot color based on isConnected
        }
    }

    /// <summary>
    /// Set the version label text.
    /// </summary>
    public void SetVersion(string version)
    {
        if (_versionLabel != null)
        {
            _versionLabel.Text = $"Lumora {version}";
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
