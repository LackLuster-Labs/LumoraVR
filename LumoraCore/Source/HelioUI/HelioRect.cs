using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Lightweight 2D rectangle used by the Helio UI system.
/// </summary>
public struct HelioRect
{
    public float2 Min;
    public float2 Size;

    public HelioRect(float2 min, float2 size)
    {
        Min = min;
        Size = size;
    }

    /// <summary>
    /// Maximum corner of the rectangle (Min + Size).
    /// </summary>
    public float2 Max => Min + Size;

    /// <summary>
    /// Get a point inside the rectangle by normalized coordinates (0-1).
    /// </summary>
    public float2 GetPoint(float2 normalized)
    {
        return Min + Size * normalized;
    }

    /// <summary>
    /// Check if a point is inside this rectangle.
    /// </summary>
    public bool Contains(float2 point)
    {
        return point.x >= Min.x && point.x <= Min.x + Size.x &&
               point.y >= Min.y && point.y <= Min.y + Size.y;
    }
}
