using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Type of pointer input device.
/// </summary>
public enum PointerType
{
    /// <summary>Desktop mouse pointer.</summary>
    Mouse,
    /// <summary>Touch input (mobile/touchscreen).</summary>
    Touch,
    /// <summary>VR laser pointer from controller.</summary>
    Laser,
    /// <summary>VR hand tracking finger poke.</summary>
    Hand
}

/// <summary>
/// Event data for Helio UI pointer interactions.
/// Contains position, movement, and source information.
/// </summary>
public class HelioPointerEventData
{
    /// <summary>
    /// Position in canvas-space coordinates.
    /// </summary>
    public float2 Position;

    /// <summary>
    /// Movement delta since the last event.
    /// </summary>
    public float2 Delta;

    /// <summary>
    /// Pointer identifier for multi-touch support.
    /// </summary>
    public int PointerId;

    /// <summary>
    /// Type of input device generating this event.
    /// </summary>
    public PointerType Type;

    /// <summary>
    /// The originating input device or controller.
    /// </summary>
    public object Source;

    /// <summary>
    /// World-space ray origin (for VR laser raycasting).
    /// </summary>
    public float3 RayOrigin;

    /// <summary>
    /// World-space ray direction (for VR laser raycasting).
    /// </summary>
    public float3 RayDirection;

    /// <summary>
    /// Distance from ray origin to hit point.
    /// </summary>
    public float Distance;

    /// <summary>
    /// World-space position of the hit point.
    /// </summary>
    public float3 WorldPosition;

    /// <summary>
    /// Whether the primary button is pressed.
    /// </summary>
    public bool IsPressed;

    /// <summary>
    /// The slot that was hit by this pointer.
    /// </summary>
    public Slot HitSlot;

    /// <summary>
    /// Creates a new pointer event with default values.
    /// </summary>
    public HelioPointerEventData()
    {
        Position = float2.Zero;
        Delta = float2.Zero;
        PointerId = 0;
        Type = PointerType.Mouse;
        Source = null;
        RayOrigin = float3.Zero;
        RayDirection = new float3(0, 0, -1);
        Distance = 0f;
        IsPressed = false;
        HitSlot = null;
    }

    /// <summary>
    /// Creates a copy of this event data.
    /// </summary>
    public HelioPointerEventData Clone()
    {
        return new HelioPointerEventData
        {
            Position = Position,
            Delta = Delta,
            PointerId = PointerId,
            Type = Type,
            Source = Source,
            RayOrigin = RayOrigin,
            RayDirection = RayDirection,
            Distance = Distance,
            WorldPosition = WorldPosition,
            IsPressed = IsPressed,
            HitSlot = HitSlot
        };
    }
}
