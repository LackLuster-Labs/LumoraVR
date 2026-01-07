using Lumora.Core.Math;
using Lumora.Core.Physics;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Rigid body physics component.
/// Enables physics simulation on a slot - responds to forces, gravity, and collisions.
/// Requires a Collider component on the same slot to define the collision shape.
/// </summary>
[ComponentCategory("Physics")]
public class RigidBody : ImplementableComponent
{
    // ===== SYNC FIELDS =====

    /// <summary>Mass in kilograms</summary>
    public readonly Sync<float> Mass;

    /// <summary>Linear velocity</summary>
    public readonly Sync<float3> LinearVelocity;

    /// <summary>Angular velocity (radians per second)</summary>
    public readonly Sync<float3> AngularVelocity;

    /// <summary>Whether gravity affects this body</summary>
    public readonly Sync<bool> UseGravity;

    /// <summary>Whether the body is kinematic (driven by animation, not physics)</summary>
    public readonly Sync<bool> IsKinematic;

    /// <summary>Linear damping (drag)</summary>
    public readonly Sync<float> LinearDamping;

    /// <summary>Angular damping</summary>
    public readonly Sync<float> AngularDamping;

    /// <summary>Freeze position on specific axes</summary>
    public readonly Sync<bool> FreezePositionX;
    public readonly Sync<bool> FreezePositionY;
    public readonly Sync<bool> FreezePositionZ;

    /// <summary>Freeze rotation on specific axes</summary>
    public readonly Sync<bool> FreezeRotationX;
    public readonly Sync<bool> FreezeRotationY;
    public readonly Sync<bool> FreezeRotationZ;

    // ===== RUNTIME STATE (set by hook) =====

    /// <summary>Is the body currently sleeping (not moving)</summary>
    public bool IsSleeping { get; set; }

    /// <summary>Is the body currently colliding with something</summary>
    public bool IsColliding { get; set; }

    // ===== INITIALIZATION =====

    public RigidBody()
    {
        Mass = new Sync<float>(this, 1f);
        LinearVelocity = new Sync<float3>(this, float3.Zero);
        AngularVelocity = new Sync<float3>(this, float3.Zero);
        UseGravity = new Sync<bool>(this, true);
        IsKinematic = new Sync<bool>(this, false);
        LinearDamping = new Sync<float>(this, 0.05f);
        AngularDamping = new Sync<float>(this, 0.05f);
        FreezePositionX = new Sync<bool>(this, false);
        FreezePositionY = new Sync<bool>(this, false);
        FreezePositionZ = new Sync<bool>(this, false);
        FreezeRotationX = new Sync<bool>(this, false);
        FreezeRotationY = new Sync<bool>(this, false);
        FreezeRotationZ = new Sync<bool>(this, false);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        AquaLogger.Log($"RigidBody: Initialized on '{Slot.SlotName.Value}' with Mass={Mass.Value}kg");

        var respawnData = Slot.GetComponent<RespawnData>();
        if (respawnData == null)
        {
            respawnData = Slot.AttachComponent<RespawnData>();
            respawnData.StoreCurrentPosition();
        }
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        RunApplyChanges();
    }

    // ===== FORCE METHODS =====

    /// <summary>
    /// Apply a force at the center of mass.
    /// </summary>
    public void AddForce(float3 force)
    {
        PendingForce += force;
    }

    /// <summary>
    /// Apply an impulse at the center of mass (instantaneous velocity change).
    /// </summary>
    public void AddImpulse(float3 impulse)
    {
        PendingImpulse += impulse;
    }

    /// <summary>
    /// Apply torque to rotate the body.
    /// </summary>
    public void AddTorque(float3 torque)
    {
        PendingTorque += torque;
    }

    // Pending forces (consumed by hook each frame)
    public float3 PendingForce;
    public float3 PendingImpulse;
    public float3 PendingTorque;

    /// <summary>
    /// Clear pending forces (called by hook after applying them).
    /// </summary>
    public void ClearPendingForces()
    {
        PendingForce = float3.Zero;
        PendingImpulse = float3.Zero;
        PendingTorque = float3.Zero;
    }
}
