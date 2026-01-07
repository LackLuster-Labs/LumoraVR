using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Text alignment options.
/// </summary>
public enum TextAlignment
{
    Left,
    Center,
    Right
}

/// <summary>
/// Text overflow handling options.
/// </summary>
public enum TextOverflow
{
    /// <summary>Clip text at boundary.</summary>
    Clip,
    /// <summary>Show ellipsis for overflow.</summary>
    Ellipsis,
    /// <summary>Wrap text to next line.</summary>
    Wrap
}

/// <summary>
/// Helio text display component.
/// Renders text with configurable styling options.
/// </summary>
[ComponentCategory("HelioUI")]
public class HelioText : Component
{
    /// <summary>
    /// The text content to display.
    /// </summary>
    public Sync<string> Content { get; private set; }

    /// <summary>
    /// Font size in UI units.
    /// </summary>
    public Sync<float> FontSize { get; private set; }

    /// <summary>
    /// Text color.
    /// </summary>
    public Sync<color> Color { get; private set; }

    /// <summary>
    /// Horizontal text alignment.
    /// </summary>
    public Sync<TextAlignment> Alignment { get; private set; }

    /// <summary>
    /// How to handle text that overflows the element bounds.
    /// </summary>
    public Sync<TextOverflow> Overflow { get; private set; }

    /// <summary>
    /// Whether to parse rich text tags (bold, italic, color).
    /// </summary>
    public Sync<bool> RichText { get; private set; }

    /// <summary>
    /// Line height multiplier.
    /// </summary>
    public Sync<float> LineHeight { get; private set; }

    public override void OnAwake()
    {
        base.OnAwake();
        Content = new Sync<string>(this, "");
        FontSize = new Sync<float>(this, 14f);
        Color = new Sync<color>(this, HelioUITheme.TextPrimary);
        Alignment = new Sync<TextAlignment>(this, TextAlignment.Left);
        Overflow = new Sync<TextOverflow>(this, TextOverflow.Clip);
        RichText = new Sync<bool>(this, false);
        LineHeight = new Sync<float>(this, 1.2f);

        // Request canvas rebuild when properties change
        Content.OnChanged += _ => RequestCanvasRebuild();
        FontSize.OnChanged += _ => RequestCanvasRebuild();
        Color.OnChanged += _ => RequestCanvasRebuild();
        Alignment.OnChanged += _ => RequestCanvasRebuild();
        Overflow.OnChanged += _ => RequestCanvasRebuild();
        LineHeight.OnChanged += _ => RequestCanvasRebuild();
    }

    private void RequestCanvasRebuild()
    {
        // Traverse up the slot hierarchy to find a HelioCanvas
        var current = Slot;
        while (current != null)
        {
            var canvas = current.GetComponent<HelioCanvas>();
            if (canvas != null)
            {
                canvas.RequestVisualRebuild(); // Text changes require visual rebuild
                return;
            }
            current = current.Parent;
        }
    }
}
