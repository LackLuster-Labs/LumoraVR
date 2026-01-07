using Godot;

namespace Aquamarine.Source.Input;

public interface IInputProvider
{
    public static IInputProvider Instance;

    public bool IsVR { get; }

    public enum InputLimb
    {
        Head,
        LeftHand,
        RightHand,
        Hip,
        LeftFoot,
        RightFoot,
    }
    public Vector3 GetPlayspaceMovementDelta { get; }
    public Vector2 GetMovementInputAxis { get; }
    public bool GetJumpInput { get; }
    public bool GetSprintInput { get; }
    public float GetHeight { get; }
    public Vector3 GetLimbPosition(InputLimb limb);
    public Quaternion GetLimbRotation(InputLimb limb);
    public void MoveTransform(Transform3D transform);

    // Controller button inputs
    public bool GetLeftGripInput { get; }
    public bool GetRightGripInput { get; }
    public bool GetLeftTriggerInput { get; }
    public bool GetRightTriggerInput { get; }
    public bool GetLeftSecondaryInput { get; } // A/X button
    public bool GetRightSecondaryInput { get; } // B/Y button

    public static Vector3 PlayspaceMovementDelta => Instance?.GetPlayspaceMovementDelta ?? Vector3.Zero;
    public static Vector2 MovementInputAxis => Instance?.GetMovementInputAxis ?? Vector2.Zero;
    public static bool JumpInput => Instance?.GetJumpInput ?? false;
    public static bool SprintInput => Instance?.GetSprintInput ?? false;
    public static float Height => Instance?.GetHeight ?? 1.8f;
    public static Vector3 LimbPosition(InputLimb limb) => Instance?.GetLimbPosition(limb) ?? Vector3.Zero;
    public static Quaternion LimbRotation(InputLimb limb) => Instance?.GetLimbRotation(limb) ?? Quaternion.Identity;

    public static (Vector3 pos, Quaternion rot) LimbTransform(InputLimb limb) => (LimbPosition(limb), LimbRotation(limb));
    public static void Move(Transform3D transform) => Instance?.MoveTransform(transform);

    // Static accessors for button inputs
    public static bool LeftGripInput => Instance?.GetLeftGripInput ?? false;
    public static bool RightGripInput => Instance?.GetRightGripInput ?? false;
    public static bool LeftTriggerInput => Instance?.GetLeftTriggerInput ?? false;
    public static bool RightTriggerInput => Instance?.GetRightTriggerInput ?? false;
    public static bool LeftSecondaryInput => Instance?.GetLeftSecondaryInput ?? false;
    public static bool RightSecondaryInput => Instance?.GetRightSecondaryInput ?? false;
}

