namespace Lumora.Core.HelioUI;

/// <summary>
/// Marker component that indicates this element should be ignored by parent layout groups.
/// The HelioRectTransform already has an IgnoreLayout sync field, but this component
/// provides a clear visual indicator in the inspector that layout is intentionally ignored.
/// </summary>
[ComponentCategory("HelioUI")]
public class HelioIgnoreLayout : Component
{
    public override void OnAwake()
    {
        base.OnAwake();

        // Set the IgnoreLayout flag on the rect transform when this component is added
        var rect = Slot.GetComponent<HelioRectTransform>();
        if (rect != null)
        {
            rect.IgnoreLayout.Value = true;
        }
    }

    public override void OnStart()
    {
        base.OnStart();

        // Ensure the flag remains set
        var rect = Slot.GetComponent<HelioRectTransform>();
        if (rect != null)
        {
            rect.IgnoreLayout.Value = true;
        }
    }
}
