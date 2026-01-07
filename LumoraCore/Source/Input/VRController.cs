using System.Numerics;

namespace Lumora.Core.Input;

/// <summary>
/// Which VR controller side.
/// </summary>
public enum VRControllerSide
{
    Left,
    Right
}

/// <summary>
/// VR controller input device.
/// </summary>
public class VRController : InputDevice
{
    public VRControllerSide Side { get; }

    /// <summary>
    /// Name of the controller device (e.g. "Quest Touch Pro", "Index Controller").
    /// </summary>
    public string DeviceName { get; set; } = "Unknown Controller";

    /// <summary>
    /// Battery level (0.0 to 1.0). -1 if not available.
    /// </summary>
    public float BatteryLevel { get; set; } = -1f;

    // Transform data
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }
    public Vector3 Velocity { get; set; }
    public Vector3 AngularVelocity { get; set; }

    // Common VR controller inputs
    public bool TriggerPressed { get; set; }
    public float TriggerValue { get; set; }
    public bool GripPressed { get; set; }
    public float GripValue { get; set; }

    /// <summary>
    /// Alias for TriggerValue (0.0 to 1.0).
    /// </summary>
    public float Trigger => TriggerValue;

    /// <summary>
    /// Alias for GripValue (0.0 to 1.0).
    /// </summary>
    public float Grip => GripValue;

    // Thumbstick/Touchpad
    public Vector2 ThumbstickPosition { get; set; }
    public bool ThumbstickPressed { get; set; }
    public bool ThumbstickTouched { get; set; }

    // Buttons
    public bool PrimaryButtonPressed { get; set; }
    public bool SecondaryButtonPressed { get; set; }
    public bool MenuButtonPressed { get; set; }

    // Haptics
    private float _hapticAmplitude;
    private float _hapticDuration;
    private float _hapticFrequency = 1000f;

    public VRController(VRControllerSide side)
    {
        Side = side;
    }

    /// <summary>
    /// Trigger haptic feedback.
    /// </summary>
    public void TriggerHaptic(float amplitude, float duration, float frequency = 1000f)
    {
        _hapticAmplitude = amplitude;
        _hapticDuration = duration;
        _hapticFrequency = frequency;
    }

    /// <summary>
    /// Get pending haptic feedback (consumed by driver).
    /// </summary>
    public (float amplitude, float duration, float frequency) GetPendingHaptic()
    {
        var result = (_hapticAmplitude, _hapticDuration, _hapticFrequency);
        _hapticAmplitude = 0;
        _hapticDuration = 0;
        return result;
    }

    /// <summary>
    /// Check if this controller is currently tracked.
    /// </summary>
    public bool IsTracked { get; set; }
}