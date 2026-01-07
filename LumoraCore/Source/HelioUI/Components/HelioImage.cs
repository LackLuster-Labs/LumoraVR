using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Image scaling modes for HelioImage.
/// </summary>
public enum ImageScaleMode
{
    /// <summary>Stretch image to fill bounds.</summary>
    Stretch,
    /// <summary>Scale to fit within bounds, preserving aspect ratio.</summary>
    Fit,
    /// <summary>Scale to fill bounds, cropping if needed.</summary>
    Fill,
    /// <summary>Display at original size, no scaling.</summary>
    None
}

/// <summary>
/// Helio image display component.
/// Renders textures with configurable scaling and tinting.
/// </summary>
[ComponentCategory("HelioUI")]
public class HelioImage : Component
{
    /// <summary>
    /// Reference to the texture to display.
    /// Uses string path for engine-agnostic asset reference.
    /// </summary>
    public Sync<string> TexturePath { get; private set; }

    /// <summary>
    /// Color tint applied to the image.
    /// </summary>
    public Sync<color> Tint { get; private set; }

    /// <summary>
    /// How the image is scaled within its bounds.
    /// </summary>
    public Sync<ImageScaleMode> ScaleMode { get; private set; }

    /// <summary>
    /// Whether to preserve aspect ratio when scaling.
    /// </summary>
    public Sync<bool> PreserveAspect { get; private set; }

    /// <summary>
    /// UV rect for displaying a portion of the texture (sprite sheet support).
    /// (x, y) = bottom-left corner, (z, w) = width, height in UV space.
    /// </summary>
    public Sync<float4> UVRect { get; private set; }

    public override void OnAwake()
    {
        base.OnAwake();
        TexturePath = new Sync<string>(this, "");
        Tint = new Sync<color>(this, new color(1f, 1f, 1f, 1f));
        ScaleMode = new Sync<ImageScaleMode>(this, ImageScaleMode.Stretch);
        PreserveAspect = new Sync<bool>(this, true);
        UVRect = new Sync<float4>(this, new float4(0f, 0f, 1f, 1f));

        // Request rebuild on changes
        TexturePath.OnChanged += _ => RequestCanvasRebuild();
        ScaleMode.OnChanged += _ => RequestCanvasRebuild();
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
