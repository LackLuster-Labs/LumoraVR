using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Components;

/// <summary>
/// A plane collider that respawns objects and teleports users back to spawn when they fall through.
/// Objects are returned to their original position, users are teleported to the spawn point.
/// </summary>
[ComponentCategory("Physics")]
public class RespawnPlane : ImplementableComponent
{
    /// <summary>
    /// Size of the respawn plane (X and Z dimensions).
    /// </summary>
    public Sync<float2> Size { get; private set; } = null!;

    /// <summary>
    /// Whether to enforce X/Z bounds when checking the respawn plane.
    /// </summary>
    public Sync<bool> UseBounds { get; private set; } = null!;

    /// <summary>
    /// Height of the respawn plane relative to the parent slot.
    /// </summary>
    public Sync<float> Height { get; private set; } = null!;

    /// <summary>
    /// Visual color for the respawn plane.
    /// </summary>
    public Sync<color> VisualColor { get; private set; } = null!;

    /// <summary>
    /// Debug line color for the respawn bounds.
    /// </summary>
    public Sync<color> DebugColor { get; private set; } = null!;

    /// <summary>
    /// Whether to render the respawn plane visual.
    /// </summary>
    public Sync<bool> ShowVisual { get; private set; } = null!;

    /// <summary>
    /// Whether to render the respawn bounds debug lines.
    /// </summary>
    public Sync<bool> ShowDebug { get; private set; } = null!;

    /// <summary>
    /// Position to teleport users to when they hit the plane.
    /// If not set, uses the world's spawn point.
    /// </summary>
    public Sync<float3> UserRespawnPosition { get; private set; } = null!;

    public override void OnAwake()
    {
        base.OnAwake();
        Size = new Sync<float2>(this, new float2(100f, 100f));
        Size.OnChanged += _ => RunApplyChanges();

        UseBounds = new Sync<bool>(this, true);
        UseBounds.OnChanged += _ => RunApplyChanges();

        Height = new Sync<float>(this, -20f);
        Height.OnChanged += _ =>
        {
            ApplyHeight();
            RunApplyChanges();
        };

        VisualColor = new Sync<color>(this, new color(1f, 0.3f, 0.3f, 0.25f));
        VisualColor.OnChanged += _ => RunApplyChanges();

        DebugColor = new Sync<color>(this, new color(0.9f, 0.2f, 1f, 1f));
        DebugColor.OnChanged += _ => RunApplyChanges();

        ShowVisual = new Sync<bool>(this, true);
        ShowVisual.OnChanged += _ => RunApplyChanges();

        ShowDebug = new Sync<bool>(this, true);
        ShowDebug.OnChanged += _ => RunApplyChanges();

        UserRespawnPosition = new Sync<float3>(this, new float3(0f, 1f, 0f));

        ApplyHeight();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        // Trigger hook update every frame to check for fallen objects
        RunApplyChanges();
    }

    private void ApplyHeight()
    {
        var localPos = Slot.LocalPosition.Value;
        if (System.Math.Abs(localPos.y - Height.Value) > 0.0001f)
        {
            Slot.LocalPosition.Value = new float3(localPos.x, Height.Value, localPos.z);
        }
    }

    /// <summary>
    /// Called by the hook when an object enters the respawn plane.
    /// </summary>
    public void OnObjectEntered(Slot objectSlot)
    {
        if (objectSlot == null || objectSlot.IsDestroyed) return;

        // Check if this is a user
        var userRoot = objectSlot.GetComponentInParents<UserRoot>();
        if (userRoot != null)
        {
            // Teleport user to spawn position
            var spawnPos = UserRespawnPosition.Value;
            userRoot.Slot.GlobalPosition = spawnPos;
            Logging.Logger.Log($"RespawnPlane: Teleported user '{userRoot.ActiveUser?.UserName?.Value ?? "Unknown"}' to spawn");
            return;
        }

        // Check if object has a stored original position
        var respawnData = objectSlot.GetComponent<RespawnData>();
        if (respawnData != null)
        {
            // Reset to original position
            objectSlot.GlobalPosition = respawnData.OriginalPosition.Value;
            objectSlot.GlobalRotation = respawnData.OriginalRotation.Value;

            // Reset velocity if it has a RigidBody
            var rigidBody = objectSlot.GetComponent<RigidBody>();
            if (rigidBody != null)
            {
                rigidBody.LinearVelocity.Value = float3.Zero;
                rigidBody.AngularVelocity.Value = float3.Zero;
            }

            Logging.Logger.Log($"RespawnPlane: Reset '{objectSlot.SlotName.Value}' to original position");
        }
    }
}

/// <summary>
/// Stores the original spawn position/rotation for respawning.
/// Attach this to objects that should respawn when hitting the RespawnPlane.
/// </summary>
[ComponentCategory("Physics")]
public class RespawnData : Component
{
    public Sync<float3> OriginalPosition { get; private set; } = null!;
    public Sync<floatQ> OriginalRotation { get; private set; } = null!;

    public override void OnAwake()
    {
        base.OnAwake();
        OriginalPosition = new Sync<float3>(this, float3.Zero);
        OriginalRotation = new Sync<floatQ>(this, floatQ.Identity);
    }

    /// <summary>
    /// Store the current position as the respawn position.
    /// </summary>
    public void StoreCurrentPosition()
    {
        OriginalPosition.Value = Slot.GlobalPosition;
        OriginalRotation.Value = Slot.GlobalRotation;
    }
}
