using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Drives the size of a HelioRectTransform from external float sources.
/// Useful for syncing UI element sizes with dynamic values or other components.
/// </summary>
[ComponentCategory("HelioUI")]
public class HelioRectSizeDriver : Component
{
    /// <summary>
    /// The rect transform to drive.
    /// </summary>
    public SyncRef<HelioRectTransform> TargetRect { get; private set; }

    /// <summary>
    /// Source for width value. If null, width is not driven.
    /// </summary>
    public SyncRef<Sync<float>> WidthSource { get; private set; }

    /// <summary>
    /// Source for height value. If null, height is not driven.
    /// </summary>
    public SyncRef<Sync<float>> HeightSource { get; private set; }

    /// <summary>
    /// Multiplier applied to source values.
    /// </summary>
    public Sync<float2> Multiplier { get; private set; }

    /// <summary>
    /// Offset added to source values after multiplier.
    /// </summary>
    public Sync<float2> Offset { get; private set; }

    public override void OnAwake()
    {
        base.OnAwake();

        TargetRect = new SyncRef<HelioRectTransform>(this);
        WidthSource = new SyncRef<Sync<float>>(this);
        HeightSource = new SyncRef<Sync<float>>(this);
        Multiplier = new Sync<float2>(this, float2.One);
        Offset = new Sync<float2>(this, float2.Zero);
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        var rect = TargetRect?.Target;
        if (rect == null)
            return;

        float2 newSize = rect.Rect.Size;
        bool sizeChanged = false;

        // Drive width if source is available
        var widthSource = WidthSource?.Target;
        if (widthSource != null)
        {
            float drivenWidth = widthSource.Value * Multiplier.Value.x + Offset.Value.x;
            if (System.MathF.Abs(newSize.x - drivenWidth) > 0.001f)
            {
                newSize.x = drivenWidth;
                sizeChanged = true;
            }
        }

        // Drive height if source is available
        var heightSource = HeightSource?.Target;
        if (heightSource != null)
        {
            float drivenHeight = heightSource.Value * Multiplier.Value.y + Offset.Value.y;
            if (System.MathF.Abs(newSize.y - drivenHeight) > 0.001f)
            {
                newSize.y = drivenHeight;
                sizeChanged = true;
            }
        }

        // Apply the new size by updating offsets
        if (sizeChanged)
        {
            var currentMin = rect.OffsetMin.Value;
            rect.OffsetMax.Value = currentMin + newSize;
        }
    }
}
