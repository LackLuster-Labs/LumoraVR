namespace Lumora.Core.Math;

/// <summary>
/// Axis-aligned bounding box (AABB).
/// Stores minimum and maximum points.
/// </summary>
public struct BoundingBox
{
    /// <summary>Minimum point of the bounding box</summary>
    public float3 Min;

    /// <summary>Maximum point of the bounding box</summary>
    public float3 Max;

    // ===== Constructors =====

    public BoundingBox(float3 min, float3 max)
    {
        Min = min;
        Max = max;
    }

    // ===== Properties =====

    /// <summary>Center of the bounding box</summary>
    public float3 Center => (Min + Max) * 0.5f;

    /// <summary>Size of the bounding box</summary>
    public float3 Size => Max - Min;

    /// <summary>Half extents of the bounding box</summary>
    public float3 Extents => Size * 0.5f;

    // ===== Methods =====

    /// <summary>
    /// Make the bounding box empty (inverted min/max).
    /// </summary>
    public void MakeEmpty()
    {
        Min = new float3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Max = new float3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
    }

    /// <summary>
    /// Expand the bounding box to include a point.
    /// </summary>
    public void Encapsulate(float3 point)
    {
        Min = new float3(
            System.Math.Min(Min.x, point.x),
            System.Math.Min(Min.y, point.y),
            System.Math.Min(Min.z, point.z)
        );
        Max = new float3(
            System.Math.Max(Max.x, point.x),
            System.Math.Max(Max.y, point.y),
            System.Math.Max(Max.z, point.z)
        );
    }

    /// <summary>
    /// Expand the bounding box to include another bounding box.
    /// </summary>
    public void Encapsulate(BoundingBox other)
    {
        Min = new float3(
            System.Math.Min(Min.x, other.Min.x),
            System.Math.Min(Min.y, other.Min.y),
            System.Math.Min(Min.z, other.Min.z)
        );
        Max = new float3(
            System.Math.Max(Max.x, other.Max.x),
            System.Math.Max(Max.y, other.Max.y),
            System.Math.Max(Max.z, other.Max.z)
        );
    }

    /// <summary>
    /// Check if a point is inside the bounding box.
    /// </summary>
    public bool Contains(float3 point)
    {
        return point.x >= Min.x && point.x <= Max.x &&
               point.y >= Min.y && point.y <= Max.y &&
               point.z >= Min.z && point.z <= Max.z;
    }

    /// <summary>
    /// Check if this bounding box intersects another.
    /// </summary>
    public bool Intersects(BoundingBox other)
    {
        return !(Max.x < other.Min.x || Min.x > other.Max.x ||
                 Max.y < other.Min.y || Min.y > other.Max.y ||
                 Max.z < other.Min.z || Min.z > other.Max.z);
    }
}
