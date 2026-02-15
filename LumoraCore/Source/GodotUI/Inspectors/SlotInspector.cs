using System;
using Lumora.Core.Assets;
using Lumora.Core.Components.Gizmos;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.GodotUI.Inspectors;

/// <summary>
/// Slot inspector panel that shows slot hierarchy and properties.
/// Extends GodotUIPanel for 3D rendering.
/// </summary>
[ComponentCategory("GodotUI/Inspectors")]
public class SlotInspector : GodotUIPanel
{
    protected override string DefaultScenePath => LumAssets.UI.SlotInspector;
    protected override float2 DefaultSize => new(400, 500);

    /// <summary>
    /// The root slot being inspected (shows hierarchy from this point).
    /// </summary>
    public SyncRef<Slot> TargetSlot { get; private set; } = null!;

    /// <summary>
    /// The currently selected slot in the hierarchy.
    /// </summary>
    public SyncRef<Slot> SelectedSlot { get; private set; } = null!;

    /// <summary>
    /// Whether to show components for the selected slot.
    /// </summary>
    public Sync<bool> ShowComponents { get; private set; } = null!;

    /// <summary>
    /// Whether to show the hierarchy tree (false = show only selected slot properties).
    /// </summary>
    public Sync<bool> ShowHierarchy { get; private set; } = null!;

    /// <summary>
    /// Reference to the linked gizmo (if any).
    /// </summary>
    public SyncRef<SlotGizmo> LinkedGizmo { get; private set; } = null!;

    /// <summary>
    /// Current depth in the hierarchy for nested inspectors.
    /// </summary>
    public Sync<int> Depth { get; private set; } = null!;

    /// <summary>
    /// Event fired when a slot is selected in the tree.
    /// </summary>
    public event Action<Slot?>? OnSlotSelected;

    /// <summary>
    /// Event fired when a slot is expanded/collapsed in the tree.
    /// </summary>
    public event Action<Slot, bool>? OnSlotExpandedChanged;

    /// <summary>
    /// Event fired when requesting to open gizmo for a slot.
    /// </summary>
    public event Action<Slot>? OnOpenGizmoRequested;

    /// <summary>
    /// Event fired when a property is edited.
    /// </summary>
    public event Action<string, object?>? OnPropertyEdited;

    /// <summary>
    /// Event fired when a child slot is added.
    /// </summary>
    public event Action<Slot, Slot>? OnChildSlotAdded;

    /// <summary>
    /// Event fired when a slot is duplicated.
    /// </summary>
    public event Action<Slot, Slot>? OnSlotDuplicated;

    /// <summary>
    /// Event fired when a slot is destroyed.
    /// </summary>
    public event Action<Slot>? OnSlotDestroyed;

    /// <summary>
    /// Event fired when component attacher is requested.
    /// </summary>
    public event Action<Slot>? OnAttachComponentRequested;

    private Slot? _boundTarget;
    private Slot? _boundSelected;

    public override void OnAwake()
    {
        base.OnAwake();
        InitializeInspectorSyncMembers();
    }

    private void InitializeInspectorSyncMembers()
    {
        TargetSlot = new SyncRef<Slot>(this);
        SelectedSlot = new SyncRef<Slot>(this);
        ShowComponents = new Sync<bool>(this, true);
        ShowHierarchy = new Sync<bool>(this, true);
        LinkedGizmo = new SyncRef<SlotGizmo>(this);
        Depth = new Sync<int>(this, 0);

        TargetSlot.OnTargetChange += OnTargetSlotChanged;
        SelectedSlot.OnTargetChange += OnSelectedSlotChanged;
        ShowComponents.OnChanged += _ => NotifyChanged();
        ShowHierarchy.OnChanged += _ => NotifyChanged();
    }

    private void OnTargetSlotChanged(SyncRef<Slot> syncRef)
    {
        // Unsubscribe from old target
        if (_boundTarget != null)
        {
            _boundTarget.OnChildAdded -= HandleChildAdded;
            _boundTarget.OnChildRemoved -= HandleChildRemoved;
            _boundTarget.OnNameChanged -= HandleNameChanged;
        }

        _boundTarget = TargetSlot.Target;

        // Subscribe to new target
        if (_boundTarget != null)
        {
            _boundTarget.OnChildAdded += HandleChildAdded;
            _boundTarget.OnChildRemoved += HandleChildRemoved;
            _boundTarget.OnNameChanged += HandleNameChanged;

            // When target changes, select it by default
            if (SelectedSlot.Target == null)
            {
                SelectedSlot.Target = _boundTarget;
            }
        }

        NotifyChanged();
    }

    private void OnSelectedSlotChanged(SyncRef<Slot> syncRef)
    {
        // Update gizmo for new selection
        if (_boundSelected != null && LinkedGizmo.Target != null)
        {
            if (LinkedGizmo.Target.TargetSlot == _boundSelected)
            {
                GizmoHelper.DestroyGizmo(_boundSelected);
                LinkedGizmo.Target = null;
            }
        }

        _boundSelected = SelectedSlot.Target;

        OnSlotSelected?.Invoke(SelectedSlot.Target);
        NotifyChanged();
    }

    private void HandleChildAdded(Slot parent, Slot child)
    {
        NotifyChanged();
    }

    private void HandleChildRemoved(Slot parent, Slot child)
    {
        // If removed slot was selected, select parent
        if (SelectedSlot.Target == child)
        {
            SelectedSlot.Target = parent;
        }
        NotifyChanged();
    }

    private void HandleNameChanged(Slot slot, string newName)
    {
        NotifyChanged();
    }

    /// <summary>
    /// Select a slot in the inspector.
    /// </summary>
    public void SelectSlot(Slot? slot)
    {
        if (slot == null) return;
        SelectedSlot.Target = slot;
    }

    /// <summary>
    /// Navigate to a child slot.
    /// </summary>
    public void NavigateToChild(Slot? child)
    {
        if (child == null) return;
        SelectedSlot.Target = child;
    }

    /// <summary>
    /// Navigate to parent slot.
    /// </summary>
    public void NavigateToParent()
    {
        var parent = SelectedSlot.Target?.Parent;
        if (parent != null && !parent.IsRootSlot)
        {
            SelectedSlot.Target = parent;
        }
    }

    /// <summary>
    /// Navigate root up one level.
    /// </summary>
    public void NavigateRootUp()
    {
        if (TargetSlot.Target != null && !TargetSlot.Target.IsRootSlot)
        {
            TargetSlot.Target = TargetSlot.Target.Parent;
        }
    }

    /// <summary>
    /// Set current selection as new hierarchy root.
    /// </summary>
    public void SetSelectionAsRoot()
    {
        if (SelectedSlot.Target != null)
        {
            TargetSlot.Target = SelectedSlot.Target;
        }
    }

    /// <summary>
    /// Add a child slot to the selected slot.
    /// </summary>
    public Slot? AddChildSlot()
    {
        if (SelectedSlot.Target == null) return null;

        var parent = SelectedSlot.Target;
        var child = parent.AddSlot($"{parent.Name.Value} - Child");

        OnChildSlotAdded?.Invoke(parent, child);
        NotifyChanged();

        return child;
    }

    /// <summary>
    /// Duplicate the selected slot.
    /// </summary>
    public Slot? DuplicateSelection()
    {
        if (SelectedSlot.Target == null || SelectedSlot.Target.IsRootSlot) return null;

        var original = SelectedSlot.Target;
        var duplicate = original.Duplicate();

        if (duplicate != null)
        {
            OnSlotDuplicated?.Invoke(original, duplicate);
            SelectedSlot.Target = duplicate;
        }

        return duplicate;
    }

    /// <summary>
    /// Destroy the selected slot.
    /// </summary>
    public void DestroySelection()
    {
        if (SelectedSlot.Target == null || SelectedSlot.Target.IsRootSlot) return;

        var toDestroy = SelectedSlot.Target;
        var parent = toDestroy.Parent;

        OnSlotDestroyed?.Invoke(toDestroy);

        // Move selection to parent before destroying
        SelectedSlot.Target = parent;

        toDestroy.Destroy();
    }

    /// <summary>
    /// Open the component attacher for the selected slot.
    /// </summary>
    public void OpenComponentAttacher()
    {
        if (SelectedSlot.Target == null) return;
        OnAttachComponentRequested?.Invoke(SelectedSlot.Target);
    }

    /// <summary>
    /// Open gizmo for the selected slot.
    /// </summary>
    public void OpenGizmoForSelected()
    {
        if (SelectedSlot.Target != null)
        {
            OnOpenGizmoRequested?.Invoke(SelectedSlot.Target);

            // Update linked gizmo
            var gizmo = GizmoHelper.SpawnGizmoFor(SelectedSlot.Target);
            if (gizmo != null)
            {
                LinkedGizmo.Target = gizmo;
                gizmo.LinkedInspector.Target = this;
            }
        }
    }

    /// <summary>
    /// Setup the inspector with a target slot.
    /// </summary>
    public void Setup(Slot target, int depth = 0)
    {
        TargetSlot.Target = target;
        Depth.Value = depth;
        SelectedSlot.Target = target;
    }

    public override void HandleButtonPress(string buttonPath)
    {
        if (buttonPath.EndsWith("ParentButton") || buttonPath.EndsWith("RootUpButton"))
        {
            NavigateRootUp();
            return;
        }

        if (buttonPath.EndsWith("SetRootButton"))
        {
            SetSelectionAsRoot();
            return;
        }

        if (buttonPath.EndsWith("GizmoButton"))
        {
            OpenGizmoForSelected();
            return;
        }

        if (buttonPath.EndsWith("AddChildButton"))
        {
            AddChildSlot();
            return;
        }

        if (buttonPath.EndsWith("DuplicateButton"))
        {
            DuplicateSelection();
            return;
        }

        if (buttonPath.EndsWith("DestroyButton"))
        {
            DestroySelection();
            return;
        }

        if (buttonPath.EndsWith("AttachComponentButton"))
        {
            OpenComponentAttacher();
            return;
        }

        if (buttonPath.EndsWith("ComponentsToggle"))
        {
            ShowComponents.Value = !ShowComponents.Value;
            return;
        }

        if (buttonPath.EndsWith("HierarchyToggle"))
        {
            ShowHierarchy.Value = !ShowHierarchy.Value;
            return;
        }

        base.HandleButtonPress(buttonPath);
    }

    public override void Close()
    {
        // Clean up linked gizmo when inspector closes
        if (LinkedGizmo.Target != null)
        {
            GizmoHelper.DestroyGizmo(LinkedGizmo.Target.TargetSlot);
        }

        // Unsubscribe from events
        if (_boundTarget != null)
        {
            _boundTarget.OnChildAdded -= HandleChildAdded;
            _boundTarget.OnChildRemoved -= HandleChildRemoved;
            _boundTarget.OnNameChanged -= HandleNameChanged;
        }

        base.Close();
    }
}
