using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Core.Assets;

namespace Lumora.Godot.UI;

/// <summary>
/// Import dialog for selecting how to import files.
/// Supports images, 3D models, avatars, and raw files.
/// </summary>
public partial class ImportDialog : Control
{
    public enum ImportType
    {
        ImageTexture,
        Model3D,
        Avatar,
        RawFile
    }

    private static readonly (ImportType type, string label, string[] extensions)[] ImportOptions = new[]
    {
        (ImportType.ImageTexture, "Image / Texture", new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tga" }),
        (ImportType.Model3D, "3D Model", new[] { ".glb", ".gltf" }),
        (ImportType.Avatar, "Avatar (VRM/GLB)", new[] { ".vrm", ".glb", ".gltf" }),
        (ImportType.RawFile, "Raw File", Array.Empty<string>())
    };

    private const string OptionButtonScenePath = "res://Scenes/UI/Components/ImportOptionButton.tscn";

    private Button _btnClose;
    private Button _btnInfo;
    private Label _titleLabel;
    private Label _subtitleLabel;
    private VBoxContainer _optionsList;
    private Label _fileInfoLabel;
    private PackedScene _optionButtonScene;
    private readonly List<Button> _optionButtons = new();

    public event Action<ImportType, string> ImportRequested;
    public event Action DialogClosed;

    private ImportType? _selectedType;
    private string _filePath;
    private LocalDB _localDB;
    private Slot _targetSlot;

    public override void _Ready()
    {
        _btnClose = GetNodeOrNull<Button>("MainMargin/VBox/Header/CloseButton");
        _btnInfo = GetNodeOrNull<Button>("MainMargin/VBox/Header/InfoButton");
        _titleLabel = GetNodeOrNull<Label>("MainMargin/VBox/Header/Title");
        _subtitleLabel = GetNodeOrNull<Label>("MainMargin/VBox/Subtitle");
        _optionsList = GetNodeOrNull<VBoxContainer>("MainMargin/VBox/OptionsList");
        _fileInfoLabel = GetNodeOrNull<Label>("MainMargin/VBox/FileInfo");

        _optionButtonScene = GD.Load<PackedScene>(OptionButtonScenePath);
        if (_optionButtonScene == null)
        {
            GD.PrintErr($"ImportDialog: Failed to load option button scene from {OptionButtonScenePath}");
        }

        ConnectSignals();
        GD.Print("ImportDialog: Initialized");
    }

    private void ConnectSignals()
    {
        _btnClose?.Connect("pressed", Callable.From(OnClosePressed));
        _btnInfo?.Connect("pressed", Callable.From(OnInfoPressed));
    }

    /// <summary>
    /// Show import dialog for a specific file.
    /// </summary>
    public void ShowForFile(string filePath, Slot targetSlot = null, LocalDB localDB = null)
    {
        _filePath = filePath;
        _targetSlot = targetSlot;
        _localDB = localDB;
        _selectedType = null;

        // Update title based on file
        var fileName = Path.GetFileName(filePath);
        SetTitle($"Import: {fileName}");

        // Show file info if label exists
        if (_fileInfoLabel != null)
        {
            _fileInfoLabel.Text = filePath;
            _fileInfoLabel.Visible = true;
        }

        // Create option buttons based on file type
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        CreateOptionButtonsForFile(extension);

        Show();
    }

    /// <summary>
    /// Show generic import dialog.
    /// </summary>
    public void ShowDialog(Slot targetSlot = null, LocalDB localDB = null)
    {
        _filePath = null;
        _targetSlot = targetSlot;
        _localDB = localDB;
        _selectedType = null;

        SetTitle("Import");
        if (_fileInfoLabel != null)
            _fileInfoLabel.Visible = false;

        // Show all options
        CreateAllOptionButtons();
        Show();
    }

    private void CreateOptionButtonsForFile(string extension)
    {
        if (_optionsList == null) return;

        ClearOptionButtons();

        // Find matching import types for this extension
        var matchingOptions = new List<(ImportType type, string label)>();

        foreach (var option in ImportOptions)
        {
            // Raw file always available
            if (option.type == ImportType.RawFile)
            {
                matchingOptions.Add((option.type, option.label));
                continue;
            }

            // Check if extension matches
            foreach (var ext in option.extensions)
            {
                if (ext.Equals(extension, StringComparison.OrdinalIgnoreCase))
                {
                    matchingOptions.Add((option.type, option.label));
                    break;
                }
            }
        }

        // If no specific matches, show all options
        if (matchingOptions.Count <= 1)
        {
            CreateAllOptionButtons();
            return;
        }

        // Create buttons for matching options
        foreach (var (type, label) in matchingOptions)
        {
            CreateOptionButton(type, label);
        }
    }

    private void CreateAllOptionButtons()
    {
        ClearOptionButtons();

        foreach (var option in ImportOptions)
        {
            CreateOptionButton(option.type, option.label);
        }
    }

    private void CreateOptionButton(ImportType type, string label)
    {
        if (_optionsList == null) return;

        Button button;

        if (_optionButtonScene != null)
        {
            button = _optionButtonScene.Instantiate<Button>();
        }
        else
        {
            // Fallback to basic button
            button = new Button();
            button.CustomMinimumSize = new Vector2(0, 36);
        }

        button.Text = label;
        _optionsList.AddChild(button);

        var capturedType = type;
        button.Connect("pressed", Callable.From(() => OnOptionSelected(capturedType)));
        _optionButtons.Add(button);
    }

    private void ClearOptionButtons()
    {
        foreach (var btn in _optionButtons)
        {
            btn.QueueFree();
        }
        _optionButtons.Clear();
    }

    private void OnClosePressed()
    {
        GD.Print("ImportDialog: Closed");
        DialogClosed?.Invoke();
        Hide();
    }

    private void OnInfoPressed()
    {
        GD.Print("ImportDialog: Info - Supported formats:");
        GD.Print("  Images: PNG, JPG, JPEG, WebP, BMP, TGA");
        GD.Print("  3D Models: GLB, GLTF");
        GD.Print("  Avatars: VRM, GLB, GLTF");
    }

    private async void OnOptionSelected(ImportType type)
    {
        _selectedType = type;
        GD.Print($"ImportDialog: Selected {type}");

        // Emit event for external handling
        ImportRequested?.Invoke(type, _filePath);

        // If we have all necessary info, perform the import
        if (!string.IsNullOrEmpty(_filePath) && _targetSlot != null)
        {
            await PerformImport(type, _filePath);
        }

        Hide();
    }

    private async Task PerformImport(ImportType type, string filePath)
    {
        GD.Print($"ImportDialog: Performing {type} import of '{filePath}'");

        try
        {
            switch (type)
            {
                case ImportType.ImageTexture:
                    await ImportImage(filePath);
                    break;

                case ImportType.Model3D:
                    await ImportModel(filePath, isAvatar: false);
                    break;

                case ImportType.Avatar:
                    await ImportModel(filePath, isAvatar: true);
                    break;

                case ImportType.RawFile:
                    await ImportRawFile(filePath);
                    break;
            }

            GD.Print($"ImportDialog: Import completed successfully");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ImportDialog: Import failed: {ex.Message}");
        }
    }

    private async Task ImportImage(string filePath)
    {
        // Import to LocalDB
        string localUri = null;
        if (_localDB != null)
        {
            localUri = await _localDB.ImportLocalAssetAsync(filePath, LocalDB.ImportLocation.Copy);
        }

        // Create image slot
        var imageSlot = _targetSlot.AddSlot(Path.GetFileNameWithoutExtension(filePath));

        // TODO: Create ImageProvider component
        GD.Print($"ImportDialog: Image imported to {localUri ?? filePath}");
    }

    private async Task ImportModel(string filePath, bool isAvatar)
    {
        ModelImportResult result;

        if (isAvatar)
        {
            result = await ModelImporter.ImportAvatarAsync(filePath, _targetSlot, _localDB);
        }
        else
        {
            result = await ModelImporter.ImportModelAsync(filePath, _targetSlot, null, _localDB);
        }

        if (result.Success)
        {
            GD.Print($"ImportDialog: Model imported successfully to slot '{result.RootSlot?.SlotName.Value}'");

            if (isAvatar)
            {
                GD.Print("ImportDialog: Avatar is ready to equip!");
                // The avatar is now loaded and can be equipped via AvatarManager
            }
        }
        else
        {
            GD.PrintErr($"ImportDialog: Model import failed: {result.ErrorMessage}");
        }
    }

    private async Task ImportRawFile(string filePath)
    {
        // Just import to LocalDB
        if (_localDB != null)
        {
            var localUri = await _localDB.ImportLocalAssetAsync(filePath, LocalDB.ImportLocation.Copy);
            GD.Print($"ImportDialog: Raw file imported to {localUri}");
        }
    }

    public void SetTitle(string title)
    {
        if (_titleLabel != null)
            _titleLabel.Text = title;
    }

    public void SetSubtitle(string subtitle)
    {
        if (_subtitleLabel != null)
            _subtitleLabel.Text = subtitle;
    }

    public ImportType? GetSelectedType() => _selectedType;
    public string GetFilePath() => _filePath;

    /// <summary>
    /// Set the LocalDB instance for imports.
    /// </summary>
    public void SetLocalDB(LocalDB localDB)
    {
        _localDB = localDB;
    }

    /// <summary>
    /// Set the target slot for imports.
    /// </summary>
    public void SetTargetSlot(Slot targetSlot)
    {
        _targetSlot = targetSlot;
    }
}
