using Godot;
using Lumora.Core;
using Lumora.Core.GodotUI;
using Aquamarine.Godot.Hooks.GodotUI;
using Aquamarine.Source.Input;
using AquaLogger = Lumora.Core.Logging.Logger;
using RuntimeEngine = Lumora.Core.Engine;
using GodotInput = Godot.Input;

namespace Aquamarine.Source.UI;

/// <summary>
/// Handles input for toggling the userspace dashboard panel.
/// - Desktop: Escape key
/// - VR: Menu button (B/Y button on right controller)
/// </summary>
public partial class DashboardToggle : Node
{
    private static DashboardToggle? _instance;
    public static DashboardToggle? Instance => _instance;

    /// <summary>
    /// Whether the dashboard is currently visible.
    /// Used by InputManager to coordinate mouse capture.
    /// </summary>
    public static bool IsDashboardVisible { get; private set; }

    // VR button state tracking for edge detection
    private bool _previousMenuButton = false;

    // Reference to the dashboard panel in userspace
    private DashboardPanel? _dashboardPanel;

    public override void _Ready()
    {
        base._Ready();
        _instance = this;
        AquaLogger.Log("DashboardToggle: Initialized");
    }

    public override void _Process(double delta)
    {
        base._Process(delta);

        // Find dashboard panel if not yet found
        if (_dashboardPanel == null)
        {
            FindDashboardPanel();
        }

        if (_dashboardPanel == null) return;

        bool shouldToggle = false;

        // Desktop: Check Escape key
        if (GodotInput.IsActionJustPressed("ui_cancel"))
        {
            AquaLogger.Log("DashboardToggle: Escape key pressed");
            shouldToggle = true;
        }

        // VR: Check menu button (B/Y button) - edge detection
        if (IInputProvider.Instance?.IsVR == true)
        {
            bool menuButton = IInputProvider.RightSecondaryInput; // B/Y button
            if (menuButton && !_previousMenuButton)
            {
                shouldToggle = true;
            }
            _previousMenuButton = menuButton;
        }

        if (shouldToggle)
        {
            ToggleDashboard();
        }

        // Keep static property in sync
        IsDashboardVisible = _dashboardPanel.IsVisible.Value;
    }

    private void FindDashboardPanel()
    {
        // Get userspace world from engine
        var engine = RuntimeEngine.Current;
        if (engine == null)
        {
            AquaLogger.Log("DashboardToggle: Engine.Current is null");
            return;
        }

        var userspaceWorld = engine.WorldManager?.UserspaceWorld;
        if (userspaceWorld == null)
        {
            AquaLogger.Log("DashboardToggle: UserspaceWorld is null");
            return;
        }

        // Find the dashboard slot - navigate step by step
        var userspaceRoot = userspaceWorld.RootSlot.FindChild("UserspaceRoot");
        if (userspaceRoot == null)
        {
            AquaLogger.Log("DashboardToggle: UserspaceRoot slot not found");
            return;
        }

        var dashboardSlot = userspaceRoot.FindChild("Dashboard");
        if (dashboardSlot == null)
        {
            AquaLogger.Log("DashboardToggle: Dashboard slot not found under UserspaceRoot");
            return;
        }

        _dashboardPanel = dashboardSlot.GetComponent<DashboardPanel>();
        if (_dashboardPanel != null)
        {
            AquaLogger.Log("DashboardToggle: Found dashboard panel");
        }
        else
        {
            AquaLogger.Log("DashboardToggle: DashboardPanel component not found on slot");
        }
    }

    /// <summary>
    /// Toggle the dashboard visibility.
    /// </summary>
    public void ToggleDashboard()
    {
        if (_dashboardPanel == null)
        {
            AquaLogger.Warn("DashboardToggle: No dashboard panel found");
            return;
        }

        _dashboardPanel.Toggle();
        IsDashboardVisible = _dashboardPanel.IsVisible.Value;
        AquaLogger.Log($"DashboardToggle: Dashboard visible = {IsDashboardVisible}");
    }

    /// <summary>
    /// Show the dashboard.
    /// </summary>
    public void ShowDashboard()
    {
        _dashboardPanel?.Show();
        IsDashboardVisible = _dashboardPanel?.IsVisible.Value ?? false;
    }

    /// <summary>
    /// Hide the dashboard.
    /// </summary>
    public void HideDashboard()
    {
        _dashboardPanel?.Hide();
        IsDashboardVisible = false;
    }

    public override void _ExitTree()
    {
        if (_instance == this)
            _instance = null;
        base._ExitTree();
    }
}
