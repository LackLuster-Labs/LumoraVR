using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.GodotUI;

/// <summary>
/// Anchor presets for UI positioning.
/// </summary>
public enum AnchorPreset
{
    TopLeft,
    TopCenter,
    TopRight,
    CenterLeft,
    Center,
    CenterRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
    FullRect,
    TopWide,
    BottomWide,
    LeftWide,
    RightWide,
    Custom
}

/// <summary>
/// Base class for all Godot UI elements.
/// </summary>
[ComponentCategory("GodotUI")]
public class GodotUIElement : ImplementableComponent
{
    /// <summary>
    /// Anchor preset for quick positioning.
    /// </summary>
    public Sync<AnchorPreset> Anchor { get; private set; } = null!;

    /// <summary>
    /// Custom anchor values (left, top, right, bottom) - 0 to 1.
    /// </summary>
    public Sync<float4> AnchorRect { get; private set; } = null!;

    /// <summary>
    /// Offset from anchors in pixels (left, top, right, bottom).
    /// </summary>
    public Sync<float4> OffsetRect { get; private set; } = null!;

    /// <summary>
    /// Minimum size of the element.
    /// </summary>
    public Sync<float2> MinSize { get; private set; } = null!;

    /// <summary>
    /// Size flags for horizontal layout.
    /// </summary>
    public Sync<int> SizeFlagsHorizontal { get; private set; } = null!;

    /// <summary>
    /// Size flags for vertical layout.
    /// </summary>
    public Sync<int> SizeFlagsVertical { get; private set; } = null!;

    /// <summary>
    /// Whether the element is visible.
    /// </summary>
    public Sync<bool> Visible { get; private set; } = null!;

    /// <summary>
    /// Modulate color (tint).
    /// </summary>
    public Sync<color> Modulate { get; private set; } = null!;

    /// <summary>
    /// Path to existing Godot node within parent panel's scene.
    /// If set, hook adopts existing node instead of creating new one.
    /// </summary>
    public Sync<string> SceneNodePath { get; private set; } = null!;

    /// <summary>
    /// Reference to parent panel (for scene node lookup).
    /// </summary>
    public SyncRef<GodotUIPanel> ParentPanel { get; private set; } = null!;

    public override void OnAwake()
    {
        base.OnAwake();
        InitializeSyncMembers();
    }

    protected virtual void InitializeSyncMembers()
    {
        Anchor = new Sync<AnchorPreset>(this, AnchorPreset.TopLeft);
        AnchorRect = new Sync<float4>(this, new float4(0, 0, 0, 0));
        OffsetRect = new Sync<float4>(this, new float4(0, 0, 100, 100));
        MinSize = new Sync<float2>(this, float2.Zero);
        SizeFlagsHorizontal = new Sync<int>(this, 1);  // Fill
        SizeFlagsVertical = new Sync<int>(this, 1);    // Fill
        Visible = new Sync<bool>(this, true);
        Modulate = new Sync<color>(this, color.White);
        SceneNodePath = new Sync<string>(this, "");
        ParentPanel = new SyncRef<GodotUIPanel>(this);

        Anchor.OnChanged += _ => NotifyChanged();
        AnchorRect.OnChanged += _ => NotifyChanged();
        OffsetRect.OnChanged += _ => NotifyChanged();
        MinSize.OnChanged += _ => NotifyChanged();
        Visible.OnChanged += _ => NotifyChanged();
        Modulate.OnChanged += _ => NotifyChanged();
    }

    /// <summary>
    /// Set anchors and offsets to fill the entire parent.
    /// </summary>
    public void SetFullRect()
    {
        Anchor.Value = AnchorPreset.FullRect;
        AnchorRect.Value = new float4(0, 0, 1, 1);
        OffsetRect.Value = float4.Zero;
    }

    /// <summary>
    /// Set position and size using pixel coordinates.
    /// </summary>
    public void SetRect(float x, float y, float width, float height)
    {
        Anchor.Value = AnchorPreset.TopLeft;
        AnchorRect.Value = new float4(0, 0, 0, 0);
        OffsetRect.Value = new float4(x, y, x + width, y + height);
    }
}
