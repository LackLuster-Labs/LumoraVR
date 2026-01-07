using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

/// <summary>
/// VR locomotion module:
/// - Left thumbstick: smooth locomotion (forward/back/strafe)
/// - Right thumbstick: snap turn (discrete 30° turns)
/// - A/X button: jump
/// </summary>
public class VRLocomotionModule : ILocomotionModule
{
    private LocomotionController _owner;
    private CharacterController _characterController;
    private InputInterface _inputInterface;

    // Snap turn state - only turn once per thumbstick push
    private bool _snapTurnTriggered;
    private const float SnapTurnAngle = 30f * 3.14159265f / 180f; // 30 degrees in radians
    private const float SnapTurnThreshold = 0.7f; // Thumbstick threshold to trigger
    private const float SnapTurnResetThreshold = 0.3f; // Threshold to reset and allow next turn

    // Jump state (only jump once per button press)
    private bool _jumpTriggered;

    // Movement settings
    private const float DeadZone = 0.1f;

    public void Activate(LocomotionController owner)
    {
        _owner = owner;
        _characterController = owner?.CharacterController;
        _inputInterface = Engine.Current?.InputInterface;
        _snapTurnTriggered = false;
        _jumpTriggered = false;
    }

    public void Deactivate()
    {
        // No special teardown required
    }

    private int _debugCounter = 0;

    public void Update(float delta)
    {
        // Debug every 60 frames
        _debugCounter++;
        bool shouldLog = _debugCounter >= 60;
        if (shouldLog) _debugCounter = 0;

        if (_owner == null)
        {
            if (shouldLog) Lumora.Core.Logging.Logger.Log("[VRLoco] owner null");
            return;
        }
        if (_characterController == null)
        {
            if (shouldLog) Lumora.Core.Logging.Logger.Log("[VRLoco] charController null");
            return;
        }
        if (!_characterController.IsReady)
        {
            if (shouldLog) Lumora.Core.Logging.Logger.Log("[VRLoco] charController not ready");
            return;
        }

        // Ensure we have the latest InputInterface
        if (_inputInterface == null && Engine.Current?.InputInterface != null)
        {
            _inputInterface = Engine.Current.InputInterface;
        }

        if (_inputInterface == null)
        {
            if (shouldLog) Lumora.Core.Logging.Logger.Log("[VRLoco] inputInterface null");
            return;
        }

        var left = _inputInterface.LeftController;
        var right = _inputInterface.RightController;

        // Skip if we have no VR controllers
        if (left == null || right == null)
        {
            if (shouldLog) Lumora.Core.Logging.Logger.Log($"[VRLoco] controllers null L:{left != null} R:{right != null}");
            return;
        }

        // if (shouldLog)
        // {
        // 	Lumora.Core.Logging.Logger.Log($"[VRLoco] thumb L:({left.ThumbstickPosition.X:F2},{left.ThumbstickPosition.Y:F2}) R:({right.ThumbstickPosition.X:F2},{right.ThumbstickPosition.Y:F2})");
        // }

        // === SMOOTH LOCOMOTION (Left Thumbstick) ===
        UpdateMovement(left);

        // === SNAP TURN (Right Thumbstick) ===
        UpdateSnapTurn(right);

        // === JUMP (A/X Button or Right Thumbstick Click) ===
        UpdateJump(left, right);
    }

    private void UpdateMovement(VRController left)
    {
        var axis = left.ThumbstickPosition;
        // Use thumbstick values directly - Y positive = forward
        float2 moveAxis = new float2(axis.X, axis.Y);

        // Apply deadzone
        if (moveAxis.LengthSquared < DeadZone * DeadZone)
        {
            moveAxis = float2.Zero;
        }

        // Normalize if over 1
        if (moveAxis.LengthSquared > 1f)
            moveAxis = moveAxis.Normalized;

        float3 moveDir = float3.Zero;
        if (moveAxis.LengthSquared > 0.001f)
        {
            _owner.GetMovementBasis(out var forward, out var rightDir);
            // Y is forward/back, X is strafe
            moveDir = (forward * moveAxis.y + rightDir * moveAxis.x).Normalized;

            // Debug logging: camera -> joystick -> movement (commented out for performance)
            // var headDevice = Engine.Current?.InputInterface?.GetBodyNode(Input.BodyNode.Head) as ITrackedDevice;
            // Lumora.Core.Logging.Logger.Log($"[VRLoco] HEAD rot:{headDevice?.RawRotation} tracking:{headDevice?.IsTracking}");
            // Lumora.Core.Logging.Logger.Log($"[VRLoco] JOYSTICK raw:({axis.X:F2},{axis.Y:F2}) processed:({moveAxis.x:F2},{moveAxis.y:F2})");
            // Lumora.Core.Logging.Logger.Log($"[VRLoco] BASIS fwd:({forward.x:F2},{forward.y:F2},{forward.z:F2}) right:({rightDir.x:F2},{rightDir.y:F2},{rightDir.z:F2})");
            // Lumora.Core.Logging.Logger.Log($"[VRLoco] MOVE dir:({moveDir.x:F2},{moveDir.y:F2},{moveDir.z:F2})");
        }
        _characterController.SetMovementDirection(moveDir);
    }

    private void UpdateSnapTurn(VRController right)
    {
        float turnAxis = right.ThumbstickPosition.X;

        // Check if we should trigger a snap turn
        if (!_snapTurnTriggered && System.Math.Abs(turnAxis) > SnapTurnThreshold)
        {
            // Perform snap turn using LocomotionController's method
            // This properly tracks yaw state
            float turnDirection = System.Math.Sign(turnAxis);
            _owner.ApplySnapTurn(-turnDirection * SnapTurnAngle);
            _snapTurnTriggered = true;
        }

        // Reset snap turn when thumbstick returns to center
        if (_snapTurnTriggered && System.Math.Abs(turnAxis) < SnapTurnResetThreshold)
        {
            _snapTurnTriggered = false;
        }
    }

    private void UpdateJump(VRController left, VRController right)
    {
        // Jump on A button (primary button on right) or thumbstick click
        bool jumpPressed = right.PrimaryButtonPressed || left.PrimaryButtonPressed;

        if (!_jumpTriggered && jumpPressed)
        {
            _characterController.RequestJump();
            _jumpTriggered = true;
        }

        // Reset when button released
        if (_jumpTriggered && !jumpPressed)
        {
            _jumpTriggered = false;
        }
    }

    public void Dispose()
    {
        _owner = null;
        _characterController = null;
        _inputInterface = null;
    }
}
