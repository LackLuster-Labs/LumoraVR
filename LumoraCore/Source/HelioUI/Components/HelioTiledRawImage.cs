using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Tiled raw image component for repeating texture patterns.
/// Extends HelioRawImage with tiling capabilities for seamless texture repetition.
/// </summary>
[ComponentCategory("HelioUI/Graphics")]
public class HelioTiledRawImage : HelioRawImage
{
    /// <summary>
    /// Scale factor for texture tiling.
    /// (x, y) control horizontal and vertical repetition.
    /// Values greater than 1 increase repetition, less than 1 decrease it.
    /// Example: (2, 2) tiles the texture 2x2, (0.5, 0.5) shows half a tile.
    /// </summary>
    public Sync<float2> TileScale { get; private set; }

    /// <summary>
    /// Offset for the tiled texture in UV space.
    /// (x, y) control horizontal and vertical offset.
    /// Useful for scrolling textures or adjusting tiling alignment.
    /// Values wrap around - 0.0 to 1.0 represents one full texture cycle.
    /// </summary>
    public Sync<float2> TileOffset { get; private set; }

    public override void OnAwake()
    {
        base.OnAwake();
        TileScale = new Sync<float2>(this, new float2(1f, 1f));
        TileOffset = new Sync<float2>(this, new float2(0f, 0f));

        // Request rebuild when tiling properties change
        TileScale.OnChanged += _ => RequestCanvasRebuild();
        TileOffset.OnChanged += _ => RequestCanvasRebuild();
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
