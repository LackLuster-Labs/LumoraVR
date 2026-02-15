using System;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Components.Gizmos;

/// <summary>
/// The master gizmo for manipulating slots.
/// Displays bounding box, name label, and toolbar buttons.
/// Manages translation, rotation, and scale sub-gizmos.
/// </summary>
[GizmoForComponent(typeof(Slot))]
public class SlotGizmo : ImplementableComponent, IGizmo
{
    /// <summary>
    /// Button size for toolbar buttons.
    /// </summary>
    public const float BUTTON_SIZE = 0.025f;

    /// <summary>
    /// Offset for buttons above the bounding box.
    /// </summary>
    public const float BUTTONS_OFFSET = 0.025f;

    /// <summary>
    /// Separation between toolbar buttons.
    /// </summary>
    public const float BUTTON_SEPARATION = 0.005f;

    /// <summary>
    /// The slot this gizmo is manipulating.
    /// </summary>
    public SyncRef<Slot> TargetSlotRef { get; private set; } = null!;

    /// <summary>
    /// Whether this gizmo is active.
    /// </summary>
    public Sync<bool> Active { get; private set; } = null!;

    /// <summary>
    /// Whether the gizmo is in folded (minimal) mode.
    /// </summary>
    public Sync<bool> IsFolded { get; private set; } = null!;

    /// <summary>
    /// Whether to use local space for transformations.
    /// </summary>
    public Sync<bool> IsLocalSpace { get; private set; } = null!;

    /// <summary>
    /// The currently active transform mode (0=translate, 1=rotate, 2=scale).
    /// </summary>
    public Sync<int> ActiveMode { get; private set; } = null!;

    /// <summary>
    /// Reference to the linked inspector (if any).
    /// </summary>
    public SyncRef<Component> LinkedInspector { get; private set; } = null!;

    /// <summary>
    /// Event fired when gizmo mode changes.
    /// </summary>
    public event Action<int> OnModeChanged;

    /// <summary>
    /// Event fired when space mode changes.
    /// </summary>
    public event Action<bool> OnSpaceChanged;

    /// <summary>
    /// Event fired when requesting to open parent gizmo.
    /// </summary>
    public event Action OnOpenParentRequested;

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
        IsFolded = new Sync<bool>(this, false);
        IsLocalSpace = new Sync<bool>(this, true);
        ActiveMode = new Sync<int>(this, 0); // Default to translation
        LinkedInspector = new SyncRef<Component>(this);

        TargetSlotRef.OnTargetChange += _ => NotifyChanged();
        Active.OnChanged += _ => NotifyChanged();
        IsFolded.OnChanged += _ => NotifyChanged();
        IsLocalSpace.OnChanged += OnLocalSpaceChanged;
        ActiveMode.OnChanged += OnActiveModeChanged;
    }

    private void OnLocalSpaceChanged(bool newValue)
    {
        NotifyChanged();
        OnSpaceChanged?.Invoke(newValue);
    }

    private void OnActiveModeChanged(int newMode)
    {
        NotifyChanged();
        OnModeChanged?.Invoke(newMode);
    }

    /// <summary>
    /// Set up this gizmo for a target slot.
    /// </summary>
    public void Setup(Slot targetSlot)
    {
        if (targetSlot == null || targetSlot.IsRootSlot)
            return;

        TargetSlotRef.Target = targetSlot;
        GizmoRegistry.TrackGizmo(targetSlot, this);

        // Position at target
        PositionAtTarget();
    }

    /// <summary>
    /// Position this gizmo at the target slot's position.
    /// </summary>
    private void PositionAtTarget()
    {
        if (TargetSlotRef.Target != null)
        {
            Slot.GlobalPosition = TargetSlotRef.Target.GlobalPosition;
        }
    }

    /// <summary>
    /// Switch to translation mode.
    /// </summary>
    public void SwitchToTranslation()
    {
        ActiveMode.Value = 0;
    }

    /// <summary>
    /// Switch to rotation mode.
    /// </summary>
    public void SwitchToRotation()
    {
        ActiveMode.Value = 1;
    }

    /// <summary>
    /// Switch to scale mode.
    /// </summary>
    public void SwitchToScale()
    {
        ActiveMode.Value = 2;
    }

    /// <summary>
    /// Toggle between local and global space.
    /// </summary>
    public void ToggleSpace()
    {
        IsLocalSpace.Value = !IsLocalSpace.Value;
    }

    /// <summary>
    /// Toggle folded mode.
    /// </summary>
    public void ToggleFolded()
    {
        IsFolded.Value = !IsFolded.Value;
    }

    /// <summary>
    /// Open gizmo for the parent slot.
    /// </summary>
    public void OpenParent()
    {
        var parent = TargetSlotRef.Target?.Parent;
        if (parent != null && !parent.IsRootSlot)
        {
            OnOpenParentRequested?.Invoke();

            // Destroy this gizmo and create one for parent
            var newGizmo = GizmoHelper.SpawnGizmoFor(parent);
            if (newGizmo != null)
            {
                // Transfer linked inspector if any
                if (LinkedInspector.Target != null)
                {
                    newGizmo.LinkedInspector.Target = LinkedInspector.Target;
                }
            }

            Slot.Destroy();
        }
    }

    /// <summary>
    /// Reset the target slot's local position to zero.
    /// </summary>
    public void ResetPosition()
    {
        if (TargetSlotRef.Target != null)
        {
            TargetSlotRef.Target.LocalPosition.Value = float3.Zero;
        }
    }

    /// <summary>
    /// Reset the target slot's local rotation to identity.
    /// </summary>
    public void ResetRotation()
    {
        if (TargetSlotRef.Target != null)
        {
            TargetSlotRef.Target.LocalRotation.Value = floatQ.Identity;
        }
    }

    /// <summary>
    /// Reset the target slot's local scale to one.
    /// </summary>
    public void ResetScale()
    {
        if (TargetSlotRef.Target != null)
        {
            TargetSlotRef.Target.LocalScale.Value = float3.One;
        }
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        PositionAtTarget();
    }

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
