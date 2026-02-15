using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Components.Gizmos;

/// <summary>
/// Abstract base class for all interactive gizmos.
/// Gizmos provide visual manipulation handles for slots and their properties.
/// </summary>
public abstract class Gizmo : ImplementableComponent, IGizmo
{
    /// <summary>
    /// Line radius for gizmo visual elements.
    /// </summary>
    public const float LINE_RADIUS = 0.002f;

    /// <summary>
    /// The slot this gizmo is manipulating.
    /// </summary>
    public SyncRef<Slot> TargetSlotRef { get; private set; } = null!;

    /// <summary>
    /// Whether this gizmo is currently active and visible.
    /// </summary>
    public Sync<bool> Active { get; private set; } = null!;

    /// <summary>
    /// The base color of this gizmo.
    /// </summary>
    public Sync<color> GizmoColor { get; private set; } = null!;

    /// <summary>
    /// Whether this gizmo is currently being interacted with.
    /// </summary>
    public Sync<bool> IsInteracting { get; private set; } = null!;

    /// <summary>
    /// IGizmo interface implementation.
    /// </summary>
    public Slot TargetSlot => TargetSlotRef?.Target;

    /// <summary>
    /// IGizmo interface implementation.
    /// </summary>
    bool IGizmo.IsActive
    {
        get => Active?.Value ?? false;
        set
        {
            if (Active != null)
                Active.Value = value;
        }
    }

    public override void OnAwake()
    {
        base.OnAwake();
        InitializeSyncMembers();
    }

    private void InitializeSyncMembers()
    {
        TargetSlotRef = new SyncRef<Slot>(this);
        Active = new Sync<bool>(this, true);
        GizmoColor = new Sync<color>(this, color.Yellow);
        IsInteracting = new Sync<bool>(this, false);

        TargetSlotRef.OnTargetChange += _ => NotifyChanged();
        Active.OnChanged += _ => NotifyChanged();
        GizmoColor.OnChanged += _ => NotifyChanged();
    }

    /// <summary>
    /// Set up this gizmo for a target slot.
    /// </summary>
    public virtual void Setup(Slot targetSlot)
    {
        TargetSlotRef.Target = targetSlot;
        GizmoRegistry.TrackGizmo(targetSlot, this);
    }

    /// <summary>
    /// Position this gizmo at the target slot's position.
    /// </summary>
    protected void PositionAtTarget()
    {
        if (TargetSlotRef.Target != null)
        {
            Slot.GlobalPosition = TargetSlotRef.Target.GlobalPosition;
            Slot.GlobalRotation = TargetSlotRef.Target.GlobalRotation;
        }
    }

    /// <summary>
    /// Set the gizmo color.
    /// </summary>
    public void SetColor(color newColor)
    {
        GizmoColor.Value = newColor;
    }

    /// <summary>
    /// Begin an interaction with this gizmo.
    /// </summary>
    /// <param name="globalPoint">The world-space point where interaction began.</param>
    /// <param name="overrideAnchor">Optional override anchor point.</param>
    /// <returns>True if interaction was started successfully.</returns>
    public virtual bool BeginInteraction(float3 globalPoint, float3? overrideAnchor = null)
    {
        if (IsInteracting.Value)
            return false;

        PositionAtTarget();
        IsInteracting.Value = true;

        float3 localPoint = Slot.GlobalPointToLocal(globalPoint);
        float3? localAnchor = overrideAnchor.HasValue
            ? Slot.GlobalPointToLocal(overrideAnchor.Value)
            : null;

        OnInteractionBegin(localPoint, localAnchor);
        return true;
    }

    /// <summary>
    /// Update the interaction with a new point.
    /// </summary>
    /// <param name="globalPoint">The current world-space point.</param>
    /// <returns>True if update was processed.</returns>
    public virtual bool UpdateInteraction(float3 globalPoint)
    {
        if (!IsInteracting.Value)
            return false;

        PositionAtTarget();
        float3 localPoint = Slot.GlobalPointToLocal(globalPoint);
        float3 computedPoint = ComputePointWithConstraints(localPoint);

        OnInteractionUpdate(computedPoint);
        return true;
    }

    /// <summary>
    /// End the current interaction.
    /// </summary>
    /// <returns>True if interaction was ended successfully.</returns>
    public virtual bool EndInteraction()
    {
        if (!IsInteracting.Value)
            return false;

        PositionAtTarget();
        IsInteracting.Value = false;
        OnInteractionEnd();
        return true;
    }

    /// <summary>
    /// Called when interaction begins. Override to implement gizmo-specific behavior.
    /// </summary>
    /// <param name="localPoint">The interaction start point in gizmo local space.</param>
    /// <param name="localAnchor">Optional anchor point in gizmo local space.</param>
    protected abstract void OnInteractionBegin(float3 localPoint, float3? localAnchor);

    /// <summary>
    /// Compute the constrained point based on gizmo type.
    /// </summary>
    /// <param name="localPoint">The raw local point.</param>
    /// <returns>The constrained point.</returns>
    protected abstract float3 ComputePointWithConstraints(float3 localPoint);

    /// <summary>
    /// Called when interaction is updated. Override to implement gizmo-specific behavior.
    /// </summary>
    /// <param name="localPoint">The constrained point in gizmo local space.</param>
    protected abstract void OnInteractionUpdate(float3 localPoint);

    /// <summary>
    /// Called when interaction ends. Override to implement gizmo-specific behavior.
    /// </summary>
    protected abstract void OnInteractionEnd();

    public override void OnChanges()
    {
        base.OnChanges();
        PositionAtTarget();
    }

    public override void OnDestroy()
    {
        if (TargetSlotRef?.Target != null)
        {
            GizmoRegistry.UntrackGizmo(TargetSlotRef.Target);
        }
        base.OnDestroy();
    }
}
