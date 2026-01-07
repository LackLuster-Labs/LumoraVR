using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Progress bar direction options.
/// </summary>
public enum ProgressBarDirection
{
    LeftToRight,
    RightToLeft,
    TopToBottom,
    BottomToTop
}

/// <summary>
/// Helio progress bar component.
/// Displays a visual progress indicator with configurable fill direction and colors.
/// </summary>
[ComponentCategory("HelioUI")]
public class HelioProgressBar : Component
{
    // ===== VALUE =====

    /// <summary>
    /// Current progress value (0-1 normalized).
    /// </summary>
    public Sync<float> Value { get; private set; }

    /// <summary>
    /// Minimum value.
    /// </summary>
    public Sync<float> MinValue { get; private set; }

    /// <summary>
    /// Maximum value.
    /// </summary>
    public Sync<float> MaxValue { get; private set; }

    // ===== VISUAL PROPERTIES =====

    /// <summary>
    /// Fill direction of the progress bar.
    /// </summary>
    public Sync<ProgressBarDirection> Direction { get; private set; }

    /// <summary>
    /// Color of the filled portion.
    /// </summary>
    public Sync<color> FillColor { get; private set; }

    /// <summary>
    /// Color of the unfilled background.
    /// </summary>
    public Sync<color> BackgroundColor { get; private set; }

    /// <summary>
    /// Reference to the fill rect transform.
    /// This rect's size is adjusted based on the current value.
    /// </summary>
    public SyncRef<HelioRectTransform> FillRect { get; private set; }

    /// <summary>
    /// Reference to the background panel.
    /// </summary>
    public SyncRef<HelioPanel> Background { get; private set; }

    /// <summary>
    /// Reference to the fill image.
    /// </summary>
    public SyncRef<HelioImage> FillImage { get; private set; }

    // ===== INITIALIZATION =====

    public override void OnAwake()
    {
        base.OnAwake();

        Value = new Sync<float>(this, 0f);
        MinValue = new Sync<float>(this, 0f);
        MaxValue = new Sync<float>(this, 1f);
        Direction = new Sync<ProgressBarDirection>(this, ProgressBarDirection.LeftToRight);
        FillColor = new Sync<color>(this, new color(0.2f, 0.6f, 1f, 1f));
        BackgroundColor = new Sync<color>(this, new color(0.2f, 0.2f, 0.2f, 1f));

        FillRect = new SyncRef<HelioRectTransform>(this);
        Background = new SyncRef<HelioPanel>(this);
        FillImage = new SyncRef<HelioImage>(this);

        // Update visuals when value changes
        Value.OnChanged += _ => UpdateVisuals();
        MinValue.OnChanged += _ => UpdateVisuals();
        MaxValue.OnChanged += _ => UpdateVisuals();
        Direction.OnChanged += _ => UpdateVisuals();
    }

    // ===== VALUE CALCULATION =====

    /// <summary>
    /// Get the normalized value (0-1).
    /// </summary>
    public float GetNormalizedValue()
    {
        float range = MaxValue.Value - MinValue.Value;
        if (range < 0.0001f) return 0f;
        return System.Math.Clamp((Value.Value - MinValue.Value) / range, 0f, 1f);
    }

    // ===== VISUALS =====

    /// <summary>
    /// Update the visual representation of the progress bar.
    /// Adjusts the fill rect's anchors and size based on current value and direction.
    /// </summary>
    public void UpdateVisuals()
    {
        var fillRectTransform = FillRect.Target;
        if (fillRectTransform == null) return;

        float normalized = GetNormalizedValue();

        // Update fill rect anchors/offsets based on direction
        switch (Direction.Value)
        {
            case ProgressBarDirection.LeftToRight:
                fillRectTransform.AnchorMin.Value = new float2(0f, 0f);
                fillRectTransform.AnchorMax.Value = new float2(normalized, 1f);
                fillRectTransform.OffsetMin.Value = float2.Zero;
                fillRectTransform.OffsetMax.Value = float2.Zero;
                break;

            case ProgressBarDirection.RightToLeft:
                fillRectTransform.AnchorMin.Value = new float2(1f - normalized, 0f);
                fillRectTransform.AnchorMax.Value = new float2(1f, 1f);
                fillRectTransform.OffsetMin.Value = float2.Zero;
                fillRectTransform.OffsetMax.Value = float2.Zero;
                break;

            case ProgressBarDirection.BottomToTop:
                fillRectTransform.AnchorMin.Value = new float2(0f, 0f);
                fillRectTransform.AnchorMax.Value = new float2(1f, normalized);
                fillRectTransform.OffsetMin.Value = float2.Zero;
                fillRectTransform.OffsetMax.Value = float2.Zero;
                break;

            case ProgressBarDirection.TopToBottom:
                fillRectTransform.AnchorMin.Value = new float2(0f, 1f - normalized);
                fillRectTransform.AnchorMax.Value = new float2(1f, 1f);
                fillRectTransform.OffsetMin.Value = float2.Zero;
                fillRectTransform.OffsetMax.Value = float2.Zero;
                break;
        }

        // Update colors if components are assigned
        var fillImage = FillImage.Target;
        if (fillImage != null)
        {
            fillImage.Tint.Value = FillColor.Value;
        }

        var background = Background.Target;
        if (background != null)
        {
            background.BackgroundColor.Value = BackgroundColor.Value;
        }

        // Mark the rect as dirty to trigger recalculation
        fillRectTransform.MarkDirty();
    }

    /// <summary>
    /// Set the progress value directly.
    /// </summary>
    public void SetValue(float value)
    {
        Value.Value = System.Math.Clamp(value, MinValue.Value, MaxValue.Value);
    }

    /// <summary>
    /// Set the progress as a normalized 0-1 value.
    /// </summary>
    public void SetNormalizedValue(float normalized)
    {
        normalized = System.Math.Clamp(normalized, 0f, 1f);
        SetValue(MinValue.Value + normalized * (MaxValue.Value - MinValue.Value));
    }
}
