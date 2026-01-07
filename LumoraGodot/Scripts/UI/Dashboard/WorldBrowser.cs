using Godot;
using System;
using System.Collections.Generic;
using Aquamarine.Source.Godot.Services;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.UI;

/// <summary>
/// World browser UI for discovering and joining worlds.
/// </summary>
public partial class WorldBrowser : Control
{
    // Service references
    private SessionBrowserService _browserService;
    private SessionThumbnailService _thumbnailService;
    public class WorldInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public int UserCount { get; set; }
        public int MaxUsers { get; set; } = 16;
        public string ThumbnailUrl { get; set; } = "";
        public string Category { get; set; } = "";
        public Texture2D? Thumbnail { get; set; }
    }

    public class CategoryInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Icon { get; set; } = "";
    }

    private static readonly CategoryInfo[] DefaultCategories = new[]
    {
        new CategoryInfo { Id = "featured", Name = "Featured", Icon = "⭐" },
        new CategoryInfo { Id = "active", Name = "Active Sessions", Icon = "🟢" },
        new CategoryInfo { Id = "social", Name = "Social", Icon = "💬" },
        new CategoryInfo { Id = "games", Name = "Games", Icon = "🎮" },
        new CategoryInfo { Id = "art", Name = "Art", Icon = "🎨" },
        new CategoryInfo { Id = "education", Name = "Educational", Icon = "📚" },
        new CategoryInfo { Id = "my_worlds", Name = "My Worlds", Icon = "📁" },
        new CategoryInfo { Id = "recent", Name = "Recent", Icon = "🕐" },
    };

    private const string WorldCardScenePath = "res://Scenes/UI/Components/WorldCard.tscn";
    private const string CategoryButtonScenePath = "res://Scenes/UI/Components/CategoryButton.tscn";

    private LineEdit? _searchBox;
    private Label? _currentWorldLabel;
    private Label? _resultCountLabel;
    private Button? _refreshButton;
    private Button? _hostButton;
    private VBoxContainer? _categoriesList;
    private GridContainer? _worldsGrid;
    private ScrollContainer? _worldsScroll;
    private Label? _loadingLabel;
    private Label? _noResultsLabel;

    // Host dialog controls
    private Control? _hostDialog;
    private LineEdit? _hostNameInput;
    private SpinBox? _hostMaxUsersInput;
    private OptionButton? _hostVisibilityInput;
    private OptionButton? _hostTemplateInput;
    private Button? _hostConfirmButton;
    private Button? _hostCancelButton;

    // World detail panel controls
    private Control? _worldDetailPanel;
    private Label? _detailTitle;
    private Label? _detailHost;
    private Label? _detailUserCount;
    private Label? _detailCategory;
    private Label? _detailDescription;
    private TextureRect? _detailThumbnail;
    private Button? _detailJoinButton;
    private Button? _detailCloseButton;
    private WorldInfo? _selectedWorldInfo;

    private PackedScene? _worldCardScene;
    private PackedScene? _categoryButtonScene;

    private readonly List<Button> _categoryButtons = new();
    private readonly List<Button> _worldCards = new();
    private readonly Dictionary<string, WorldInfo> _worldsCache = new();

    private string _selectedCategory = "featured";
    private string _searchQuery = "";

    public event Action<WorldInfo>? WorldSelected;
    public event Action<string>? CategoryChanged;

    public override void _Ready()
    {
        // Get node references
        _searchBox = GetNodeOrNull<LineEdit>("MainHBox/ContentArea/Header/HeaderMargin/HeaderHBox/SearchBox");
        _currentWorldLabel = GetNodeOrNull<Label>("MainHBox/Sidebar/SidebarMargin/SidebarVBox/CurrentWorld");
        _resultCountLabel = GetNodeOrNull<Label>("MainHBox/ContentArea/Header/HeaderMargin/HeaderHBox/ResultCount");
        _refreshButton = GetNodeOrNull<Button>("MainHBox/ContentArea/Header/HeaderMargin/HeaderHBox/RefreshButton");
        _categoriesList = GetNodeOrNull<VBoxContainer>("MainHBox/Sidebar/SidebarMargin/SidebarVBox/CategoriesScroll/CategoriesList");
        _worldsGrid = GetNodeOrNull<GridContainer>("MainHBox/ContentArea/ContentMargin/WorldsScroll/WorldsGrid");
        _worldsScroll = GetNodeOrNull<ScrollContainer>("MainHBox/ContentArea/ContentMargin/WorldsScroll");
        _loadingLabel = GetNodeOrNull<Label>("MainHBox/ContentArea/ContentMargin/LoadingLabel");
        _noResultsLabel = GetNodeOrNull<Label>("MainHBox/ContentArea/ContentMargin/NoResultsLabel");

        // Load scenes
        _worldCardScene = GD.Load<PackedScene>(WorldCardScenePath);
        _categoryButtonScene = GD.Load<PackedScene>(CategoryButtonScenePath);

        if (_worldCardScene == null)
            GD.PrintErr($"WorldBrowser: Failed to load {WorldCardScenePath}");
        if (_categoryButtonScene == null)
            GD.PrintErr($"WorldBrowser: Failed to load {CategoryButtonScenePath}");

        // Connect signals
        _searchBox?.Connect("text_changed", Callable.From<string>(OnSearchTextChanged));
        _refreshButton?.Connect("pressed", Callable.From(OnRefreshPressed));

        // Create host button if it doesn't exist
        CreateHostButton();

        // Create host dialog
        CreateHostDialog();

        // Create world detail panel
        CreateWorldDetailPanel();

        // Build UI
        CreateCategoryButtons();

        // Initialize session browser service
        InitializeSessionBrowser();

        AquaLogger.Log("WorldBrowser: Initialized");
    }

    private void InitializeSessionBrowser()
    {
        // Create or find SessionBrowserService
        _browserService = GetNodeOrNull<SessionBrowserService>("/root/SessionBrowserService");
        if (_browserService == null)
        {
            _browserService = new SessionBrowserService();
            _browserService.Name = "SessionBrowserService";
            GetTree().Root.AddChild(_browserService);
        }

        // Connect service to this UI
        _browserService.ConnectToUI(this);

        // Subscribe to join events
        _browserService.OnJoinStarted += OnJoinStarted;
        _browserService.OnJoinSuccess += OnJoinSucceeded;
        _browserService.OnJoinFailed += OnJoinFailed;

        // Start scanning for sessions
        _browserService.StartScanning();
    }

    private void OnJoinStarted(string worldName)
    {
        SetLoading(true);
        SetCurrentWorld($"Joining {worldName}...");
    }

    private void OnJoinSucceeded(Lumora.Core.World world)
    {
        SetLoading(false);
        SetCurrentWorld(world?.WorldName?.Value ?? "Unknown");
        AquaLogger.Log($"WorldBrowser: Successfully joined world");
    }

    private void OnJoinFailed(string reason)
    {
        SetLoading(false);
        SetCurrentWorld($"Join failed: {reason}");
        AquaLogger.Warn($"WorldBrowser: Join failed - {reason}");
    }

    #region Host Session UI

    private void CreateHostButton()
    {
        // Try to find existing host button in header
        var headerHBox = GetNodeOrNull<HBoxContainer>("MainHBox/ContentArea/Header/HeaderMargin/HeaderHBox");
        if (headerHBox == null)
            return;

        _hostButton = headerHBox.GetNodeOrNull<Button>("HostButton");
        if (_hostButton == null)
        {
            // Create host button
            _hostButton = new Button();
            _hostButton.Name = "HostButton";
            _hostButton.Text = "Host Session";
            _hostButton.CustomMinimumSize = new Vector2(120, 0);
            headerHBox.AddChild(_hostButton);
            // Move before refresh button
            headerHBox.MoveChild(_hostButton, headerHBox.GetChildCount() - 2);
        }

        _hostButton.Connect("pressed", Callable.From(OnHostButtonPressed));
    }

    private void CreateHostDialog()
    {
        // Create modal dialog for hosting
        _hostDialog = new Control();
        _hostDialog.Name = "HostDialog";
        _hostDialog.Visible = false;
        _hostDialog.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_hostDialog);

        // Background overlay
        var overlay = new ColorRect();
        overlay.Color = new Color(0, 0, 0, 0.7f);
        overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _hostDialog.AddChild(overlay);

        // Dialog panel
        var panel = new PanelContainer();
        panel.CustomMinimumSize = new Vector2(400, 300);
        panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        panel.Position = new Vector2(-200, -150);
        _hostDialog.AddChild(panel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 15);
        panel.AddChild(vbox);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 20);
        margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        vbox.AddChild(margin);

        var innerVbox = new VBoxContainer();
        innerVbox.AddThemeConstantOverride("separation", 10);
        margin.AddChild(innerVbox);

        // Title
        var title = new Label();
        title.Text = "Host New Session";
        title.HorizontalAlignment = HorizontalAlignment.Center;
        innerVbox.AddChild(title);

        // Session name
        var nameLabel = new Label();
        nameLabel.Text = "Session Name:";
        innerVbox.AddChild(nameLabel);

        _hostNameInput = new LineEdit();
        _hostNameInput.PlaceholderText = "My World";
        _hostNameInput.Text = "My World";
        innerVbox.AddChild(_hostNameInput);

        // Max users
        var maxLabel = new Label();
        maxLabel.Text = "Max Users:";
        innerVbox.AddChild(maxLabel);

        _hostMaxUsersInput = new SpinBox();
        _hostMaxUsersInput.MinValue = 1;
        _hostMaxUsersInput.MaxValue = 64;
        _hostMaxUsersInput.Value = 16;
        innerVbox.AddChild(_hostMaxUsersInput);

        // Template
        var templateLabel = new Label();
        templateLabel.Text = "World Template:";
        innerVbox.AddChild(templateLabel);

        _hostTemplateInput = new OptionButton();
        _hostTemplateInput.AddItem("Grid Space", 0);
        _hostTemplateInput.AddItem("Social Space", 1);
        _hostTemplateInput.AddItem("Empty", 2);
        _hostTemplateInput.Selected = 0; // Default to Grid
        innerVbox.AddChild(_hostTemplateInput);

        // Visibility
        var visLabel = new Label();
        visLabel.Text = "Visibility:";
        innerVbox.AddChild(visLabel);

        _hostVisibilityInput = new OptionButton();
        _hostVisibilityInput.AddItem("Private", 0);
        _hostVisibilityInput.AddItem("LAN Only", 1);
        _hostVisibilityInput.AddItem("Public", 2);
        _hostVisibilityInput.Selected = 1; // Default to LAN
        innerVbox.AddChild(_hostVisibilityInput);

        // Buttons
        var buttonBox = new HBoxContainer();
        buttonBox.Alignment = BoxContainer.AlignmentMode.Center;
        buttonBox.AddThemeConstantOverride("separation", 20);
        innerVbox.AddChild(buttonBox);

        _hostCancelButton = new Button();
        _hostCancelButton.Text = "Cancel";
        _hostCancelButton.CustomMinimumSize = new Vector2(100, 35);
        buttonBox.AddChild(_hostCancelButton);

        _hostConfirmButton = new Button();
        _hostConfirmButton.Text = "Host";
        _hostConfirmButton.CustomMinimumSize = new Vector2(100, 35);
        buttonBox.AddChild(_hostConfirmButton);

        // Connect signals
        _hostConfirmButton.Connect("pressed", Callable.From(OnHostConfirm));
        _hostCancelButton.Connect("pressed", Callable.From(OnHostCancel));
        overlay.GuiInput += OnHostOverlayInput;
    }

    private void OnHostButtonPressed()
    {
        ShowHostDialog();
    }

    private void ShowHostDialog()
    {
        if (_hostDialog != null)
        {
            _hostDialog.Visible = true;
            _hostNameInput?.GrabFocus();
        }
    }

    private void HideHostDialog()
    {
        if (_hostDialog != null)
        {
            _hostDialog.Visible = false;
        }
    }

    private void OnHostOverlayInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseEvent && mouseEvent.Pressed)
        {
            HideHostDialog();
        }
    }

    private void OnHostConfirm()
    {
        var sessionName = _hostNameInput?.Text ?? "My World";
        var maxUsers = (int)(_hostMaxUsersInput?.Value ?? 16);
        var templateIndex = _hostTemplateInput?.Selected ?? 0;
        var visibilityIndex = _hostVisibilityInput?.Selected ?? 1;

        HideHostDialog();
        HostSession(sessionName, maxUsers, templateIndex, visibilityIndex);
    }

    private void OnHostCancel()
    {
        HideHostDialog();
    }

    private void HostSession(string name, int maxUsers, int templateIndex, int visibilityIndex)
    {
        // Map template index to template name
        var templateName = templateIndex switch
        {
            0 => "Grid",
            1 => "SocialSpace",
            2 => "Empty",
            _ => "Grid"
        };

        AquaLogger.Log($"WorldBrowser: Hosting session '{name}' (template: {templateName}, max {maxUsers} users, visibility {visibilityIndex})");

        try
        {
            var worldManager = Lumora.Core.Engine.Current?.WorldManager;
            if (worldManager == null)
            {
                AquaLogger.Error("WorldBrowser: Cannot host - WorldManager not available");
                return;
            }

            // Get current focused world (local home) to unfocus later
            var previousWorld = worldManager.FocusedWorld;

            // Start session on default port
            ushort port = 7777;
            var world = worldManager.StartSession(name, port, null, templateName);

            if (world != null)
            {
                // Set visibility
                var visibility = visibilityIndex switch
                {
                    0 => Lumora.Core.Networking.Session.SessionVisibility.Private,
                    1 => Lumora.Core.Networking.Session.SessionVisibility.LAN,
                    2 => Lumora.Core.Networking.Session.SessionVisibility.Public,
                    _ => Lumora.Core.Networking.Session.SessionVisibility.LAN
                };

                world.Session?.SetVisibility(visibility);

                // Focus the new world
                worldManager.FocusWorld(world);
                SetCurrentWorld(name);

                // Start thumbnail capture service for this session
                StartThumbnailService();

                AquaLogger.Log($"WorldBrowser: Successfully hosting '{name}' on port {port}");
            }
            else
            {
                AquaLogger.Error("WorldBrowser: Failed to create hosted session");
            }
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"WorldBrowser: Host failed - {ex.Message}");
        }
    }

    private void StartThumbnailService()
    {
        // Create or find existing thumbnail service
        if (_thumbnailService == null)
        {
            _thumbnailService = GetNodeOrNull<SessionThumbnailService>("/root/SessionThumbnailService");
            if (_thumbnailService == null)
            {
                _thumbnailService = new SessionThumbnailService();
                _thumbnailService.Name = "SessionThumbnailService";
                GetTree().Root.AddChild(_thumbnailService);
            }
        }

        // Force an immediate capture
        _thumbnailService.CaptureNow();
        AquaLogger.Log("WorldBrowser: Thumbnail service started");
    }

    #endregion

    #region World Detail Panel

    private void CreateWorldDetailPanel()
    {
        // Create fullscreen panel overlay
        _worldDetailPanel = new Control();
        _worldDetailPanel.Name = "WorldDetailPanel";
        _worldDetailPanel.Visible = false;
        _worldDetailPanel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_worldDetailPanel);

        // Background overlay (darker)
        var overlay = new ColorRect();
        overlay.Color = new Color(0, 0, 0, 0.85f);
        overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _worldDetailPanel.AddChild(overlay);

        // Main panel container (centered, large)
        var panelContainer = new PanelContainer();
        panelContainer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        panelContainer.OffsetLeft = 40;
        panelContainer.OffsetRight = -40;
        panelContainer.OffsetTop = 40;
        panelContainer.OffsetBottom = -40;
        _worldDetailPanel.AddChild(panelContainer);

        // Main layout
        var mainMargin = new MarginContainer();
        mainMargin.AddThemeConstantOverride("margin_left", 30);
        mainMargin.AddThemeConstantOverride("margin_right", 30);
        mainMargin.AddThemeConstantOverride("margin_top", 30);
        mainMargin.AddThemeConstantOverride("margin_bottom", 30);
        panelContainer.AddChild(mainMargin);

        var mainVBox = new VBoxContainer();
        mainVBox.AddThemeConstantOverride("separation", 20);
        mainMargin.AddChild(mainVBox);

        // Header with title and close button
        var headerHBox = new HBoxContainer();
        mainVBox.AddChild(headerHBox);

        _detailTitle = new Label();
        _detailTitle.Text = "World Name";
        _detailTitle.AddThemeFontSizeOverride("font_size", 28);
        _detailTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        headerHBox.AddChild(_detailTitle);

        var closeX = new Button();
        closeX.Text = "X";
        closeX.CustomMinimumSize = new Vector2(40, 40);
        closeX.Connect("pressed", Callable.From(HideWorldDetailPanel));
        headerHBox.AddChild(closeX);

        // Content area (horizontal split)
        var contentHBox = new HBoxContainer();
        contentHBox.SizeFlagsVertical = SizeFlags.ExpandFill;
        contentHBox.AddThemeConstantOverride("separation", 30);
        mainVBox.AddChild(contentHBox);

        // Left side - Thumbnail
        var thumbnailContainer = new VBoxContainer();
        thumbnailContainer.CustomMinimumSize = new Vector2(400, 0);
        contentHBox.AddChild(thumbnailContainer);

        var thumbnailPanel = new PanelContainer();
        thumbnailPanel.CustomMinimumSize = new Vector2(400, 300);
        thumbnailContainer.AddChild(thumbnailPanel);

        _detailThumbnail = new TextureRect();
        _detailThumbnail.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        _detailThumbnail.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
        thumbnailPanel.AddChild(_detailThumbnail);

        var noImageLabel = new Label();
        noImageLabel.Text = "No Preview Available";
        noImageLabel.HorizontalAlignment = HorizontalAlignment.Center;
        noImageLabel.VerticalAlignment = VerticalAlignment.Center;
        noImageLabel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        thumbnailPanel.AddChild(noImageLabel);

        // Right side - Details
        var detailsVBox = new VBoxContainer();
        detailsVBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        detailsVBox.AddThemeConstantOverride("separation", 15);
        contentHBox.AddChild(detailsVBox);

        // Host info
        var hostHBox = new HBoxContainer();
        detailsVBox.AddChild(hostHBox);
        var hostIcon = new Label();
        hostIcon.Text = "Host:";
        hostIcon.CustomMinimumSize = new Vector2(100, 0);
        hostHBox.AddChild(hostIcon);
        _detailHost = new Label();
        _detailHost.Text = "Unknown";
        _detailHost.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        hostHBox.AddChild(_detailHost);

        // User count
        var usersHBox = new HBoxContainer();
        detailsVBox.AddChild(usersHBox);
        var usersIcon = new Label();
        usersIcon.Text = "Users:";
        usersIcon.CustomMinimumSize = new Vector2(100, 0);
        usersHBox.AddChild(usersIcon);
        _detailUserCount = new Label();
        _detailUserCount.Text = "0 / 16";
        _detailUserCount.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        usersHBox.AddChild(_detailUserCount);

        // Category
        var categoryHBox = new HBoxContainer();
        detailsVBox.AddChild(categoryHBox);
        var categoryIcon = new Label();
        categoryIcon.Text = "Category:";
        categoryIcon.CustomMinimumSize = new Vector2(100, 0);
        categoryHBox.AddChild(categoryIcon);
        _detailCategory = new Label();
        _detailCategory.Text = "General";
        _detailCategory.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        categoryHBox.AddChild(_detailCategory);

        // Description
        var descLabel = new Label();
        descLabel.Text = "Description:";
        detailsVBox.AddChild(descLabel);

        var descScroll = new ScrollContainer();
        descScroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        descScroll.CustomMinimumSize = new Vector2(0, 100);
        detailsVBox.AddChild(descScroll);

        _detailDescription = new Label();
        _detailDescription.Text = "No description available.";
        _detailDescription.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _detailDescription.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        descScroll.AddChild(_detailDescription);

        // Spacer
        var spacer = new Control();
        spacer.SizeFlagsVertical = SizeFlags.ExpandFill;
        detailsVBox.AddChild(spacer);

        // Action buttons
        var buttonHBox = new HBoxContainer();
        buttonHBox.Alignment = BoxContainer.AlignmentMode.End;
        buttonHBox.AddThemeConstantOverride("separation", 20);
        mainVBox.AddChild(buttonHBox);

        _detailCloseButton = new Button();
        _detailCloseButton.Text = "Close";
        _detailCloseButton.CustomMinimumSize = new Vector2(150, 50);
        _detailCloseButton.Connect("pressed", Callable.From(HideWorldDetailPanel));
        buttonHBox.AddChild(_detailCloseButton);

        _detailJoinButton = new Button();
        _detailJoinButton.Text = "Join Session";
        _detailJoinButton.CustomMinimumSize = new Vector2(150, 50);
        _detailJoinButton.Connect("pressed", Callable.From(OnDetailJoinPressed));
        buttonHBox.AddChild(_detailJoinButton);

        // Click overlay to close
        overlay.GuiInput += OnDetailOverlayInput;
    }

    private void ShowWorldDetailPanel(WorldInfo world)
    {
        if (_worldDetailPanel == null || world == null)
            return;

        _selectedWorldInfo = world;

        // Update UI with world info
        if (_detailTitle != null)
            _detailTitle.Text = world.Name;

        if (_detailHost != null)
            _detailHost.Text = world.Host;

        if (_detailUserCount != null)
            _detailUserCount.Text = $"{world.UserCount} / {world.MaxUsers}";

        if (_detailCategory != null)
        {
            // Find category display name
            var categoryName = world.Category;
            foreach (var cat in DefaultCategories)
            {
                if (cat.Id == world.Category)
                {
                    categoryName = cat.Name;
                    break;
                }
            }
            _detailCategory.Text = categoryName;
        }

        if (_detailDescription != null)
            _detailDescription.Text = "No description available.";

        if (_detailThumbnail != null)
            _detailThumbnail.Texture = world.Thumbnail;

        // Check if we're already in this world or hosting it
        var currentWorld = Lumora.Core.Engine.Current?.WorldManager?.FocusedWorld;
        bool isInWorld = currentWorld?.Session?.Metadata?.SessionId == world.Id;

        if (_detailJoinButton != null)
        {
            _detailJoinButton.Text = isInWorld ? "Already Joined" : "Join Session";
            _detailJoinButton.Disabled = isInWorld;
        }

        _worldDetailPanel.Visible = true;
    }

    private void HideWorldDetailPanel()
    {
        if (_worldDetailPanel != null)
        {
            _worldDetailPanel.Visible = false;
            _selectedWorldInfo = null;
        }
    }

    private void OnDetailOverlayInput(InputEvent @event)
    {
        // Don't close on overlay click - user must click Close or X button
        // This prevents accidental closes
    }

    private void OnDetailJoinPressed()
    {
        if (_selectedWorldInfo == null)
            return;

        // Save before hiding (HideWorldDetailPanel clears _selectedWorldInfo)
        var worldToJoin = _selectedWorldInfo;
        HideWorldDetailPanel();
        WorldSelected?.Invoke(worldToJoin);
    }

    #endregion

    private void CreateCategoryButtons()
    {
        if (_categoriesList == null || _categoryButtonScene == null) return;

        foreach (var btn in _categoryButtons)
            btn.QueueFree();
        _categoryButtons.Clear();

        foreach (var category in DefaultCategories)
        {
            var button = _categoryButtonScene.Instantiate<Button>();
            button.Text = $"  {category.Icon}  {category.Name}";
            _categoriesList.AddChild(button);

            var capturedId = category.Id;
            button.Connect("pressed", Callable.From(() => OnCategorySelected(capturedId)));
            _categoryButtons.Add(button);
        }

        // Select first category
        if (_categoryButtons.Count > 0)
            UpdateCategorySelection(_selectedCategory);
    }

    private void OnCategorySelected(string categoryId)
    {
        _selectedCategory = categoryId;
        UpdateCategorySelection(categoryId);
        CategoryChanged?.Invoke(categoryId);
        RefreshWorldList();
    }

    private void UpdateCategorySelection(string categoryId)
    {
        for (int i = 0; i < DefaultCategories.Length && i < _categoryButtons.Count; i++)
        {
            var btn = _categoryButtons[i];
            var isSelected = DefaultCategories[i].Id == categoryId;
            // Visual feedback for selection
            btn.Modulate = isSelected ? new Color(1, 1, 1, 1) : new Color(0.7f, 0.7f, 0.7f, 1);
        }
    }

    private void OnSearchTextChanged(string newText)
    {
        _searchQuery = newText.ToLower().Trim();
        RefreshWorldList();
    }

    private void OnRefreshPressed()
    {
        AquaLogger.Log("WorldBrowser: Refresh pressed");

        // Restart scanning to refresh sessions
        if (_browserService != null)
        {
            _browserService.StopScanning();
            _browserService.StartScanning();
        }

        RefreshWorldList();
    }

    private void RefreshWorldList()
    {
        if (_worldsGrid == null || _worldCardScene == null) return;

        // Clear existing cards
        foreach (var card in _worldCards)
            card.QueueFree();
        _worldCards.Clear();

        // Filter worlds
        var filteredWorlds = new List<WorldInfo>();
        foreach (var world in _worldsCache.Values)
        {
            // Category filter
            if (_selectedCategory != "featured" && _selectedCategory != "active" && _selectedCategory != "recent")
            {
                if (world.Category != _selectedCategory)
                    continue;
            }

            // Search filter
            if (!string.IsNullOrEmpty(_searchQuery))
            {
                if (!world.Name.ToLower().Contains(_searchQuery) &&
                    !world.Host.ToLower().Contains(_searchQuery))
                    continue;
            }

            filteredWorlds.Add(world);
        }

        // Sort by user count for active/featured
        if (_selectedCategory == "featured" || _selectedCategory == "active")
            filteredWorlds.Sort((a, b) => b.UserCount.CompareTo(a.UserCount));

        // Update result count
        if (_resultCountLabel != null)
            _resultCountLabel.Text = $"0 - {filteredWorlds.Count}";

        // Show/hide labels
        if (_noResultsLabel != null)
            _noResultsLabel.Visible = filteredWorlds.Count == 0;
        if (_worldsScroll != null)
            _worldsScroll.Visible = filteredWorlds.Count > 0;

        // Create world cards
        foreach (var world in filteredWorlds)
        {
            CreateWorldCard(world);
        }
    }

    private void CreateWorldCard(WorldInfo world)
    {
        if (_worldsGrid == null || _worldCardScene == null) return;

        var card = _worldCardScene.Instantiate<Button>();
        _worldsGrid.AddChild(card);

        // Set card data
        var titleLabel = card.GetNodeOrNull<Label>("VBox/InfoPanel/VBox/Title");
        var hostLabel = card.GetNodeOrNull<Label>("VBox/InfoPanel/VBox/Host");
        var countLabel = card.GetNodeOrNull<Label>("VBox/ThumbnailContainer/UserCountBadge/HBox/Count");
        var thumbnailRect = card.GetNodeOrNull<TextureRect>("VBox/ThumbnailContainer/Thumbnail");
        var noImageLabel = card.GetNodeOrNull<Label>("VBox/ThumbnailContainer/NoImageLabel");

        if (titleLabel != null) titleLabel.Text = world.Name;
        if (hostLabel != null) hostLabel.Text = $"Host: {world.Host}";
        if (countLabel != null) countLabel.Text = world.UserCount.ToString();

        // Show/hide thumbnail
        if (thumbnailRect != null && noImageLabel != null)
        {
            if (world.Thumbnail != null)
            {
                thumbnailRect.Texture = world.Thumbnail;
                noImageLabel.Visible = false;
            }
            else
            {
                noImageLabel.Visible = true;
            }
        }

        // Connect click
        var capturedWorld = world;
        card.Connect("pressed", Callable.From(() => OnWorldCardPressed(capturedWorld)));

        _worldCards.Add(card);
    }

    private void OnWorldCardPressed(WorldInfo world)
    {
        GD.Print($"WorldBrowser: Selected world '{world.Name}' hosted by {world.Host}");
        ShowWorldDetailPanel(world);
    }

    public void SetCurrentWorld(string worldName)
    {
        if (_currentWorldLabel != null)
            _currentWorldLabel.Text = worldName;
    }

    public void SetLoading(bool loading)
    {
        if (_loadingLabel != null)
            _loadingLabel.Visible = loading;
        if (_worldsScroll != null)
            _worldsScroll.Visible = !loading;
    }

    /// <summary>
    /// Add or update a world in the browser.
    /// </summary>
    public void UpdateWorld(WorldInfo world)
    {
        _worldsCache[world.Id] = world;
        RefreshWorldList();
    }

    /// <summary>
    /// Clear all worlds.
    /// </summary>
    public void ClearWorlds()
    {
        _worldsCache.Clear();
        RefreshWorldList();
    }
}
