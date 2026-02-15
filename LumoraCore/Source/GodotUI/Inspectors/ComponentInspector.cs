using System;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.GodotUI.Inspectors;

/// <summary>
/// Component inspector panel that shows all sync members of a component.
/// Allows editing properties with appropriate editor controls.
/// </summary>
[ComponentCategory("GodotUI/Inspectors")]
public class ComponentInspector : GodotUIPanel
{
    /// <summary>
    /// Scene path for the inspector UI.
    /// </summary>
    protected override string DefaultScenePath => LumAssets.UI.ComponentInspector;

    /// <summary>
    /// Default panel size.
    /// </summary>
    protected override float2 DefaultSize => new float2(350, 400);

    /// <summary>
    /// The component being inspected.
    /// </summary>
    public SyncRef<Component> TargetComponent { get; private set; } = null!;

    /// <summary>
    /// Whether to allow removing this component from the slot.
    /// </summary>
    public Sync<bool> AllowRemove { get; private set; } = null!;

    /// <summary>
    /// Whether to show inherited properties from base classes.
    /// </summary>
    public Sync<bool> ShowInherited { get; private set; } = null!;

    /// <summary>
    /// Whether to show the component header with type name.
    /// </summary>
    public Sync<bool> ShowHeader { get; private set; } = null!;

    /// <summary>
    /// Event fired when a property value is changed.
    /// </summary>
    public event Action<string, object?>? OnPropertyChanged;

    /// <summary>
    /// Event fired when remove component is requested.
    /// </summary>
    public event Action<Component>? OnRemoveRequested;

    /// <summary>
    /// Event fired when a reference field is clicked (for navigation).
    /// </summary>
    public event Action<ISyncRef>? OnReferenceClicked;

    public override void OnAwake()
    {
        base.OnAwake();
        InitializeSyncMembers();
    }

    private void InitializeSyncMembers()
    {
        TargetComponent = new SyncRef<Component>(this);
        AllowRemove = new Sync<bool>(this, true);
        ShowInherited = new Sync<bool>(this, false);
        ShowHeader = new Sync<bool>(this, true);

        TargetComponent.OnTargetChange += OnTargetComponentChanged;
    }

    private void OnTargetComponentChanged(SyncRef<Component> syncRef)
    {
        NotifyChanged();
    }

    /// <summary>
    /// Get the type name of the target component.
    /// </summary>
    public string GetComponentTypeName()
    {
        return TargetComponent.Target?.GetType().Name ?? "None";
    }

    /// <summary>
    /// Get the slot containing the target component.
    /// </summary>
    public Slot? GetComponentSlot()
    {
        return TargetComponent.Target?.Slot;
    }

    /// <summary>
    /// Request removal of the target component.
    /// </summary>
    public void RequestRemove()
    {
        if (TargetComponent.Target != null && AllowRemove.Value)
        {
            OnRemoveRequested?.Invoke(TargetComponent.Target);
        }
    }

    /// <summary>
    /// Update a property value on the target component.
    /// </summary>
    public void SetPropertyValue(string propertyName, object? value)
    {
        var component = TargetComponent.Target;
        if (component == null) return;

        var field = component.TryGetField(propertyName);
        if (field is IField fieldInterface)
        {
            fieldInterface.BoxedValue = value;
            OnPropertyChanged?.Invoke(propertyName, value);
        }
    }

    /// <summary>
    /// Get a property value from the target component.
    /// </summary>
    public object? GetPropertyValue(string propertyName)
    {
        var component = TargetComponent.Target;
        if (component == null) return null;

        var field = component.TryGetField(propertyName);
        if (field is ISyncMember syncMember)
        {
            return syncMember.GetValueAsObject();
        }
        return null;
    }

    /// <summary>
    /// Handle a reference field being clicked.
    /// </summary>
    public void HandleReferenceClick(string propertyName)
    {
        var component = TargetComponent.Target;
        if (component == null) return;

        var field = component.TryGetField(propertyName);
        if (field is ISyncRef syncRef)
        {
            OnReferenceClicked?.Invoke(syncRef);
        }
    }

    /// <summary>
    /// Handle button press from UI.
    /// </summary>
    public override void HandleButtonPress(string buttonPath)
    {
        if (buttonPath.EndsWith("RemoveButton"))
        {
            RequestRemove();
            return;
        }

        if (buttonPath.EndsWith("InheritedToggle"))
        {
            ShowInherited.Value = !ShowInherited.Value;
            return;
        }

        base.HandleButtonPress(buttonPath);
    }
}
