namespace Lumora.Core.HelioUI;

/// <summary>
/// Interface for HelioUI components that define layout metrics.
/// </summary>
public interface IHelioLayoutElement
{
    /// <summary>
    /// Minimum width this element requires.
    /// </summary>
    float MinWidth { get; }

    /// <summary>
    /// Minimum height this element requires.
    /// </summary>
    float MinHeight { get; }

    /// <summary>
    /// Preferred width for this element.
    /// </summary>
    float PreferredWidth { get; }

    /// <summary>
    /// Preferred height for this element.
    /// </summary>
    float PreferredHeight { get; }

    /// <summary>
    /// Flexible width factor (0 = fixed, >0 = can expand).
    /// </summary>
    float FlexibleWidth { get; }

    /// <summary>
    /// Flexible height factor (0 = fixed, >0 = can expand).
    /// </summary>
    float FlexibleHeight { get; }

    /// <summary>
    /// Layout priority. Higher values are processed first.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Ensure layout metrics are valid and up-to-date.
    /// </summary>
    void EnsureValidMetrics();

    /// <summary>
    /// Clear any dirty/changed flags for metrics.
    /// </summary>
    void ClearChangedMetrics();

    /// <summary>
    /// Called when the rect's width changes due to layout.
    /// </summary>
    void LayoutRectWidthChanged();

    /// <summary>
    /// Called when the rect's height changes due to layout.
    /// </summary>
    void LayoutRectHeightChanged();
}
