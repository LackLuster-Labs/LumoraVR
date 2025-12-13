using Godot;
using System;
using System.Collections.Generic;

namespace Lumora.Godot.UI;

/// <summary>
/// World browser UI for discovering and joining worlds.
/// </summary>
public partial class WorldBrowser : Control
{
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
    private VBoxContainer? _categoriesList;
    private GridContainer? _worldsGrid;
    private ScrollContainer? _worldsScroll;
    private Label? _loadingLabel;
    private Label? _noResultsLabel;

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

        // Build UI
        CreateCategoryButtons();
        LoadDemoWorlds();

        GD.Print("WorldBrowser: Initialized");
    }

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
        GD.Print("WorldBrowser: Refresh pressed");
        RefreshWorldList();
    }

    private void LoadDemoWorlds()
    {
        // Demo data - replace with actual API calls
        var demoWorlds = new[]
        {
            new WorldInfo { Id = "1", Name = "Social Hub", Host = "LumoraTeam", UserCount = 24, Category = "featured" },
            new WorldInfo { Id = "2", Name = "Art Gallery", Host = "ArtistPro", UserCount = 8, Category = "art" },
            new WorldInfo { Id = "3", Name = "Mini Games Arena", Host = "GameMaster", UserCount = 16, Category = "games" },
            new WorldInfo { Id = "4", Name = "Chill Lounge", Host = "ChillVibes", UserCount = 12, Category = "social" },
            new WorldInfo { Id = "5", Name = "Tutorial World", Host = "Helper", UserCount = 4, Category = "education" },
            new WorldInfo { Id = "6", Name = "VR Chess", Host = "ChessPro", UserCount = 2, Category = "games" },
            new WorldInfo { Id = "7", Name = "Music Studio", Host = "DJ_Mix", UserCount = 6, Category = "art" },
            new WorldInfo { Id = "8", Name = "Avatar Testing", Host = "TestUser", UserCount = 1, Category = "my_worlds" },
            new WorldInfo { Id = "9", Name = "Dance Club", Host = "PartyHost", UserCount = 32, Category = "featured" },
            new WorldInfo { Id = "10", Name = "Meditation Space", Host = "ZenMaster", UserCount = 5, Category = "social" },
            new WorldInfo { Id = "11", Name = "Racing Track", Host = "SpeedRacer", UserCount = 8, Category = "games" },
            new WorldInfo { Id = "12", Name = "Science Lab", Host = "Professor", UserCount = 3, Category = "education" },
        };

        _worldsCache.Clear();
        foreach (var world in demoWorlds)
            _worldsCache[world.Id] = world;

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
        WorldSelected?.Invoke(world);
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
