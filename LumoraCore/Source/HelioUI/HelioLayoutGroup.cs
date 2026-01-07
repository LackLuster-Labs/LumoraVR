using System.Collections.Generic;
using System.Linq;
using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Base class for Helio layout groups (horizontal/vertical).
/// </summary>
public abstract class HelioLayoutGroup : Component
{
    private bool _dirty = true;

    public Sync<float4> Padding { get; private set; }
    public Sync<float2> Spacing { get; private set; }

    public override void OnAwake()
    {
        base.OnAwake();
        Padding = new Sync<float4>(this, float4.Zero); // left, top, right, bottom
        Spacing = new Sync<float2>(this, new float2(8f, 8f));
        Padding.OnChanged += _ => MarkDirty();
        Spacing.OnChanged += _ => MarkDirty();
    }

    public override void OnLateUpdate(float delta)
    {
        base.OnLateUpdate(delta);
        if (_dirty)
            RebuildLayout(Slot.GetComponent<HelioRectTransform>());
    }

    /// <summary>
    /// Trigger a layout rebuild on next LateUpdate.
    /// </summary>
    public void MarkDirty()
    {
        _dirty = true;
    }

    /// <summary>
    /// Rebuild layout immediately using the provided parent rect.
    /// </summary>
    public void RebuildLayout(HelioRectTransform parentRect)
    {
        if (parentRect == null)
            return;

        // Ensure the rect is up-to-date before we use it for layout calculations
        // This fixes timing issues where layouts run before their RectTransform recalculates
        parentRect.Recalculate(false);

        ApplyLayout(parentRect);
        _dirty = false;
    }

    protected abstract void ApplyLayout(HelioRectTransform parentRect);

    protected List<(HelioRectTransform rect, HelioLayoutElement element)> GatherChildren()
    {
        var list = new List<(HelioRectTransform, HelioLayoutElement)>();
        foreach (var child in Slot.Children)
        {
            var rect = child.GetComponent<HelioRectTransform>();
            if (rect == null)
                continue;

            var element = child.GetComponent<HelioLayoutElement>();
            if (element != null && element.IgnoreLayout.Value)
                continue;

            list.Add((rect, element));
        }
        return list;
    }
}
