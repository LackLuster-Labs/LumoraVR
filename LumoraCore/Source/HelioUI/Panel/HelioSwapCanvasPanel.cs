using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Slide direction for panel transitions.
/// </summary>
public enum Slide
{
    None,
    Left,
    Right
}

/// <summary>
/// Canvas panel with animated content swapping.
/// </summary>
[ComponentCategory("HelioUI/Panel")]
public class HelioSwapCanvasPanel : HelioCanvasPanel
{
    // ===== REFERENCES =====

    protected SyncRef<HelioRectTransform> _currentPanel;
    protected SyncRef<Slot> _container;

    // ===== INITIALIZATION =====

    public override void OnAwake()
    {
        base.OnAwake();
        _currentPanel = new SyncRef<HelioRectTransform>(this);
        _container = new SyncRef<Slot>(this);
    }

    protected override void SetupPanel()
    {
        base.SetupPanel();

        // Create container with mask for clipping
        var container = SetupContainer();
        _container.Target = container;

        // Add mask component for clipping during swaps
        container.AttachComponent<HelioMask>();

        // Add background image for mask
        var maskPanel = container.AttachComponent<HelioPanel>();
        maskPanel.BackgroundColor.Value = new color(0f, 0f, 0f, 0f); // Transparent
    }

    /// <summary>
    /// Setup the swap container.
    /// </summary>
    protected virtual Slot SetupContainer()
    {
        var canvasSlot = Canvas?.Slot;
        if (canvasSlot == null) return Slot.AddSlot("Container");

        var container = canvasSlot.AddSlot("Container");

        // Container must fill the canvas
        var rect = container.AttachComponent<HelioRectTransform>();
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;
        rect.OffsetMin.Value = float2.Zero;
        rect.OffsetMax.Value = float2.Zero;

        return container;
    }

    /// <summary>
    /// Swap to a new panel with animation.
    /// Returns a UIBuilder for building content.
    /// </summary>
    public HelioUIBuilder SwapPanel(Slide slide, float duration = 0.25f)
    {
        var container = _container?.Target;
        if (container == null)
        {
            // Fallback: create container with proper rect
            var canvasSlot = Canvas?.Slot ?? Slot;
            container = canvasSlot.AddSlot("Container");
            var containerRect = container.AttachComponent<HelioRectTransform>();
            containerRect.AnchorMin.Value = float2.Zero;
            containerRect.AnchorMax.Value = float2.One;
            _container.Target = container;
        }

        // Create new content slot
        var contentSlot = container.AddSlot("Content");
        var rect = contentSlot.AttachComponent<HelioRectTransform>();
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;

        // Add vertical layout for content
        var layout = contentSlot.AttachComponent<HelioVerticalLayout>();
        layout.Padding.Value = new float4(8f, 8f, 8f, 8f);
        layout.Spacing.Value = new float2(4f, 4f);

        var canvasSize = CanvasSize;

        // Animate based on slide direction
        switch (slide)
        {
            case Slide.Left:
                // New panel slides in from right
                AnimateIn(rect, new float2(canvasSize.x, 0f), duration);
                AnimateOut(_currentPanel?.Target, new float2(-canvasSize.x, 0f), duration);
                break;

            case Slide.Right:
                // New panel slides in from left
                AnimateIn(rect, new float2(-canvasSize.x, 0f), duration);
                AnimateOut(_currentPanel?.Target, new float2(canvasSize.x, 0f), duration);
                break;

            case Slide.None:
            default:
                // Instant swap
                DestroyCurrentPanel();
                break;
        }

        _currentPanel.Target = rect;
        return new HelioUIBuilder(contentSlot);
    }

    private void AnimateIn(HelioRectTransform rect, float2 fromOffset, float duration)
    {
        if (rect == null || duration <= 0f) return;

        // Start offset
        rect.OffsetMin.Value = fromOffset;
        rect.OffsetMax.Value = fromOffset;

        // TODO: Implement tween system for smooth animation
        // For now, just set to final position
        rect.OffsetMin.Value = float2.Zero;
        rect.OffsetMax.Value = float2.Zero;
    }

    private void AnimateOut(HelioRectTransform rect, float2 toOffset, float duration)
    {
        if (rect == null) return;

        // TODO: Implement tween system
        // For now, just destroy
        rect.Slot.Destroy();
    }

    private void DestroyCurrentPanel()
    {
        var current = _currentPanel?.Target;
        if (current != null)
        {
            current.Slot.Destroy();
        }
        _currentPanel.Target = null;
    }
}

/// <summary>
/// Mask component for clipping child content.
/// </summary>
[ComponentCategory("HelioUI")]
public class HelioMask : Component
{
    /// <summary>
    /// Whether to show the mask graphic.
    /// </summary>
    public Sync<bool> ShowMaskGraphic { get; private set; }

    public override void OnAwake()
    {
        base.OnAwake();
        ShowMaskGraphic = new Sync<bool>(this, false);
    }
}
