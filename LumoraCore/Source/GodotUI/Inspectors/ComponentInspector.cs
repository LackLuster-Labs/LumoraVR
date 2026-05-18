// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
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
    public readonly SyncRef<Component> TargetComponent;

    /// <summary>
    /// Whether to allow removing this component from the slot.
    /// </summary>
    public readonly Sync<bool> AllowRemove;

    /// <summary>
    /// Whether to show inherited properties from base classes.
    /// </summary>
    public readonly Sync<bool> ShowInherited;

    /// <summary>
    /// Whether to show the component header with type name.
    /// </summary>
    public readonly Sync<bool> ShowHeader;

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

        TargetComponent.OnTargetChange += OnTargetComponentChanged;
    }

    public override void OnInit()
    {
        base.OnInit();
        AllowRemove.Value = true;
        ShowInherited.Value = false;
        ShowHeader.Value = true;
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

    // ── SyncMemberEditorBuilder integration ───────────────────────────────────

    /// <summary>
    /// Returns typed descriptors for every inspectable sync member on the target component.
    /// The Godot scene uses this to build the correct editor widget per field.
    /// </summary>
    public IReadOnlyList<FieldEditorInfo> GetMemberDescriptors()
    {
        var component = TargetComponent.Target;
        if (component == null) return [];
        return SyncMemberEditorBuilder.Build(component, ShowInherited.Value);
    }

    /// <summary>
    /// Update a field by name using a raw string from a text input.
    /// Works for bool, int, float, string, and enum fields.
    /// </summary>
    public bool TrySetFieldFromString(string memberName, string rawInput)
    {
        var field = TargetComponent.Target?.TryGetField(memberName);
        if (field is not ISyncMember member) return false;
        bool ok = SyncMemberEditorBuilder.TrySetFromString(member, rawInput);
        if (ok) OnPropertyChanged?.Invoke(memberName, rawInput);
        return ok;
    }

    /// <summary>Typed setter for bool fields (e.g. toggle widgets).</summary>
    public void SetBool(string memberName, bool value)
    {
        var field = TargetComponent.Target?.TryGetField(memberName);
        if (field is ISyncMember m) { SyncMemberEditorBuilder.SetBool(m, value); OnPropertyChanged?.Invoke(memberName, value); }
    }

    /// <summary>Typed setter for int fields.</summary>
    public void SetInt(string memberName, int value)
    {
        var field = TargetComponent.Target?.TryGetField(memberName);
        if (field is ISyncMember m) { SyncMemberEditorBuilder.SetInt(m, value); OnPropertyChanged?.Invoke(memberName, value); }
    }

    /// <summary>Typed setter for float fields.</summary>
    public void SetFloat(string memberName, float value)
    {
        var field = TargetComponent.Target?.TryGetField(memberName);
        if (field is ISyncMember m) { SyncMemberEditorBuilder.SetFloat(m, value); OnPropertyChanged?.Invoke(memberName, value); }
    }

    /// <summary>Typed setter for float3 fields (position, scale, etc.).</summary>
    public void SetFloat3(string memberName, float3 value)
    {
        var field = TargetComponent.Target?.TryGetField(memberName);
        if (field is ISyncMember m) { SyncMemberEditorBuilder.SetFloat3(m, value); OnPropertyChanged?.Invoke(memberName, value); }
    }

    /// <summary>Typed setter for color fields — also opens the color picker if requested.</summary>
    public void SetColor(string memberName, color value)
    {
        var field = TargetComponent.Target?.TryGetField(memberName);
        if (field is ISyncMember m) { SyncMemberEditorBuilder.SetColor(m, value); OnPropertyChanged?.Invoke(memberName, value); }
    }

    /// <summary>Typed setter for enum fields (supply the enum name as string).</summary>
    public void SetEnum(string memberName, string enumName)
    {
        var field = TargetComponent.Target?.TryGetField(memberName);
        if (field is ISyncMember m) { SyncMemberEditorBuilder.SetEnum(m, enumName); OnPropertyChanged?.Invoke(memberName, enumName); }
    }

    /// <summary>
    /// Spawn a ColorPickerPanel for a color field and return it.
    /// The picker is placed near the inspector panel slot.
    /// </summary>
    public GodotUI.Wizards.ColorPickerPanel? OpenColorPicker(string memberName)
    {
        var component = TargetComponent.Target;
        if (component == null) return null;

        var pickerSlot = Slot.Parent?.AddSlot($"ColorPicker_{memberName}") ?? Slot.AddSlot($"ColorPicker_{memberName}");
        var picker = pickerSlot.AttachComponent<GodotUI.Wizards.ColorPickerPanel>();
        picker.SetTarget(component, memberName, memberName);

        pickerSlot.GlobalPosition = Slot.GlobalPosition
            + Slot.Right * 0.48f
            + Slot.Up * 0.04f
            + Slot.Forward * 0.03f;
        pickerSlot.GlobalRotation = Slot.GlobalRotation;

        return picker;
    }

    /// <summary>
    /// Override GetUIData to expose the component type name and member count
    /// so the Godot header can display them without extra C# calls.
    /// </summary>
    public override Dictionary<string, string> GetUIData()
    {
        var component = TargetComponent.Target;
        if (component == null)
            return new Dictionary<string, string> { ["TypeName"] = "None", ["MemberCount"] = "0" };

        var descriptors = GetMemberDescriptors();
        return new Dictionary<string, string>
        {
            ["TypeName"] = component.GetType().Name,
            ["MemberCount"] = descriptors.Count.ToString(),
            ["AllowRemove"] = AllowRemove.Value ? "1" : "0",
        };
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
