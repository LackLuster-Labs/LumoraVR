using Lumora.Core.Math;

namespace Lumora.Core.GodotUI;

/// <summary>
/// A panel container that can hold child UI elements.
/// </summary>
[ComponentCategory("GodotUI")]
public class GodotPanel : GodotUIElement
{
    /// <summary>
    /// Background color of the panel.
    /// </summary>
    public Sync<color> BackgroundColor { get; private set; } = null!;

    /// <summary>
    /// Corner radius for rounded corners.
    /// </summary>
    public Sync<float> CornerRadius { get; private set; } = null!;

    /// <summary>
    /// Border width.
    /// </summary>
    public Sync<float> BorderWidth { get; private set; } = null!;

    /// <summary>
    /// Border color.
    /// </summary>
    public Sync<color> BorderColor { get; private set; } = null!;

    protected override void InitializeSyncMembers()
    {
        base.InitializeSyncMembers();

        BackgroundColor = new Sync<color>(this, new color(0.15f, 0.15f, 0.2f, 1f));
        CornerRadius = new Sync<float>(this, 4f);
        BorderWidth = new Sync<float>(this, 0f);
        BorderColor = new Sync<color>(this, new color(0.3f, 0.3f, 0.4f, 1f));

        BackgroundColor.OnChanged += _ => NotifyChanged();
        CornerRadius.OnChanged += _ => NotifyChanged();
        BorderWidth.OnChanged += _ => NotifyChanged();
        BorderColor.OnChanged += _ => NotifyChanged();
    }
}
