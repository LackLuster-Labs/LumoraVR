using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Raw image/texture display component for HelioUI.
/// Renders textures with configurable tinting, UV mapping, and aspect preservation.
/// </summary>
[ComponentCategory("HelioUI/Graphics")]
public class HelioRawImage : Component
{
    /// <summary>
    /// Reference to the texture to display.
    /// Uses string path for engine-agnostic asset reference.
    /// </summary>
    public Sync<string> TexturePath { get; private set; }

    /// <summary>
    /// Color tint applied to the raw image.
    /// Defaults to white (1, 1, 1, 1) for no tinting.
    /// </summary>
    public Sync<color> Tint { get; private set; }

    /// <summary>
    /// UV rectangle for texture coordinate mapping.
    /// (x, y) = minimum UV coordinates, (z, w) = maximum UV coordinates.
    /// Defaults to (0, 0, 1, 1) for full texture display.
    /// Useful for sprite sheets, texture atlases, and partial texture display.
    /// </summary>
    public Sync<float4> UVRect { get; private set; }

    /// <summary>
    /// Whether to preserve the texture's aspect ratio when scaling.
    /// If true, the image will maintain its original proportions within the bounds.
    /// If false, the image will stretch to fill the available space.
    /// </summary>
    public Sync<bool> PreserveAspect { get; private set; }

    public override void OnAwake()
    {
        base.OnAwake();
        TexturePath = new Sync<string>(this, "");
        Tint = new Sync<color>(this, new color(1f, 1f, 1f, 1f));
        UVRect = new Sync<float4>(this, new float4(0f, 0f, 1f, 1f));
        PreserveAspect = new Sync<bool>(this, true);

        // Request rebuild when texture or UV properties change
        TexturePath.OnChanged += _ => RequestCanvasRebuild();
        UVRect.OnChanged += _ => RequestCanvasRebuild();
        PreserveAspect.OnChanged += _ => RequestCanvasRebuild();
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
