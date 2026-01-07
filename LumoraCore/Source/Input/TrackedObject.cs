using Lumora.Core.Math;

namespace Lumora.Core.Input;

/// <summary>
/// A tracked object that provides position and rotation from VR tracking.
/// </summary>
public class TrackedObject : InputDevice, ITrackedDevice
{
    private float3 _rawPosition = float3.Zero;
    private floatQ _rawRotation = floatQ.Identity;

    /// <summary>
    /// The body node this tracked object corresponds to.
    /// </summary>
    public BodyNode CorrespondingBodyNode { get; set; } = BodyNode.NONE;

    /// <summary>
    /// The tracking space for coordinate transformation.
    /// </summary>
    public TrackingSpace TrackingSpace { get; set; }

    /// <summary>
    /// Whether this device is currently tracking.
    /// </summary>
    public bool IsTracking { get; set; }

    /// <summary>
    /// Priority for body node assignment (higher wins).
    /// </summary>
    public int Priority { get; set; } = 0;

    /// <summary>
    /// Raw position before tracking space transformation.
    /// </summary>
    public float3 RawPosition
    {
        get => _rawPosition;
        set => _rawPosition = value;
    }

    /// <summary>
    /// Raw rotation before tracking space transformation.
    /// </summary>
    public floatQ RawRotation
    {
        get => _rawRotation;
        set => _rawRotation = value;
    }

    /// <summary>
    /// Position transformed by tracking space.
    /// </summary>
    public float3 Position => TrackingSpace?.Transform(RawPosition) ?? RawPosition;

    /// <summary>
    /// Rotation transformed by tracking space.
    /// </summary>
    public floatQ Rotation => TrackingSpace?.Transform(RawRotation) ?? RawRotation;

    /// <summary>
    /// Offset from device position to body node position.
    /// </summary>
    public float3 BodyNodePositionOffset { get; set; } = float3.Zero;

    /// <summary>
    /// Offset from device rotation to body node rotation.
    /// </summary>
    public floatQ BodyNodeRotationOffset { get; set; } = floatQ.Identity;
}
