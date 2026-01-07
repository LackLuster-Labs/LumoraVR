using Lumora.Core.Math;

namespace Lumora.Core.GodotUI;

/// <summary>
/// Root component for Godot UI. Creates a SubViewport that renders UI to a texture.
/// The texture is then displayed on a 3D quad in the world.
/// </summary>
[ComponentCategory("GodotUI")]
public class GodotUICanvas : ImplementableComponent
{
    /// <summary>
    /// Size of the UI canvas in pixels.
    /// </summary>
    public Sync<float2> Size { get; private set; } = null!;

    /// <summary>
    /// Pixels per unit (affects how large the UI appears in world space).
    /// Higher values = smaller UI in world.
    /// </summary>
    public Sync<float> PixelsPerUnit { get; private set; } = null!;

    /// <summary>
    /// Whether the canvas is interactive (receives input).
    /// </summary>
    public Sync<bool> Interactive { get; private set; } = null!;

    /// <summary>
    /// Background color of the canvas.
    /// </summary>
    public Sync<color> BackgroundColor { get; private set; } = null!;

    /// <summary>
    /// Whether the background is transparent.
    /// </summary>
    public Sync<bool> TransparentBackground { get; private set; } = null!;

    public override void OnAwake()
    {
        base.OnAwake();
        InitializeSyncMembers();
    }

    private void InitializeSyncMembers()
    {
        Size = new Sync<float2>(this, new float2(800, 600));
        PixelsPerUnit = new Sync<float>(this, 1000f);  // 1000 pixels = 1 world unit
        Interactive = new Sync<bool>(this, true);
        BackgroundColor = new Sync<color>(this, new color(0.1f, 0.1f, 0.15f, 1f));
        TransparentBackground = new Sync<bool>(this, false);

        Size.OnChanged += _ => NotifyChanged();
        PixelsPerUnit.OnChanged += _ => NotifyChanged();
        BackgroundColor.OnChanged += _ => NotifyChanged();
        TransparentBackground.OnChanged += _ => NotifyChanged();
    }
}
