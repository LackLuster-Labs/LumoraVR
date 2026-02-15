using System;

namespace Lumora.Core;

/// <summary>
/// Specifies a numeric range for a field, enabling slider UI in inspectors.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class RangeAttribute : Attribute
{
    /// <summary>
    /// Minimum value of the range.
    /// </summary>
    public float Min { get; }

    /// <summary>
    /// Maximum value of the range.
    /// </summary>
    public float Max { get; }

    /// <summary>
    /// Format string for display (e.g., "F2" for 2 decimal places, "P0" for percentage).
    /// </summary>
    public string TextFormat { get; }

    /// <summary>
    /// Step value for the slider (0 = auto).
    /// </summary>
    public float Step { get; }

    /// <summary>
    /// Whether to use exponential scaling (useful for very large ranges).
    /// </summary>
    public bool Exponential { get; }

    /// <summary>
    /// Create a range attribute with min/max values.
    /// </summary>
    public RangeAttribute(float min, float max, string textFormat = "F2", float step = 0f, bool exponential = false)
    {
        Min = min;
        Max = max;
        TextFormat = textFormat;
        Step = step;
        Exponential = exponential;
    }

    /// <summary>
    /// Create a range attribute with integer min/max values.
    /// </summary>
    public RangeAttribute(int min, int max, string textFormat = "F0", float step = 1f, bool exponential = false)
    {
        Min = min;
        Max = max;
        TextFormat = textFormat;
        Step = step;
        Exponential = exponential;
    }
}

/// <summary>
/// Marks a field as read-only in the inspector.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class ReadOnlyAttribute : Attribute
{
}

/// <summary>
/// Provides a tooltip/description for a field in the inspector.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class TooltipAttribute : Attribute
{
    public string Text { get; }

    public TooltipAttribute(string text)
    {
        Text = text;
    }
}

/// <summary>
/// Groups fields together in the inspector under a collapsible header.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class GroupAttribute : Attribute
{
    public string Name { get; }

    public GroupAttribute(string name)
    {
        Name = name;
    }
}

/// <summary>
/// Adds a space before this field in the inspector.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class SpaceAttribute : Attribute
{
    public float Height { get; }

    public SpaceAttribute(float height = 10f)
    {
        Height = height;
    }
}

/// <summary>
/// Adds a header label before this field in the inspector.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class HeaderAttribute : Attribute
{
    public string Text { get; }

    public HeaderAttribute(string text)
    {
        Text = text;
    }
}
