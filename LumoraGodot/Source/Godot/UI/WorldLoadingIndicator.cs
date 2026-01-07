using Godot;
using Lumora.Core;
using Lumora.Core.Management;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Source.Godot.UI;

/// <summary>
/// 2D loading indicator overlay that shows when joining a world.
/// Displays progress and allows cancellation.
/// </summary>
public partial class WorldLoadingIndicator : CanvasLayer
{
    // UI elements
    private ColorRect _backgroundOverlay;
    private PanelContainer _panel;
    private Label _titleLabel;
    private Label _statusLabel;
    private ProgressBar _progressBar;
    private Label _percentLabel;
    private Button _cancelButton;

    private WorldLoadingOperation _currentOperation;
    private static WorldLoadingIndicator _instance;

    public static WorldLoadingIndicator Instance => _instance;

    public override void _Ready()
    {
        _instance = this;
        Layer = 99; // Just below dashboard (100)
        Visible = false;

        CreateUI();
        SubscribeToEvents();

        AquaLogger.Log("WorldLoadingIndicator: Initialized");
    }

    public override void _ExitTree()
    {
        UnsubscribeFromEvents();
        if (_instance == this)
            _instance = null;
    }

    private void CreateUI()
    {
        // Semi-transparent background
        _backgroundOverlay = new ColorRect();
        _backgroundOverlay.Color = new Color(0, 0, 0, 0.6f);
        _backgroundOverlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_backgroundOverlay);

        // Center panel
        _panel = new PanelContainer();
        _panel.CustomMinimumSize = new Vector2(450, 200);
        _panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        _panel.Position = new Vector2(-225, -100);
        AddChild(_panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 30);
        margin.AddThemeConstantOverride("margin_right", 30);
        margin.AddThemeConstantOverride("margin_top", 25);
        margin.AddThemeConstantOverride("margin_bottom", 25);
        _panel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 15);
        margin.AddChild(vbox);

        // Title - "Joining World"
        _titleLabel = new Label();
        _titleLabel.Text = "Joining World";
        _titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _titleLabel.AddThemeFontSizeOverride("font_size", 22);
        vbox.AddChild(_titleLabel);

        // Status text
        _statusLabel = new Label();
        _statusLabel.Text = "Connecting...";
        _statusLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _statusLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        vbox.AddChild(_statusLabel);

        // Progress bar container
        var progressHBox = new HBoxContainer();
        progressHBox.AddThemeConstantOverride("separation", 10);
        vbox.AddChild(progressHBox);

        _progressBar = new ProgressBar();
        _progressBar.MinValue = 0;
        _progressBar.MaxValue = 100;
        _progressBar.Value = 0;
        _progressBar.ShowPercentage = false;
        _progressBar.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _progressBar.CustomMinimumSize = new Vector2(0, 25);
        progressHBox.AddChild(_progressBar);

        _percentLabel = new Label();
        _percentLabel.Text = "0%";
        _percentLabel.CustomMinimumSize = new Vector2(45, 0);
        _percentLabel.HorizontalAlignment = HorizontalAlignment.Right;
        progressHBox.AddChild(_percentLabel);

        // Cancel button
        var buttonContainer = new HBoxContainer();
        buttonContainer.Alignment = BoxContainer.AlignmentMode.Center;
        vbox.AddChild(buttonContainer);

        _cancelButton = new Button();
        _cancelButton.Text = "Cancel";
        _cancelButton.CustomMinimumSize = new Vector2(120, 35);
        _cancelButton.Connect("pressed", Callable.From(OnCancelPressed));
        buttonContainer.AddChild(_cancelButton);
    }

    private void SubscribeToEvents()
    {
        var service = Lumora.Core.Engine.Current?.WorldLoadingService;
        if (service != null)
        {
            service.OnLoadingStarted += OnLoadingStarted;
            service.OnLoadingProgress += OnLoadingProgress;
            service.OnLoadingComplete += OnLoadingComplete;
            service.OnLoadingFailed += OnLoadingFailed;
        }
    }

    private void UnsubscribeFromEvents()
    {
        var service = Lumora.Core.Engine.Current?.WorldLoadingService;
        if (service != null)
        {
            service.OnLoadingStarted -= OnLoadingStarted;
            service.OnLoadingProgress -= OnLoadingProgress;
            service.OnLoadingComplete -= OnLoadingComplete;
            service.OnLoadingFailed -= OnLoadingFailed;
        }
    }

    private void OnLoadingStarted(WorldLoadingOperation operation)
    {
        _currentOperation = operation;
        CallDeferred(nameof(ShowIndicator), operation.WorldName);
    }

    private void OnLoadingProgress(WorldLoadingOperation operation)
    {
        CallDeferred(nameof(UpdateProgress), operation.Progress, operation.StatusMessage);
    }

    private void OnLoadingComplete(WorldLoadingOperation operation)
    {
        CallDeferred(nameof(HideIndicator));
    }

    private void OnLoadingFailed(WorldLoadingOperation operation)
    {
        CallDeferred(nameof(ShowError), operation.ErrorMessage);
    }

    private void ShowIndicator(string worldName)
    {
        _titleLabel.Text = $"Joining: {worldName}";
        _statusLabel.Text = "Connecting...";
        _progressBar.Value = 0;
        _percentLabel.Text = "0%";
        _cancelButton.Text = "Cancel";
        _cancelButton.Disabled = false;
        Visible = true;

        AquaLogger.Log($"WorldLoadingIndicator: Showing for '{worldName}'");
    }

    private void UpdateProgress(float progress, string status)
    {
        var percent = Mathf.RoundToInt(progress * 100);
        _progressBar.Value = percent;
        _percentLabel.Text = $"{percent}%";
        _statusLabel.Text = status;
    }

    private void HideIndicator()
    {
        Visible = false;
        _currentOperation = null;
        AquaLogger.Log("WorldLoadingIndicator: Hidden");
    }

    private void ShowError(string error)
    {
        _statusLabel.Text = $"Failed: {error}";
        _statusLabel.AddThemeColorOverride("font_color", new Color(1f, 0.4f, 0.4f));
        _cancelButton.Text = "Close";
        _cancelButton.Disabled = false;

        // Auto-hide after 3 seconds
        GetTree().CreateTimer(3.0).Timeout += () =>
        {
            if (Visible && _cancelButton.Text == "Close")
            {
                HideIndicator();
            }
        };
    }

    private void OnCancelPressed()
    {
        if (_currentOperation != null && !_currentOperation.IsComplete && !_currentOperation.IsFailed)
        {
            _currentOperation.Cancel();
            _statusLabel.Text = "Cancelled";
        }
        HideIndicator();
    }

    /// <summary>
    /// Ensure the indicator is created and added to scene tree.
    /// Call this during engine initialization.
    /// </summary>
    public static void EnsureCreated()
    {
        if (_instance != null)
            return;

        var sceneTree = global::Godot.Engine.GetMainLoop() as SceneTree;
        if (sceneTree?.Root == null)
            return;

        var indicator = new WorldLoadingIndicator();
        indicator.Name = "WorldLoadingIndicator";
        sceneTree.Root.AddChild(indicator);

        AquaLogger.Log("WorldLoadingIndicator: Created and added to scene tree");
    }
}
