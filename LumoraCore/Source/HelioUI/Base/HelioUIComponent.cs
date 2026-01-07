namespace Lumora.Core.HelioUI;

/// <summary>
/// Base class for all HelioUI components.
/// Provides automatic RectTransform management.
/// </summary>
[ComponentCategory("HelioUI")]
public abstract class HelioUIComponent : Component
{
    private HelioRectTransform _rectTransform;

    /// <summary>
    /// The RectTransform associated with this UI component.
    /// Automatically retrieved or created on the same slot.
    /// </summary>
    public HelioRectTransform RectTransform
    {
        get
        {
            if (_rectTransform == null || _rectTransform.IsDestroyed)
            {
                _rectTransform = EnsureRectTransform();
            }
            return _rectTransform;
        }
    }

    /// <summary>
    /// Ensure a RectTransform exists on this slot.
    /// </summary>
    protected HelioRectTransform EnsureRectTransform()
    {
        var rect = Slot.GetComponent<HelioRectTransform>();
        if (rect == null)
        {
            rect = Slot.AttachComponent<HelioRectTransform>();
        }
        return rect;
    }

    public override void OnAwake()
    {
        base.OnAwake();
        // Ensure we have a RectTransform
        _rectTransform = EnsureRectTransform();
    }

    /// <summary>
    /// Get the computed rect for this UI element.
    /// </summary>
    public HelioRect GetRect()
    {
        return RectTransform?.Rect ?? default;
    }

    /// <summary>
    /// Set this element's rect directly (for layout-driven elements).
    /// </summary>
    public void SetRect(HelioRect rect)
    {
        RectTransform?.SetLayoutRect(rect);
    }
}
