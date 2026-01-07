using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Mode for aspect ratio fitting.
/// </summary>
public enum AspectMode
{
    /// <summary>Don't enforce aspect ratio.</summary>
    None,
    /// <summary>Width controls height.</summary>
    WidthControlsHeight,
    /// <summary>Height controls width.</summary>
    HeightControlsWidth,
    /// <summary>Fit within parent bounds.</summary>
    FitInParent,
    /// <summary>Fill parent bounds (may exceed on one axis).</summary>
    EnvelopeParent
}

/// <summary>
/// Helio aspect ratio fitter component.
/// Automatically adjusts size to maintain a specific aspect ratio.
/// </summary>
[ComponentCategory("HelioUI")]
public class HelioAspectRatioFitter : Component
{
    /// <summary>
    /// The aspect ratio to maintain (width / height).
    /// </summary>
    public Sync<float> AspectRatio { get; private set; }

    /// <summary>
    /// How to apply the aspect ratio.
    /// </summary>
    public Sync<AspectMode> Mode { get; private set; }

    private bool _dirty = true;

    public override void OnAwake()
    {
        base.OnAwake();

        AspectRatio = new Sync<float>(this, 1f);
        Mode = new Sync<AspectMode>(this, AspectMode.None);

        AspectRatio.OnChanged += _ => MarkDirty();
        Mode.OnChanged += _ => MarkDirty();
    }

    public void MarkDirty()
    {
        _dirty = true;
    }

    public override void OnLateUpdate(float delta)
    {
        base.OnLateUpdate(delta);

        if (_dirty)
        {
            UpdateSize();
            _dirty = false;
        }
    }

    private void UpdateSize()
    {
        if (Mode.Value == AspectMode.None) return;

        var rect = Slot.GetComponent<HelioRectTransform>();
        if (rect == null) return;

        float aspectRatio = AspectRatio.Value;
        if (aspectRatio <= 0) return;

        float2 currentSize = rect.Rect.Size;
        float2 newSize = currentSize;

        switch (Mode.Value)
        {
            case AspectMode.WidthControlsHeight:
                newSize.y = currentSize.x / aspectRatio;
                break;

            case AspectMode.HeightControlsWidth:
                newSize.x = currentSize.y * aspectRatio;
                break;

            case AspectMode.FitInParent:
            case AspectMode.EnvelopeParent:
                // Get parent size
                float2 parentSize = GetParentSize(rect);
                if (parentSize.x <= 0 || parentSize.y <= 0) return;

                float parentAspect = parentSize.x / parentSize.y;

                if (Mode.Value == AspectMode.FitInParent)
                {
                    // Fit inside: use smaller scale
                    if (aspectRatio > parentAspect)
                    {
                        // Width limited
                        newSize.x = parentSize.x;
                        newSize.y = parentSize.x / aspectRatio;
                    }
                    else
                    {
                        // Height limited
                        newSize.y = parentSize.y;
                        newSize.x = parentSize.y * aspectRatio;
                    }
                }
                else // EnvelopeParent
                {
                    // Fill/envelope: use larger scale
                    if (aspectRatio > parentAspect)
                    {
                        // Height limited
                        newSize.y = parentSize.y;
                        newSize.x = parentSize.y * aspectRatio;
                    }
                    else
                    {
                        // Width limited
                        newSize.x = parentSize.x;
                        newSize.y = parentSize.x / aspectRatio;
                    }
                }
                break;
        }

        // Apply new size if changed
        if (!newSize.Equals(currentSize))
        {
            // Maintain center position
            float2 center = rect.Rect.Min + currentSize * 0.5f;
            float2 newMin = center - newSize * 0.5f;

            rect.OffsetMin.Value = newMin;
            rect.OffsetMax.Value = newMin + newSize;
        }
    }

    private float2 GetParentSize(HelioRectTransform rect)
    {
        var parent = Slot.Parent;
        if (parent == null) return float2.Zero;

        var parentRect = parent.GetComponent<HelioRectTransform>();
        if (parentRect != null)
        {
            return parentRect.Rect.Size;
        }

        // Check for canvas
        var canvas = parent.GetComponent<HelioCanvas>();
        if (canvas != null)
        {
            return canvas.ReferenceSize.Value;
        }

        return float2.Zero;
    }
}
