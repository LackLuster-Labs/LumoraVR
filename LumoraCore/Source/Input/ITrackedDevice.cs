using Lumora.Core.Math;

namespace Lumora.Core.Input;

/// <summary>
/// Interface for tracked VR devices (headset, controllers, trackers).
/// </summary>
public interface ITrackedDevice : IInputDevice
{
    /// <summary>
    /// The body node this device corresponds to.
    /// </summary>
    BodyNode CorrespondingBodyNode { get; }

    /// <summary>
    /// Whether this device is currently being tracked.
    /// </summary>
    bool IsTracking { get; }

    /// <summary>
    /// Priority of this device (higher priority wins when multiple devices track the same body node).
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// The tracking space this device operates in.
    /// </summary>
    TrackingSpace TrackingSpace { get; }

    /// <summary>
    /// Raw position before tracking space transformation.
    /// </summary>
    float3 RawPosition { get; }

    /// <summary>
    /// Raw rotation before tracking space transformation.
    /// </summary>
    floatQ RawRotation { get; }

    /// <summary>
    /// Transformed position in world space.
    /// </summary>
    float3 Position { get; }

    /// <summary>
    /// Transformed rotation in world space.
    /// </summary>
    floatQ Rotation { get; }

    /// <summary>
    /// Offset from device position to body node position.
    /// </summary>
    float3 BodyNodePositionOffset { get; }

    /// <summary>
    /// Offset from device rotation to body node rotation.
    /// </summary>
    floatQ BodyNodeRotationOffset { get; }
}

/// <summary>
/// Tracking space for coordinate transformation.
/// </summary>
public class TrackingSpace
{
    public float3 Position { get; set; } = float3.Zero;
    public floatQ Rotation { get; set; } = floatQ.Identity;
    public float Scale { get; set; } = 1f;

    /// <summary>
    /// Transform a position from tracking space to world space.
    /// </summary>
    public float3 Transform(float3 position)
    {
        return Position + Rotation * (position * Scale);
    }

    /// <summary>
    /// Transform a rotation from tracking space to world space.
    /// </summary>
    public floatQ Transform(floatQ rotation)
    {
        return Rotation * rotation;
    }
}
