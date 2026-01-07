using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Canvas panel combining a HelioCanvas with a window frame.
/// </summary>
[ComponentCategory("HelioUI/Panel")]
public class HelioCanvasPanel : Component
{
    // ===== REFERENCES =====

    protected SyncRef<HelioCanvas> _canvas;
    protected SyncRef<HelioWindowPanel> _panel;

    /// <summary>
    /// The HelioUI Canvas for 2D rendering.
    /// </summary>
    public HelioCanvas Canvas => _canvas?.Target;

    /// <summary>
    /// The 3D window panel frame.
    /// </summary>
    public HelioWindowPanel Panel => _panel?.Target;

    // ===== PROPERTIES =====

    /// <summary>
    /// Canvas pixel dimensions.
    /// </summary>
    public float2 CanvasSize
    {
        get => _canvas?.Target?.ReferenceSize?.Value ?? new float2(400f, 600f);
        set
        {
            if (_canvas?.Target?.ReferenceSize != null)
                _canvas.Target.ReferenceSize.Value = value;
        }
    }

    /// <summary>
    /// Canvas scale (pixels per world unit).
    /// Higher values = smaller physical size.
    /// </summary>
    public float CanvasScale
    {
        get => _canvas?.Target?.PixelScale?.Value ?? 1500f;
        set
        {
            if (_canvas?.Target?.PixelScale != null)
                _canvas.Target.PixelScale.Value = value;
        }
    }

    /// <summary>
    /// Physical height in world units.
    /// </summary>
    public float PhysicalHeight
    {
        get => CanvasSize.y / CanvasScale;
        set => CanvasScale = CanvasSize.y / value;
    }

    /// <summary>
    /// Physical width in world units.
    /// </summary>
    public float PhysicalWidth
    {
        get => CanvasSize.x / CanvasScale;
        set => CanvasScale = CanvasSize.x / value;
    }

    // ===== INITIALIZATION =====

    public override void OnAwake()
    {
        base.OnAwake();
        _canvas = new SyncRef<HelioCanvas>(this);
        _panel = new SyncRef<HelioWindowPanel>(this);
    }

    public override void OnStart()
    {
        base.OnStart();
        // Only setup if not already initialized
        if (_panel?.Target == null)
            SetupPanel();
    }

    /// <summary>
    /// Initialize the panel immediately (for synchronous setup).
    /// Call this after attaching the component if you need immediate access.
    /// </summary>
    public void Initialize()
    {
        if (_panel?.Target == null)
            SetupPanel();
    }

    /// <summary>
    /// Setup the panel structure.
    /// </summary>
    protected virtual void SetupPanel()
    {
        // Create window panel
        var windowPanel = Slot.AttachComponent<HelioWindowPanel>();
        _panel.Target = windowPanel;

        // Initialize immediately so ContentSlot is available
        windowPanel.Initialize();

        windowPanel.Padding.Value = 0.005f;
        windowPanel.ZPadding.Value = 0.002f;
        windowPanel.Thickness.Value = 0.01f;

        // Create canvas on content slot
        var contentSlot = windowPanel.ContentSlot?.Target;
        if (contentSlot != null)
        {
            var canvas = contentSlot.AttachComponent<HelioCanvas>();
            _canvas.Target = canvas;
            canvas.ReferenceSize.Value = new float2(400f, 600f);
            canvas.PixelScale.Value = 1500f; // ~40cm tall
        }
    }

    /// <summary>
    /// Helper to set scrollable text content.
    /// </summary>
    public HelioText SetText(string content, float fontSize = 16f, TextAlignment align = TextAlignment.Left)
    {
        var canvas = Canvas;
        if (canvas == null) return null;

        var builder = new HelioUIBuilder(canvas.Slot);
        builder.VerticalLayout(spacing: 4f);
        var text = builder.Text(content, fontSize: fontSize);
        if (text != null)
        {
            text.Alignment.Value = align;
        }
        return text;
    }
}
