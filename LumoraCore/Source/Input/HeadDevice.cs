using System.Numerics;

namespace Lumora.Core.Input;

/// <summary>
/// VR headset/head tracking device.
/// </summary>
public class HeadDevice : InputDevice
{
    /// <summary>
    /// Name of the head device (e.g. "Quest 3", "Index HMD").
    /// </summary>
    public string DeviceName { get; set; } = "Unknown HMD";

    /// <summary>
    /// Battery level (0.0 to 1.0). -1 if not available.
    /// </summary>
    public float BatteryLevel { get; set; } = -1f;

    // Transform data
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }
    public Vector3 Velocity { get; set; }
    public Vector3 AngularVelocity { get; set; }

    // Eye tracking (if available)
    public Vector3 LeftEyeGazeDirection { get; set; }
    public Vector3 RightEyeGazeDirection { get; set; }
    public Vector3 CombinedEyeGazeDirection { get; set; }
    public float LeftEyeOpenness { get; set; } = 1f;
    public float RightEyeOpenness { get; set; } = 1f;
    public float PupilDilation { get; set; }

    // IPD (Interpupillary Distance)
    public float IPD { get; set; } = 0.063f; // Default 63mm

    // Tracking confidence
    public float TrackingConfidence { get; set; } = 1f;

    /// <summary>
    /// Check if eye tracking is available.
    /// </summary>
    public bool HasEyeTracking { get; set; }

    /// <summary>
    /// Check if the headset is currently worn.
    /// </summary>
    public bool IsWorn { get; set; } = true;

    /// <summary>
    /// Check if this device is currently tracked.
    /// Default false - VR driver must set to true when headset is actually tracked.
    /// </summary>
    public bool IsTracked { get; set; } = false;

    /// <summary>
    /// Get the forward direction of the head.
    /// </summary>
    public Vector3 Forward => Vector3.Transform(Vector3.UnitZ, Rotation);

    /// <summary>
    /// Get the up direction of the head.
    /// </summary>
    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Rotation);

    /// <summary>
    /// Get the right direction of the head.
    /// </summary>
    public Vector3 Right => Vector3.Transform(Vector3.UnitX, Rotation);
}