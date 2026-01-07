using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Helio panel component.
/// Visual container with background color and optional border styling.
/// </summary>
[ComponentCategory("HelioUI")]
public class HelioPanel : Component
{
    /// <summary>
    /// Background fill color.
    /// </summary>
    public Sync<color> BackgroundColor { get; private set; }

    /// <summary>
    /// Corner radius for rounded corners (x=topLeft, y=topRight, z=bottomRight, w=bottomLeft).
    /// </summary>
    public Sync<float4> BorderRadius { get; private set; }

    /// <summary>
    /// Border stroke color.
    /// </summary>
    public Sync<color> BorderColor { get; private set; }

    /// <summary>
    /// Border stroke width.
    /// </summary>
    public Sync<float> BorderWidth { get; private set; }

    public override void OnAwake()
    {
        base.OnAwake();
        BackgroundColor = new Sync<color>(this, HelioUITheme.PanelBackground);
        BorderRadius = new Sync<float4>(this, float4.Zero);
        BorderColor = new Sync<color>(this, HelioUITheme.PanelBorder);
        BorderWidth = new Sync<float>(this, 1f);

        // Request rebuild on visual changes
        BackgroundColor.OnChanged += _ => RequestCanvasRebuild();
        BorderRadius.OnChanged += _ => RequestCanvasRebuild();
        BorderColor.OnChanged += _ => RequestCanvasRebuild();
        BorderWidth.OnChanged += _ => RequestCanvasRebuild();
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
                canvas.RequestVisualRebuild(); // Visual changes require mesh rebuild
                return;
            }
            current = current.Parent;
        }
    }
}
