// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Godot.UI;

/// <summary>
/// Import dialog for selecting how to import files.
/// Supports images, 3D models, avatars, and raw files.
/// Functionality is split across partial class files:
///   ImportDialog.TypeSelector.cs  - option button creation/filtering
///   ImportDialog.Progress.cs      - progress bar and status reporting
///   ImportDialog.Executor.cs      - per-type import logic
///   ImportDialog.AvatarSetup.cs   - avatar creator flow
/// </summary>
public partial class ImportDialog : Control
{
    public enum ImportType
    {
        ImageTexture,
        Model3D,
        Avatar,
        Shader,
        RawFile
    }

    private const string OptionButtonScenePath = "res://Scenes/UI/Components/ImportOptionButton.tscn";

    private Button _btnClose;
    private Button _btnInfo;
    private Button _btnAvatarSetup;
    private Label _titleLabel;
    private Label _subtitleLabel;
    private VBoxContainer _optionsList;
    private Label _fileInfoLabel;
    private Control _progressPanel;
    private Label _progressStatusLabel;
    private ProgressBar _progressBar;
    private PackedScene _optionButtonScene;
    private readonly List<Button> _optionButtons = new();

    private bool _isInitialized;
    private bool _isImporting;
    private ImportType? _selectedType;
    private string _filePath;
    private LocalDB _localDB;
    private Slot _targetSlot;
    private bool _hasImportSpawnPosition;
    private float3 _importSpawnPosition;
    private Slot _lastImportedAvatarSlot;
    private AvatarCreatorSession? _activeAvatarCreatorSession;
    private Control _infoTooltip;

    public event Action<ImportType, string> ImportRequested;
    public event Action DialogClosed;

    public override void _Ready()
    {
        _btnClose = GetNodeOrNull<Button>("MainMargin/VBox/Header/CloseButton");
        _btnInfo = GetNodeOrNull<Button>("MainMargin/VBox/Header/InfoButton");
        _btnAvatarSetup = GetNodeOrNull<Button>("MainMargin/VBox/AvatarSetupButton");
        _titleLabel = GetNodeOrNull<Label>("MainMargin/VBox/Header/Title");
        _subtitleLabel = GetNodeOrNull<Label>("MainMargin/VBox/Subtitle");
        _optionsList = GetNodeOrNull<VBoxContainer>("MainMargin/VBox/OptionsScroll/OptionsList");
        _fileInfoLabel = GetNodeOrNull<Label>("MainMargin/VBox/FileInfo");
        _progressPanel = GetNodeOrNull<Control>("MainMargin/VBox/ProgressPanel");
        _progressStatusLabel = GetNodeOrNull<Label>("MainMargin/VBox/ProgressPanel/Status");
        _progressBar = GetNodeOrNull<ProgressBar>("MainMargin/VBox/ProgressPanel/Bar");

        _optionButtonScene = GD.Load<PackedScene>(OptionButtonScenePath);
        if (_optionButtonScene == null)
            GD.PrintErr($"ImportDialog: Failed to load option button scene from {OptionButtonScenePath}");

        ConnectSignals();
        _isInitialized = true;
        SetImportInProgress(false);
        UpdateAvatarSetupButton();
        RefreshDialogForCurrentFile();
        GD.Print("ImportDialog: Initialized");
    }

    private void ConnectSignals()
    {
        _btnClose?.Connect("pressed", Callable.From(OnClosePressed));
        _btnInfo?.Connect("pressed", Callable.From(OnInfoPressed));
        _btnAvatarSetup?.Connect("pressed", Callable.From(OnAvatarSetupPressed));
    }

    /// <summary>
    /// Show the import dialog pre-configured for a specific file.
    /// </summary>
    public void ShowForFile(string filePath, Slot targetSlot = null, LocalDB localDB = null, float3? importSpawnPosition = null)
    {
        ResetAvatarCreatorState();
        _filePath = filePath;
        _targetSlot = targetSlot;
        _localDB = localDB;
        _hasImportSpawnPosition = importSpawnPosition.HasValue;
        _importSpawnPosition = importSpawnPosition ?? float3.Zero;
        _selectedType = null;
        _isImporting = false;
        _lastImportedAvatarSlot = null;

        if (_isInitialized)
        {
            UpdateAvatarSetupButton();
            RefreshDialogForCurrentFile();
            Show();
        }
    }

    /// <summary>
    /// Show the import dialog in generic mode.
    /// </summary>
    public void ShowDialog(Slot targetSlot = null, LocalDB localDB = null)
    {
        ResetAvatarCreatorState();
        _filePath = null;
        _targetSlot = targetSlot;
        _localDB = localDB;
        _hasImportSpawnPosition = false;
        _importSpawnPosition = float3.Zero;
        _selectedType = null;
        _isImporting = false;
        _lastImportedAvatarSlot = null;

        if (_isInitialized)
        {
            UpdateAvatarSetupButton();
            RefreshDialogForCurrentFile();
            Show();
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

    public void SetLocalDB(LocalDB localDB) => _localDB = localDB;

    public void SetTargetSlot(Slot targetSlot) => _targetSlot = targetSlot;

    private void RefreshDialogForCurrentFile()
    {
        if (!_isInitialized)
            return;

        if (string.IsNullOrWhiteSpace(_filePath))
        {
            SetTitle("Import");
            SetSubtitle("What are you importing?");
            if (_fileInfoLabel != null)
                _fileInfoLabel.Visible = false;
            CreateAllOptionButtons();
        }
        else
        {
            SetTitle($"Import: {Path.GetFileName(_filePath)}");
            SetSubtitle("Choose import type");
            if (_fileInfoLabel != null)
            {
                _fileInfoLabel.Text = _filePath;
                _fileInfoLabel.Visible = true;
            }

            CreateOptionButtonsForFile(Path.GetExtension(_filePath).ToLowerInvariant());
        }

        SetImportInProgress(_isImporting);

        var optionsScroll = GetNodeOrNull<ScrollContainer>("MainMargin/VBox/OptionsScroll");
        if (optionsScroll != null)
            optionsScroll.ScrollVertical = 0;
    }

    private void OnClosePressed()
    {
        if (_isImporting)
            return;

        ResetAvatarCreatorState();
        GD.Print("ImportDialog: Closed");
        DialogClosed?.Invoke();
        Hide();
    }

    private void OnInfoPressed()
    {
        // toggle off if already up so spam-clicking dismisses it - xlinka
        if (_infoTooltip != null && IsInstanceValid(_infoTooltip))
        {
            _infoTooltip.QueueFree();
            _infoTooltip = null;
            return;
        }

        ShowInfoTooltip();
    }

    private async void ShowInfoTooltip()
    {
        if (_btnInfo == null)
            return;

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.10f, 0.18f, 0.96f),
            BorderColor = new Color(0.47f, 0.37f, 0.94f, 0.85f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 10,
            ContentMarginTop = 8,
            ContentMarginRight = 10,
            ContentMarginBottom = 8,
        };

        // PanelContainer auto-shrinks to its child. plain Panel does not, which is why the old version was a giant black box - xlinka
        var panel = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 100,
        };
        panel.AddThemeStyleboxOverride("panel", style);

        var vbox = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
        };
        vbox.AddThemeConstantOverride("separation", 4);
        panel.AddChild(vbox);

        AddTooltipLabel(vbox, "Supported formats", header: true);
        AddTooltipLabel(vbox, "Images: PNG, JPG, JPEG, WebP, BMP, TGA", header: false);
        AddTooltipLabel(vbox, "3D: GLB, GLTF, FBX, OBJ, DAE, 3DS, BLEND, STL, PLY, X, ASE", header: false);
        AddTooltipLabel(vbox, "Avatars: VRM, GLB, GLTF, FBX, DAE", header: false);

        AddChild(panel);
        _infoTooltip = panel;

        // wait one frame so the container actually has a laid out size before we read it - xlinka
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!IsInstanceValid(panel))
            return;

        var btnPos = _btnInfo.GetGlobalPosition() - GetGlobalPosition();
        var btnSize = _btnInfo.Size;
        var panelSize = panel.Size;

        float x = btnPos.X + btnSize.X * 0.5f - panelSize.X * 0.5f;
        float y = btnPos.Y - panelSize.Y - 6f;

        // not enough room above. drop it below instead - xlinka
        if (y < 4f)
            y = btnPos.Y + btnSize.Y + 6f;

        x = Mathf.Clamp(x, 8f, Mathf.Max(8f, Size.X - panelSize.X - 8f));
        panel.Position = new Vector2(x, y);

        var tooltipRef = panel;
        var timer = GetTree().CreateTimer(4.0);
        timer.Timeout += () =>
        {
            if (tooltipRef != null && IsInstanceValid(tooltipRef))
            {
                tooltipRef.QueueFree();
                if (_infoTooltip == tooltipRef)
                    _infoTooltip = null;
            }
        };
    }

    private static void AddTooltipLabel(VBoxContainer parent, string text, bool header)
    {
        var label = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", header ? 13 : 11);
        label.AddThemeColorOverride(
            "font_color",
            header ? new Color(0.47f, 0.37f, 0.94f, 1f) : new Color(0.85f, 0.85f, 0.92f, 1f));
        parent.AddChild(label);
    }

    private async void OnOptionSelected(ImportType type)
    {
        if (_isImporting)
            return;

        if (type != ImportType.Avatar)
        {
            ResetAvatarCreatorState();
            _lastImportedAvatarSlot = null;
            UpdateAvatarSetupButton();
        }

        _selectedType = type;
        GD.Print($"ImportDialog: Selected {type}");
        ImportRequested?.Invoke(type, _filePath);

        bool success = true;
        if (!string.IsNullOrEmpty(_filePath) && _targetSlot != null)
            success = await PerformImport(type, _filePath);

        if (!success)
            return;

        // avatars need the dialog open so the user can hit the Avatar Creator button - xlinka
        if (type == ImportType.Avatar)
        {
            SetSubtitle("Avatar imported as draft");
            return;
        }

        SetSubtitle("Import finished");
        ScheduleAutoClose(0.6);
    }

    private void ScheduleAutoClose(double delaySeconds)
    {
        var timer = GetTree().CreateTimer(delaySeconds);
        timer.Timeout += () =>
        {
            if (!IsInstanceValid(this) || _isImporting)
                return;

            ResetAvatarCreatorState();
            DialogClosed?.Invoke();
            Hide();
        };
    }

    private void ResetAvatarCreatorState()
    {
        _activeAvatarCreatorSession?.Dispose();
        _activeAvatarCreatorSession = null;
    }
}
