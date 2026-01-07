using System;
using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Base class for wizard-style multi-step UI forms.
/// </summary>
[ComponentCategory("HelioUI/Wizard")]
public abstract class HelioWizardForm : Component
{
    // ===== REFERENCES =====

    /// <summary>
    /// The swap canvas panel used for transitions.
    /// </summary>
    protected SyncRef<HelioSwapCanvasPanel> SwapPanel { get; private set; }

    /// <summary>
    /// Navigation path stack.
    /// </summary>
    protected readonly List<Action<HelioUIBuilder>> Path = new();

    // ===== CONFIGURATION =====

    /// <summary>
    /// Duration of panel transitions.
    /// </summary>
    protected virtual float Duration => 0.25f;

    /// <summary>
    /// Size of the wizard canvas in pixels.
    /// </summary>
    protected virtual float2 CanvasSize => new float2(400f, 800f);

    /// <summary>
    /// Pixels per world unit. Higher = smaller physical panel.
    /// Default 1000 = 800px / 1000 = 0.8m tall.
    /// </summary>
    protected virtual float CanvasScale => 1000f;

    /// <summary>
    /// Access to the underlying window panel.
    /// </summary>
    public HelioWindowPanel Panel => SwapPanel?.Target?.Panel;

    // ===== INITIALIZATION =====

    public override void OnAwake()
    {
        base.OnAwake();
        SwapPanel = new SyncRef<HelioSwapCanvasPanel>(this);
    }

    public override void OnStart()
    {
        base.OnStart();

        // Create swap canvas panel
        var swapPanel = Slot.AttachComponent<HelioSwapCanvasPanel>();
        SwapPanel.Target = swapPanel;

        // Initialize immediately so Canvas and Panel are available
        swapPanel.Initialize();

        // Now we can set size (canvas exists)
        swapPanel.CanvasSize = CanvasSize;
        swapPanel.CanvasScale = CanvasScale;

        // Setup panel buttons (panel now exists)
        Panel?.AddCloseButton();
        Panel?.AddParentButton();
        if (Panel != null)
            Panel.Title.Value = "Wizard";

        // Open root step (no animation for initial)
        OpenRoot(swapPanel.SwapPanel(Slide.None));
    }

    // ===== NAVIGATION =====

    /// <summary>
    /// Override in subclasses to build the initial wizard step.
    /// </summary>
    protected abstract void OpenRoot(HelioUIBuilder ui);

    /// <summary>
    /// Navigate to a new step (slides left).
    /// </summary>
    protected void Open(Action<HelioUIBuilder> builder)
    {
        if (builder == null) return;
        Path.Add(builder);
        builder(SwapPanel.Target.SwapPanel(Slide.Left, Duration));
    }

    /// <summary>
    /// Return to the previous step (slides right).
    /// </summary>
    protected void Return()
    {
        if (Path.Count == 0) return;

        Path.RemoveAt(Path.Count - 1);
        var action = Path.Count > 0 ? Path[^1] : new Action<HelioUIBuilder>(OpenRoot);
        action(SwapPanel.Target.SwapPanel(Slide.Right, Duration));
    }

    /// <summary>
    /// Close the wizard entirely.
    /// </summary>
    public virtual void Close()
    {
        Path.Clear();
        Slot.Destroy();
    }

    /// <summary>
    /// Reset to the root step.
    /// </summary>
    public void Reset()
    {
        Path.Clear();
        OpenRoot(SwapPanel.Target.SwapPanel(Slide.Right, Duration));
    }

    // ===== UTILITY =====

    /// <summary>
    /// Create a standard navigation footer with Back and Next buttons.
    /// </summary>
    protected void BuildNavigationFooter(HelioUIBuilder ui, string nextLabel = "Next", Action onNext = null, bool showBack = true)
    {
        ui.HorizontalLayout(spacing: 8f);

        if (showBack && Path.Count > 0)
        {
            ui.Button("Back", Return);
        }

        ui.FlexibleSpacer();

        if (onNext != null)
        {
            ui.Button(nextLabel, onNext);
        }

        ui.EndLayout();
    }

    /// <summary>
    /// Create a standard header with title.
    /// </summary>
    protected void BuildHeader(HelioUIBuilder ui, string title)
    {
        ui.Text(title, fontSize: 24f);
        ui.Spacer(16f);
    }
}
