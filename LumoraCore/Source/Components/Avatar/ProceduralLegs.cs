using System;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Avatar;

/// <summary>
/// Procedural leg animation - plants feet and steps when moving.
/// Simplified approach: feet stay under hips, step forward/back with movement.
/// </summary>
[ComponentCategory("Avatar")]
public class ProceduralLegs : Component
{
    // ===== CONFIGURATION =====

    public float StepDistance { get; set; } = 0.3f;
    public float StepHeight { get; set; } = 0.12f;
    public float StepDuration { get; set; } = 0.2f;
    public float FootSpacing { get; set; } = 0.12f;
    public float ArmSwingAmount { get; set; } = 0.12f;

    // ===== REFERENCES =====

    private GodotIKAvatar? _ikAvatar;
    private UserRoot? _userRoot;

    // ===== FOOT STATE =====

    private float3 _leftFootPos;
    private float3 _rightFootPos;
    private bool _leftStepping;
    private bool _rightStepping;
    private float _leftStepT;
    private float _rightStepT;
    private float3 _leftStepFrom;
    private float3 _rightStepFrom;
    private float3 _leftStepTo;
    private float3 _rightStepTo;

    // ===== TRACKING =====

    private float3 _lastRootPos;
    private float3 _smoothVelocity;
    private bool _initialized;

    public override void OnAwake()
    {
        base.OnAwake();
        _ikAvatar = Slot.GetComponent<GodotIKAvatar>() ?? Slot.Parent?.GetComponentInChildren<GodotIKAvatar>();

        // Search up the hierarchy for UserRoot (it's on the root user slot)
        var searchSlot = Slot;
        while (searchSlot != null && _userRoot == null)
        {
            _userRoot = searchSlot.GetComponent<UserRoot>();
            searchSlot = searchSlot.Parent;
        }
    }

    public override void OnStart()
    {
        base.OnStart();
        if (_ikAvatar == null)
        {
            AquaLogger.Warn("ProceduralLegs: No GodotIKAvatar found");
            return;
        }

        if (_userRoot == null)
        {
            AquaLogger.Warn("ProceduralLegs: No UserRoot found in hierarchy");
        }

        var rootPos = GetRootPosition();
        float groundY = GetGroundY();

        // Initialize feet directly under root, on ground
        _leftFootPos = new float3(rootPos.x - FootSpacing, groundY, rootPos.z);
        _rightFootPos = new float3(rootPos.x + FootSpacing, groundY, rootPos.z);
        _lastRootPos = rootPos;
        _initialized = true;

        var leftTarget = _ikAvatar.LeftFootTarget.Target;
        var rightTarget = _ikAvatar.RightFootTarget.Target;
        AquaLogger.Log($"ProceduralLegs: Initialized - UserRoot={(_userRoot != null ? "found" : "null")}, LeftFootTarget={leftTarget?.SlotName.Value ?? "null"}, RightFootTarget={rightTarget?.SlotName.Value ?? "null"}");
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (!_initialized || _ikAvatar == null) return;

        var rootPos = GetRootPosition();
        float groundY = GetGroundY();

        // Calculate velocity from root movement
        float3 velocity = (rootPos - _lastRootPos) / MathF.Max(delta, 0.001f);
        velocity.y = 0;
        _lastRootPos = rootPos;

        // Smooth velocity
        _smoothVelocity = float3.Lerp(_smoothVelocity, velocity, delta * 10f);
        float speed = _smoothVelocity.Length;

        // Calculate where feet SHOULD be (directly under root)
        float3 leftIdeal = new float3(rootPos.x - FootSpacing, groundY, rootPos.z);
        float3 rightIdeal = new float3(rootPos.x + FootSpacing, groundY, rootPos.z);

        // Add small forward offset based on velocity
        if (speed > 0.1f)
        {
            float3 velDir = _smoothVelocity.Normalized;
            float3 stepOffset = velDir * 0.1f; // Small prediction
            leftIdeal += stepOffset;
            rightIdeal += stepOffset;
        }

        // Update stepping
        UpdateStep(ref _leftStepping, ref _leftStepT, ref _leftFootPos, ref _leftStepFrom, ref _leftStepTo, leftIdeal, groundY, delta, false);
        UpdateStep(ref _rightStepping, ref _rightStepT, ref _rightFootPos, ref _rightStepFrom, ref _rightStepTo, rightIdeal, groundY, delta, true);

        // Check if need new step
        if (!_leftStepping && !_rightStepping)
        {
            float leftDist = HorizDist(_leftFootPos, leftIdeal);
            float rightDist = HorizDist(_rightFootPos, rightIdeal);

            if (leftDist > StepDistance || rightDist > StepDistance)
            {
                // Step the foot that's further behind
                if (leftDist >= rightDist)
                    StartStep(ref _leftStepping, ref _leftStepT, ref _leftStepFrom, ref _leftStepTo, _leftFootPos, leftIdeal);
                else
                    StartStep(ref _rightStepping, ref _rightStepT, ref _rightStepFrom, ref _rightStepTo, _rightFootPos, rightIdeal);
            }
        }

        // Apply to IK targets
        ApplyFootPositions();
        ApplyArmSwing(rootPos, speed);
    }

    private void UpdateStep(ref bool stepping, ref float t, ref float3 footPos, ref float3 from, ref float3 to, float3 ideal, float groundY, float delta, bool isRight)
    {
        if (!stepping) return;

        t += delta / StepDuration;
        if (t >= 1f)
        {
            stepping = false;
            t = 0f;
            footPos = to;
            footPos.y = groundY;
        }
        else
        {
            // Smooth interpolation
            float smooth = t * t * (3f - 2f * t);
            footPos = float3.Lerp(from, to, smooth);

            // Arc up in middle
            float lift = MathF.Sin(t * MathF.PI) * StepHeight;
            footPos.y = groundY + lift;
        }
    }

    private void StartStep(ref bool stepping, ref float t, ref float3 from, ref float3 to, float3 currentPos, float3 targetPos)
    {
        stepping = true;
        t = 0f;
        from = currentPos;
        to = targetPos;
    }

    private float HorizDist(float3 a, float3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private void ApplyFootPositions()
    {
        if (_ikAvatar == null) return;

        var leftSlot = _ikAvatar.LeftFootTarget.Target;
        var rightSlot = _ikAvatar.RightFootTarget.Target;

        if (leftSlot != null)
            leftSlot.GlobalPosition = _leftFootPos;

        if (rightSlot != null)
            rightSlot.GlobalPosition = _rightFootPos;
    }

    private void ApplyArmSwing(float3 rootPos, float speed)
    {
        if (_ikAvatar == null) return;

        var leftHand = _ikAvatar.LeftHandTarget.Target;
        var rightHand = _ikAvatar.RightHandTarget.Target;

        if (leftHand == null && rightHand == null) return;

        // Skip procedural arm swing when hands are actively tracked.
        bool leftTracked = IsHandTracked(leftHand);
        bool rightTracked = IsHandTracked(rightHand);

        if (leftTracked && rightTracked)
            return;

        // Use body-facing basis so arms don't drift in world axes while turning.
        var bodyRot = _userRoot?.HeadFacingRotation ?? Slot.GlobalRotation;
        float3 forward = bodyRot * float3.Backward; // Godot forward is -Z
        forward.y = 0f;
        if (forward.LengthSquared < 1e-6f)
            forward = float3.Backward;
        forward = forward.Normalized;

        float3 right = float3.Cross(forward, float3.Up);
        if (right.LengthSquared < 1e-6f)
            right = float3.Right;
        right = right.Normalized;

        // Calculate swing based on which foot is forward along body direction.
        float swing = 0f;
        if (speed > 0.1f)
        {
            float3 leftDelta = _leftFootPos - rootPos;
            float3 rightDelta = _rightFootPos - rootPos;
            float leftFootZ = float3.Dot(leftDelta, forward);
            float rightFootZ = float3.Dot(rightDelta, forward);
            swing = (rightFootZ - leftFootZ) * 0.5f;
            swing = System.Math.Clamp(swing, -ArmSwingAmount, ArmSwingAmount);
        }

        float handY = rootPos.y; // At hip height
        float handSide = 0.25f;

        if (leftHand != null && !leftTracked)
        {
            var leftPos = rootPos + (-right * handSide) + (forward * swing);
            leftPos.y = handY;
            leftHand.GlobalPosition = leftPos;
        }

        if (rightHand != null && !rightTracked)
        {
            var rightPos = rootPos + (right * handSide) + (forward * -swing);
            rightPos.y = handY;
            rightHand.GlobalPosition = rightPos;
        }
    }

    private static bool IsHandTracked(Slot handSlot)
    {
        if (handSlot == null)
            return false;

        var positioner = handSlot.GetComponent<TrackedDevicePositioner>();
        return positioner != null && positioner.IsTracking.Value;
    }

    private float3 GetRootPosition()
    {
        // Use user root position as the reference (where the player is)
        if (_userRoot != null)
            return _userRoot.Slot.GlobalPosition + new float3(0, 0.9f, 0); // Hip height
        return Slot.GlobalPosition + new float3(0, 0.9f, 0);
    }

    private float GetGroundY()
    {
        if (_userRoot != null)
            return _userRoot.Slot.GlobalPosition.y;
        return Slot.GlobalPosition.y;
    }

    public float3 LeftFootPosition => _leftFootPos;
    public float3 RightFootPosition => _rightFootPos;
    public bool IsLeftFootStepping => _leftStepping;
    public bool IsRightFootStepping => _rightStepping;
}
