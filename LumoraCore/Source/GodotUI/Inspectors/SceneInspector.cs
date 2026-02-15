using System;
using Lumora.Core.Assets;
using Lumora.Core.Components.Gizmos;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

#nullable enable

namespace Lumora.Core.GodotUI.Inspectors;

/// <summary>
/// Combined scene inspector with slot hierarchy and component views.
/// Main inspector panel for exploring and editing the world.
/// </summary>
[ComponentCategory("GodotUI/Inspectors")]
public class SceneInspector : GodotUIPanel
{
    protected override string DefaultScenePath => LumAssets.UI.SceneInspector;
    protected override float2 DefaultSize => new float2(800, 600);

    /// <summary>
    /// The root slot for the hierarchy view.
    /// </summary>
    public SyncRef<Slot> Root { get; private set; } = null!;

    /// <summary>
    /// The currently selected slot for component view.
    /// </summary>
    public SyncRef<Slot> ComponentView { get; private set; } = null!;

    /// <summary>
    /// Reference to the linked gizmo for the selected slot.
    /// </summary>
    public SyncRef<SlotGizmo> LinkedGizmo { get; private set; } = null!;

    /// <summary>
    /// Whether the hierarchy panel is expanded.
    /// </summary>
    public Sync<bool> HierarchyExpanded { get; private set; } = null!;

    /// <summary>
    /// Whether to show inherited component properties.
    /// </summary>
    public Sync<bool> ShowInherited { get; private set; } = null!;

    /// <summary>
    /// Split ratio between hierarchy and component panels (0-1).
    /// </summary>
    public Sync<float> SplitRatio { get; private set; } = null!;

    /// <summary>
    /// Event fired when root changes.
    /// </summary>
    public event Action<Slot?>? OnRootChanged;

    /// <summary>
    /// Event fired when component view selection changes.
    /// </summary>
    public event Action<Slot?>? OnSelectionChanged;

    /// <summary>
    /// Event fired when a slot is destroyed.
    /// </summary>
    public event Action<Slot>? OnSlotDestroyed;

    /// <summary>
    /// Event fired when a slot is duplicated.
    /// </summary>
    public event Action<Slot, Slot>? OnSlotDuplicated;

    /// <summary>
    /// Event fired when a child is added to a slot.
    /// </summary>
    public event Action<Slot, Slot>? OnChildAdded;

    /// <summary>
    /// Event fired when requesting component attacher.
    /// </summary>
    public event Action<Slot>? OnAttachComponentRequested;

    private Slot? _previousRoot;
    private Slot? _previousSelection;

    public override void OnAwake()
    {
        base.OnAwake();
        InitializeSceneInspectorSyncMembers();
    }

    private void InitializeSceneInspectorSyncMembers()
    {
        Root = new SyncRef<Slot>(this);
        ComponentView = new SyncRef<Slot>(this);
        LinkedGizmo = new SyncRef<SlotGizmo>(this);
        HierarchyExpanded = new Sync<bool>(this, true);
        ShowInherited = new Sync<bool>(this, false);
        SplitRatio = new Sync<float>(this, 0.4f);

        Root.OnTargetChange += OnRootTargetChanged;
        ComponentView.OnTargetChange += OnComponentViewTargetChanged;
    }

    private void OnRootTargetChanged(SyncRef<Slot> syncRef)
    {
        if (_previousRoot != null)
        {
            _previousRoot.OnChildAdded -= HandleChildAdded;
            _previousRoot.OnChildRemoved -= HandleChildRemoved;
        }

        _previousRoot = Root.Target;

        if (_previousRoot != null)
        {
            _previousRoot.OnChildAdded += HandleChildAdded;
            _previousRoot.OnChildRemoved += HandleChildRemoved;
        }

        OnRootChanged?.Invoke(Root.Target);
        NotifyChanged();
    }

    private void OnComponentViewTargetChanged(SyncRef<Slot> syncRef)
    {
        // Remove gizmo from previous selection
        if (_previousSelection != null && LinkedGizmo.Target != null)
        {
            if (LinkedGizmo.Target.TargetSlot == _previousSelection)
            {
                GizmoHelper.DestroyGizmo(_previousSelection);
                LinkedGizmo.Target = null;
            }
        }

        _previousSelection = ComponentView.Target;

        // Add gizmo to new selection (if not root)
        if (ComponentView.Target != null && !ComponentView.Target.IsRootSlot)
        {
            var gizmo = GizmoHelper.SpawnGizmoFor(ComponentView.Target);
            if (gizmo != null)
            {
                LinkedGizmo.Target = gizmo;
            }
        }

        OnSelectionChanged?.Invoke(ComponentView.Target);
        NotifyChanged();
    }

    private void HandleChildAdded(Slot parent, Slot child)
    {
        NotifyChanged();
    }

    private void HandleChildRemoved(Slot parent, Slot child)
    {
        // If removed slot was selected, clear selection
        if (ComponentView.Target == child)
        {
            ComponentView.Target = parent;
        }
        NotifyChanged();
    }

    /// <summary>
    /// Navigate hierarchy root up one level.
    /// </summary>
    public void NavigateRootUp()
    {
        if (Root.Target != null && !Root.Target.IsRootSlot)
        {
            Root.Target = Root.Target.Parent;
        }
    }

    /// <summary>
    /// Set the component view slot as the new hierarchy root.
    /// </summary>
    public void SetSelectionAsRoot()
    {
        if (ComponentView.Target != null)
        {
            Root.Target = ComponentView.Target;
        }
    }

    /// <summary>
    /// Add a child slot to the selected slot.
    /// </summary>
    public Slot? AddChild()
    {
        if (ComponentView.Target == null) return null;

        var newSlot = ComponentView.Target.AddSlot($"{ComponentView.Target.Name.Value} - Child");
        OnChildAdded?.Invoke(ComponentView.Target, newSlot);
        NotifyChanged();
        return newSlot;
    }

    /// <summary>
    /// Duplicate the selected slot.
    /// </summary>
    public Slot? DuplicateSelection()
    {
        if (ComponentView.Target == null || ComponentView.Target.IsRootSlot) return null;

        var duplicate = ComponentView.Target.Duplicate();
        if (duplicate != null)
        {
            OnSlotDuplicated?.Invoke(ComponentView.Target, duplicate);
            ComponentView.Target = duplicate;
        }
        NotifyChanged();
        return duplicate;
    }

    /// <summary>
    /// Destroy the selected slot.
    /// </summary>
    public void DestroySelection()
    {
        if (ComponentView.Target == null || ComponentView.Target.IsRootSlot) return;

        var toDestroy = ComponentView.Target;
        var parent = toDestroy.Parent;

        OnSlotDestroyed?.Invoke(toDestroy);

        // Move selection to parent before destroying
        ComponentView.Target = parent;

        toDestroy.Destroy();
        NotifyChanged();
    }

    /// <summary>
    /// Open the component attacher for the selected slot.
    /// </summary>
    public void OpenComponentAttacher()
    {
        if (ComponentView.Target == null) return;
        OnAttachComponentRequested?.Invoke(ComponentView.Target);
    }

    /// <summary>
    /// Select a slot in the hierarchy.
    /// </summary>
    public void SelectSlot(Slot? slot)
    {
        ComponentView.Target = slot;
    }

    /// <summary>
    /// Focus on a slot (set as root and select it).
    /// </summary>
    public void FocusSlot(Slot slot)
    {
        if (slot == null) return;
        Root.Target = slot;
        ComponentView.Target = slot;
    }

    /// <summary>
    /// Initialize with world root.
    /// </summary>
    public void InitializeWithWorldRoot()
    {
        if (World != null)
        {
            Root.Target = World.RootSlot;
            ComponentView.Target = World.RootSlot;
        }
    }

    public override void HandleButtonPress(string buttonPath)
    {
        if (buttonPath.EndsWith("RootUpButton"))
        {
            NavigateRootUp();
            return;
        }

        if (buttonPath.EndsWith("SetRootButton"))
        {
            SetSelectionAsRoot();
            return;
        }

        if (buttonPath.EndsWith("AddChildButton"))
        {
            AddChild();
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

        if (buttonPath.EndsWith("InheritedToggle"))
        {
            ShowInherited.Value = !ShowInherited.Value;
            return;
        }

        base.HandleButtonPress(buttonPath);
    }

    public override void Close()
    {
        // Clean up gizmo
        if (LinkedGizmo.Target != null)
        {
            GizmoHelper.DestroyGizmo(LinkedGizmo.Target.TargetSlot);
        }

        // Unsubscribe from events
        if (_previousRoot != null)
        {
            _previousRoot.OnChildAdded -= HandleChildAdded;
            _previousRoot.OnChildRemoved -= HandleChildRemoved;
        }

        base.Close();
    }
}
