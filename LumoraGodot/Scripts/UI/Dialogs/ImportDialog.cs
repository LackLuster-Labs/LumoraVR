using Godot;
using System;
using System.Collections.Generic;

namespace Lumora.Godot.UI;

/// <summary>
/// Import dialog for selecting how to import files.
/// </summary>
public partial class ImportDialog : Control
{
    public enum ImportType
    {
        ImageTexture,
        RawFile
    }

    private static readonly (ImportType type, string label)[] DefaultOptions = new[]
    {
        (ImportType.ImageTexture, "Image / Texture"),
        (ImportType.RawFile, "Raw File")
    };

    private const string OptionButtonScenePath = "res://Scenes/UI/Components/ImportOptionButton.tscn";

    private Button? _btnClose;
    private Button? _btnInfo;
    private Label? _titleLabel;
    private VBoxContainer? _optionsList;
    private PackedScene? _optionButtonScene;
    private readonly List<Button> _optionButtons = new();

    public event Action<ImportType>? ImportTypeSelected;
    public event Action? DialogClosed;

    private ImportType? _selectedType;

    public override void _Ready()
    {
        _btnClose = GetNodeOrNull<Button>("MainMargin/VBox/Header/CloseButton");
        _btnInfo = GetNodeOrNull<Button>("MainMargin/VBox/Header/InfoButton");
        _titleLabel = GetNodeOrNull<Label>("MainMargin/VBox/Header/Title");
        _optionsList = GetNodeOrNull<VBoxContainer>("MainMargin/VBox/OptionsList");

        _optionButtonScene = GD.Load<PackedScene>(OptionButtonScenePath);
        if (_optionButtonScene == null)
        {
            GD.PrintErr($"ImportDialog: Failed to load option button scene from {OptionButtonScenePath}");
        }

        ConnectSignals();
        CreateOptionButtons(DefaultOptions);
        GD.Print("ImportDialog: Initialized");
    }

    private void ConnectSignals()
    {
        _btnClose?.Connect("pressed", Callable.From(OnClosePressed));
        _btnInfo?.Connect("pressed", Callable.From(OnInfoPressed));
    }

    private void CreateOptionButtons((ImportType type, string label)[] options)
    {
        if (_optionsList == null || _optionButtonScene == null) return;

        // Clear existing buttons
        foreach (var btn in _optionButtons)
        {
            btn.QueueFree();
        }
        _optionButtons.Clear();

        // Create new buttons from scene
        foreach (var (type, label) in options)
        {
            var button = _optionButtonScene.Instantiate<Button>();
            button.Text = label;
            _optionsList.AddChild(button);

            var capturedType = type;
            button.Connect("pressed", Callable.From(() => OnOptionSelected(capturedType)));
            _optionButtons.Add(button);
        }
    }

    private void OnClosePressed()
    {
        GD.Print("ImportDialog: Closed");
        DialogClosed?.Invoke();
        Hide();
    }

    private void OnInfoPressed()
    {
        GD.Print("ImportDialog: Info button pressed");
    }

    private void OnOptionSelected(ImportType type)
    {
        _selectedType = type;
        GD.Print($"ImportDialog: Selected {type}");
        ImportTypeSelected?.Invoke(type);
    }

    public void SetTitle(string title)
    {
        if (_titleLabel != null)
            _titleLabel.Text = title;
    }

    public ImportType? GetSelectedType() => _selectedType;

    public void ShowDialog()
    {
        _selectedType = null;
        Show();
    }
}
