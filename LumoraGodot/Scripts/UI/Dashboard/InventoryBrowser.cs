using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lumora.CDN;

namespace Lumora.Godot.UI;

/// <summary>
/// Inventory browser UI with top-toolbar controls and full-width content panel.
/// </summary>
public partial class InventoryBrowser : Control
{
    private const string AssetCardScenePath = "res://Scenes/UI/Components/AssetCard.tscn";
    private const float CardMinWidth = 180f;
    private const float GridSpacing = 12f;
    private const int MaxStatusLength = 52;

    private LumoraClient? _client;
    private UserProfile? _currentUser;
    private bool _isReady;

    private LineEdit? _searchBox;
    private Label? _resultCountLabel;
    private Label? _statusLabel;
    private Label? _quotaLabel;

    private Button? _refreshButton;
    private Button? _createFolderButton;
    private Button? _inventoriesButton;
    private Button? _shareButton;
    private Button? _unshareButton;
    private Button? _getUrlButton;
    private Button? _deleteButton;
    private Button? _saveAvatarButton;

    private OptionButton? _typeFilter;
    private OptionButton? _folderSelector;

    private GridContainer? _assetGrid;
    private ScrollContainer? _assetsScroll;
    private Label? _loadingLabel;
    private Label? _noResultsLabel;

    private ConfirmationDialog? _createFolderDialog;
    private LineEdit? _createFolderNameInput;

    private PackedScene? _assetCardScene;

    private readonly Dictionary<string, Texture2D> _thumbnailCache = new();
    private readonly Dictionary<int, string?> _folderOptionToId = new();
    private readonly List<AssetRef> _allAssets = new();
    private List<UserFolder> _folders = new();

    private string? _selectedFolderId;
    private string _searchQuery = string.Empty;
    private AssetRef? _selectedAsset;

    public override void _Ready()
    {
        _searchBox = GetNodeOrNull<LineEdit>("RootMargin/RootVBox/ToolbarPanel/ToolbarMargin/ToolbarVBox/FilterRow/SearchBox");
        _resultCountLabel = GetNodeOrNull<Label>("RootMargin/RootVBox/ToolbarPanel/ToolbarMargin/ToolbarVBox/FilterRow/ResultCount");
        _statusLabel = GetNodeOrNull<Label>("RootMargin/RootVBox/ToolbarPanel/ToolbarMargin/ToolbarVBox/FilterRow/Status");
        _quotaLabel = GetNodeOrNull<Label>("RootMargin/RootVBox/ToolbarPanel/ToolbarMargin/ToolbarVBox/ActionRow/QuotaLabel");

        _refreshButton = GetNodeOrNull<Button>("RootMargin/RootVBox/ToolbarPanel/ToolbarMargin/ToolbarVBox/FilterRow/RefreshButton");
        _createFolderButton = GetNodeOrNull<Button>("RootMargin/RootVBox/ToolbarPanel/ToolbarMargin/ToolbarVBox/FilterRow/CreateFolderButton");
        _inventoriesButton = GetNodeOrNull<Button>("RootMargin/RootVBox/ToolbarPanel/ToolbarMargin/ToolbarVBox/ActionRow/InventoriesButton");
        _shareButton = GetNodeOrNull<Button>("RootMargin/RootVBox/ToolbarPanel/ToolbarMargin/ToolbarVBox/ActionRow/ShareButton");
        _unshareButton = GetNodeOrNull<Button>("RootMargin/RootVBox/ToolbarPanel/ToolbarMargin/ToolbarVBox/ActionRow/UnshareButton");
        _getUrlButton = GetNodeOrNull<Button>("RootMargin/RootVBox/ToolbarPanel/ToolbarMargin/ToolbarVBox/ActionRow/GetUrlButton");
        _deleteButton = GetNodeOrNull<Button>("RootMargin/RootVBox/ToolbarPanel/ToolbarMargin/ToolbarVBox/ActionRow/DeleteButton");
        _saveAvatarButton = GetNodeOrNull<Button>("RootMargin/RootVBox/ToolbarPanel/ToolbarMargin/ToolbarVBox/ActionRow/SaveAvatarButton");

        _typeFilter = GetNodeOrNull<OptionButton>("RootMargin/RootVBox/ToolbarPanel/ToolbarMargin/ToolbarVBox/FilterRow/TypeFilter");
        _folderSelector = GetNodeOrNull<OptionButton>("RootMargin/RootVBox/ToolbarPanel/ToolbarMargin/ToolbarVBox/FilterRow/FolderSelector");

        _assetGrid = GetNodeOrNull<GridContainer>("RootMargin/RootVBox/ContentPanel/ContentMargin/AssetsScroll/AssetGrid");
        _assetsScroll = GetNodeOrNull<ScrollContainer>("RootMargin/RootVBox/ContentPanel/ContentMargin/AssetsScroll");
        _loadingLabel = GetNodeOrNull<Label>("RootMargin/RootVBox/ContentPanel/LoadingLabel");
        _noResultsLabel = GetNodeOrNull<Label>("RootMargin/RootVBox/ContentPanel/NoResultsLabel");

        _createFolderDialog = GetNodeOrNull<ConfirmationDialog>("CreateFolderDialog");
        _createFolderNameInput = GetNodeOrNull<LineEdit>("CreateFolderDialog/Margin/VBox/FolderNameInput");

        _assetCardScene = GD.Load<PackedScene>(AssetCardScenePath);
        if (_assetCardScene == null)
        {
            GD.PrintErr($"InventoryBrowser: Failed to load {AssetCardScenePath}");
        }

        SetupTypeFilter();
        BuildFolderSelector();
        ConnectSignals();
        UpdateInteractionState(false);
        UpdateActionButtons();
        UpdateGridColumns();
        CallDeferred(nameof(UpdateGridColumns));

        _isReady = true;

        if (_client != null)
        {
            _ = RefreshInventory();
        }
    }

    public void SetClient(LumoraClient client)
    {
        _client = client;
        if (_isReady)
        {
            _ = RefreshInventory();
        }
    }

    public void SetCurrentUser(UserProfile? profile)
    {
        _currentUser = profile;
        if (_isReady)
        {
            _ = RefreshInventory();
        }
    }

    private void SetupTypeFilter()
    {
        if (_typeFilter == null)
            return;

        _typeFilter.Clear();
        _typeFilter.AddItem("All Types", 0);
        _typeFilter.AddItem("Avatars", 1);
        _typeFilter.AddItem("Worlds", 2);
        _typeFilter.AddItem("Props", 3);
        _typeFilter.Selected = 0;
    }

    private void ConnectSignals()
    {
        if (_searchBox != null)
            _searchBox.TextChanged += OnSearchTextChanged;

        if (_typeFilter != null)
            _typeFilter.ItemSelected += OnTypeFilterChanged;

        if (_folderSelector != null)
            _folderSelector.ItemSelected += OnFolderSelected;

        if (_refreshButton != null)
            _refreshButton.Pressed += OnRefreshPressed;

        if (_createFolderButton != null)
            _createFolderButton.Pressed += OnCreateFolderPressed;

        if (_inventoriesButton != null)
            _inventoriesButton.Pressed += OnInventoriesPressed;

        if (_shareButton != null)
            _shareButton.Pressed += OnSharePressed;

        if (_unshareButton != null)
            _unshareButton.Pressed += OnUnsharePressed;

        if (_getUrlButton != null)
            _getUrlButton.Pressed += OnGetUrlPressed;

        if (_deleteButton != null)
            _deleteButton.Pressed += OnDeletePressed;

        if (_saveAvatarButton != null)
            _saveAvatarButton.Pressed += OnSaveAvatarPressed;

        if (_createFolderDialog != null)
            _createFolderDialog.Confirmed += OnCreateFolderConfirmed;

        if (_assetsScroll != null)
            _assetsScroll.Resized += UpdateGridColumns;
    }

    private void OnSearchTextChanged(string text)
    {
        _searchQuery = text?.Trim() ?? string.Empty;
        ApplyFilters();
    }

    private void OnTypeFilterChanged(long _index)
    {
        ApplyFilters();
    }

    private void OnFolderSelected(long index)
    {
        if (_folderOptionToId.TryGetValue((int)index, out var folderId))
            _selectedFolderId = folderId;
        else
            _selectedFolderId = null;

        ApplyFilters();
    }

    private void OnInventoriesPressed()
    {
        _selectedFolderId = null;
        if (_folderSelector != null && _folderSelector.ItemCount > 0)
            _folderSelector.Select(0);

        ApplyFilters();
    }

    private void OnRefreshPressed()
    {
        _ = RefreshInventory();
    }

    private void OnCreateFolderPressed()
    {
        if (_createFolderDialog == null || _createFolderNameInput == null)
            return;

        _createFolderNameInput.Text = string.Empty;
        _createFolderDialog.PopupCentered(new Vector2I(380, 160));
        _createFolderNameInput.GrabFocus();
    }

    private async void OnCreateFolderConfirmed()
    {
        if (_client == null)
        {
            SetStatus("Client not initialized");
            return;
        }

        var name = _createFolderNameInput?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            SetStatus("Folder name cannot be empty");
            return;
        }

        SetStatus($"Creating folder '{name}'...");
        var result = await _client.CreateFolder(name, _selectedFolderId);
        if (!result.Success)
        {
            SetStatus(result.Status == System.Net.HttpStatusCode.Unauthorized
                ? "Not authenticated"
                : $"Create folder failed: {result.Message}", true);
            return;
        }

        await RefreshInventory();
    }

    private void OnSharePressed()
    {
        if (_selectedAsset == null)
        {
            SetStatus("Select an asset first");
            return;
        }

        SetStatus("Share controls will be wired to sharing API next");
    }

    private void OnUnsharePressed()
    {
        if (_selectedAsset == null)
        {
            SetStatus("Select an asset first");
            return;
        }

        SetStatus("Unshare controls will be wired to sharing API next");
    }

    private void OnGetUrlPressed()
    {
        if (_selectedAsset == null)
        {
            SetStatus("Select an asset first");
            return;
        }

        var uri = $"lumora:///{_selectedAsset.Hash}";
        DisplayServer.ClipboardSet(uri);
        SetStatus("Asset URL copied to clipboard");
    }

    private async void OnDeletePressed()
    {
        if (_client == null || _selectedAsset == null)
        {
            SetStatus("Select an asset first");
            return;
        }

        SetStatus($"Deleting '{_selectedAsset.Name}'...");
        var result = await _client.RemoveAsset(_selectedAsset.AssetId);
        if (!result.Success)
        {
            SetStatus(result.Status == System.Net.HttpStatusCode.Unauthorized
                ? "Not authenticated"
                : $"Delete failed: {result.Message}", true);
            return;
        }

        await RefreshInventory();
    }

    private void OnSaveAvatarPressed()
    {
        if (_selectedAsset == null)
        {
            SetStatus("Select an avatar first");
            return;
        }

        if (_selectedAsset.Type != AssetType.Avatar)
        {
            SetStatus("Selected asset is not an avatar");
            return;
        }

        SetStatus("Save Avatar flow will be wired to avatar loader next");
    }

    private async Task RefreshInventory()
    {
        if (_client == null)
        {
            SetStatus("Inventory client unavailable");
            return;
        }

        SetLoading(true, "Loading inventory...");

        var inventoryResult = await _client.GetInventory();
        if (!inventoryResult.Success || inventoryResult.Data == null)
        {
            SetLoading(false, string.Empty);
            _folders = new List<UserFolder>();
            _allAssets.Clear();
            _selectedAsset = null;

            if (inventoryResult.Status == System.Net.HttpStatusCode.Unauthorized)
            {
                SetStatus("Log in to view your inventory");
                UpdateInteractionState(false);
            }
            else
            {
                SetStatus($"Failed to load inventory: {inventoryResult.Message}", true);
                UpdateInteractionState(false);
            }

            BuildFolderSelector();
            RebuildAssetCards(Array.Empty<AssetRef>());
            UpdateActionButtons();
            return;
        }

        _folders = inventoryResult.Data.Folders ?? new List<UserFolder>();
        _allAssets.Clear();
        _allAssets.AddRange(CollectAllAssets(_folders));
        _selectedAsset = null;

        UpdateInteractionState(true);
        BuildFolderSelector();
        ApplyFilters();

        await RefreshQuota();

        SetLoading(false, string.Empty);
        SetStatus($"Loaded {_allAssets.Count} assets");
        UpdateActionButtons();
    }

    private async Task RefreshQuota()
    {
        if (_client == null || _quotaLabel == null)
            return;

        if (_currentUser?.StorageQuota != null)
        {
            float usedGB = _currentUser.StorageQuota.UsedMB / 1024f;
            float totalGB = _currentUser.StorageQuota.QuotaMB / 1024f;
            _quotaLabel.Text = $"Quota: {usedGB:F1} / {totalGB:F0} GB";
            return;
        }

        var quotaResult = await _client.GetQuota();
        if (quotaResult.Success && quotaResult.Data != null)
        {
            float usedGB = quotaResult.Data.UsedMB / 1024f;
            float totalGB = quotaResult.Data.QuotaMB / 1024f;
            _quotaLabel.Text = $"Quota: {usedGB:F1} / {totalGB:F0} GB";
        }
        else
        {
            _quotaLabel.Text = "Quota: unavailable";
        }
    }

    private void BuildFolderSelector()
    {
        if (_folderSelector == null)
            return;

        string? previousSelection = _selectedFolderId;

        _folderSelector.Clear();
        _folderOptionToId.Clear();

        _folderSelector.AddItem("All Folders", 0);
        _folderOptionToId[0] = null;

        foreach (var folder in _folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            AddFolderOptionRecursive(folder, 0);

        int selectedIndex = 0;
        if (!string.IsNullOrEmpty(previousSelection))
        {
            foreach (var pair in _folderOptionToId)
            {
                if (pair.Value == previousSelection)
                {
                    selectedIndex = pair.Key;
                    break;
                }
            }
        }

        if (selectedIndex < _folderSelector.ItemCount)
            _folderSelector.Select(selectedIndex);

        _selectedFolderId = _folderOptionToId.TryGetValue(selectedIndex, out var selectedId)
            ? selectedId
            : null;
    }

    private void AddFolderOptionRecursive(UserFolder folder, int depth)
    {
        if (_folderSelector == null)
            return;

        string indent = depth <= 0 ? string.Empty : new string(' ', depth * 2);
        string label = depth <= 0 ? folder.Name : $"{indent}- {folder.Name}";

        int index = _folderSelector.ItemCount;
        _folderSelector.AddItem(label, index);
        _folderOptionToId[index] = folder.Id;

        if (folder.Subfolders == null)
            return;

        foreach (var subfolder in folder.Subfolders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            AddFolderOptionRecursive(subfolder, depth + 1);
    }

    private IEnumerable<AssetRef> CollectAllAssets(IEnumerable<UserFolder> folders)
    {
        var unique = new Dictionary<string, AssetRef>(StringComparer.Ordinal);
        foreach (var folder in folders)
            CollectAssetsRecursive(folder, unique);

        return unique.Values;
    }

    private void CollectAssetsRecursive(UserFolder folder, IDictionary<string, AssetRef> assets)
    {
        if (folder.Assets != null)
        {
            foreach (var asset in folder.Assets)
            {
                if (!string.IsNullOrEmpty(asset.AssetId))
                    assets[asset.AssetId] = asset;
            }
        }

        if (folder.Subfolders == null)
            return;

        foreach (var subfolder in folder.Subfolders)
            CollectAssetsRecursive(subfolder, assets);
    }

    private void ApplyFilters()
    {
        IEnumerable<AssetRef> source = _allAssets;

        if (!string.IsNullOrEmpty(_selectedFolderId))
        {
            var selectedFolder = FindFolderById(_folders, _selectedFolderId);
            source = selectedFolder == null
                ? Enumerable.Empty<AssetRef>()
                : CollectFolderAssets(selectedFolder);
        }

        if (_typeFilter != null)
        {
            source = _typeFilter.Selected switch
            {
                1 => source.Where(a => a.Type == AssetType.Avatar),
                2 => source.Where(a => a.Type == AssetType.World),
                3 => source.Where(a => a.Type == AssetType.Prop),
                _ => source
            };
        }

        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            var needle = _searchQuery;
            source = source.Where(a =>
                (!string.IsNullOrEmpty(a.Name) && a.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)) ||
                (a.Tags != null && a.Tags.Any(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase))));
        }

        var filtered = source.OrderByDescending(a => a.AddedAt).ToList();

        RebuildAssetCards(filtered);

        if (_resultCountLabel != null)
            _resultCountLabel.Text = $"{filtered.Count} assets";
    }

    private UserFolder? FindFolderById(IEnumerable<UserFolder> folders, string folderId)
    {
        foreach (var folder in folders)
        {
            if (folder.Id == folderId)
                return folder;

            var found = FindFolderById(folder.Subfolders ?? new List<UserFolder>(), folderId);
            if (found != null)
                return found;
        }

        return null;
    }

    private IEnumerable<AssetRef> CollectFolderAssets(UserFolder folder)
    {
        var output = new Dictionary<string, AssetRef>(StringComparer.Ordinal);
        CollectAssetsRecursive(folder, output);
        return output.Values;
    }

    private void RebuildAssetCards(IReadOnlyList<AssetRef> assets)
    {
        if (_assetGrid == null)
            return;

        foreach (Node child in _assetGrid.GetChildren())
            child.QueueFree();

        if (_noResultsLabel != null)
            _noResultsLabel.Visible = assets.Count == 0;

        if (_assetsScroll != null)
            _assetsScroll.Visible = assets.Count > 0;

        if (_assetCardScene == null)
            return;

        _selectedAsset = null;
        UpdateActionButtons();
        UpdateGridColumns();

        foreach (var asset in assets)
        {
            var card = _assetCardScene.Instantiate<Button>();
            if (card == null)
                continue;

            BindAssetCard(card, asset);
            _assetGrid.AddChild(card);
        }
    }

    private void BindAssetCard(Button card, AssetRef asset)
    {
        var titleLabel = card.GetNodeOrNull<Label>("VBox/InfoPanel/VBox/Title");
        var typeLabel = card.GetNodeOrNull<Label>("VBox/ThumbnailContainer/TypeBadge/TypeLabel");
        var sizeLabel = card.GetNodeOrNull<Label>("VBox/InfoPanel/VBox/SizeLabel");
        var thumbnailRect = card.GetNodeOrNull<TextureRect>("VBox/ThumbnailContainer/Thumbnail");
        var noImageLabel = card.GetNodeOrNull<Label>("VBox/ThumbnailContainer/NoImageLabel");

        if (titleLabel != null)
            titleLabel.Text = string.IsNullOrWhiteSpace(asset.Name) ? "Unnamed Asset" : asset.Name;

        if (typeLabel != null)
            typeLabel.Text = asset.Type.ToString();

        if (sizeLabel != null)
            sizeLabel.Text = FormatBytes(asset.SizeBytes);

        card.TooltipText = BuildAssetTooltip(asset);
        card.Pressed += () => OnAssetCardPressed(asset);

        if (thumbnailRect != null)
            thumbnailRect.Texture = null;

        if (!string.IsNullOrWhiteSpace(asset.ThumbnailHash))
            _ = LoadThumbnailIntoCard(asset.ThumbnailHash!, thumbnailRect, noImageLabel);
        else if (noImageLabel != null)
            noImageLabel.Visible = true;
    }

    private string BuildAssetTooltip(AssetRef asset)
    {
        string tags = asset.Tags == null || asset.Tags.Count == 0
            ? "none"
            : string.Join(", ", asset.Tags);

        return $"{asset.Name}\nType: {asset.Type}\nSize: {FormatBytes(asset.SizeBytes)}\nTags: {tags}";
    }

    private async Task LoadThumbnailIntoCard(string hash, TextureRect? thumbnailRect, Label? noImageLabel)
    {
        if (_client == null || thumbnailRect == null)
            return;

        if (_thumbnailCache.TryGetValue(hash, out var cachedTexture))
        {
            thumbnailRect.Texture = cachedTexture;
            if (noImageLabel != null)
                noImageLabel.Visible = false;
            return;
        }

        var contentResult = await _client.FetchContent(hash);
        if (!contentResult.Success || contentResult.Data == null)
            return;

        var texture = DecodeTexture(contentResult.Data);
        if (texture == null)
            return;

        _thumbnailCache[hash] = texture;

        if (!GodotObject.IsInstanceValid(thumbnailRect))
            return;

        thumbnailRect.Texture = texture;
        if (noImageLabel != null && GodotObject.IsInstanceValid(noImageLabel))
            noImageLabel.Visible = false;
    }

    private static Texture2D? DecodeTexture(byte[] data)
    {
        var image = new Image();

        var error = image.LoadPngFromBuffer(data);
        if (error != Error.Ok)
            error = image.LoadJpgFromBuffer(data);

        if (error != Error.Ok)
            return null;

        return ImageTexture.CreateFromImage(image);
    }

    private void OnAssetCardPressed(AssetRef asset)
    {
        _selectedAsset = asset;
        UpdateActionButtons();
        SetStatus($"Selected: {asset.Name} ({asset.Type})");
    }

    private void SetLoading(bool loading, string message)
    {
        if (_loadingLabel != null)
        {
            _loadingLabel.Visible = loading;
            _loadingLabel.Text = loading ? message : string.Empty;
        }

        if (_assetsScroll != null && loading)
            _assetsScroll.Visible = false;

        if (_noResultsLabel != null && loading)
            _noResultsLabel.Visible = false;
    }

    private void SetStatus(string message, bool shorten = false)
    {
        if (_statusLabel == null)
            return;

        if (shorten && message.Length > MaxStatusLength)
        {
            _statusLabel.Text = $"{message[..(MaxStatusLength - 3)]}...";
            _statusLabel.TooltipText = message;
        }
        else
        {
            _statusLabel.Text = message;
            _statusLabel.TooltipText = string.Empty;
        }
    }

    private void UpdateInteractionState(bool authenticated)
    {
        if (_createFolderButton != null)
            _createFolderButton.Disabled = !authenticated;

        if (_typeFilter != null)
            _typeFilter.Disabled = !authenticated;

        if (_folderSelector != null)
            _folderSelector.Disabled = !authenticated;

        if (_searchBox != null)
            _searchBox.Editable = authenticated;
    }

    private void UpdateActionButtons()
    {
        bool hasSelection = _selectedAsset != null;
        bool authenticated = _client?.IsAuthenticated == true;

        if (_shareButton != null)
            _shareButton.Disabled = !authenticated || !hasSelection;

        if (_unshareButton != null)
            _unshareButton.Disabled = !authenticated || !hasSelection;

        if (_getUrlButton != null)
            _getUrlButton.Disabled = !hasSelection;

        if (_deleteButton != null)
            _deleteButton.Disabled = !authenticated || !hasSelection;

        if (_saveAvatarButton != null)
            _saveAvatarButton.Disabled = !hasSelection || _selectedAsset?.Type != AssetType.Avatar;
    }

    private void UpdateGridColumns()
    {
        if (_assetGrid == null || _assetsScroll == null)
            return;

        var availableWidth = _assetsScroll.Size.X;
        if (availableWidth <= 0f)
            return;

        int columns = Mathf.Max(1, (int)Math.Floor((availableWidth + GridSpacing) / (CardMinWidth + GridSpacing)));
        if (_assetGrid.Columns != columns)
            _assetGrid.Columns = columns;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 B";

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
