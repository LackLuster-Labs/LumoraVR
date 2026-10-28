// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Helio.UI.Listing;
using Lumora.Core.Localization;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Components.UI;

// Owns every button this panel binds. A Helio button carries ONE press action and it is a SyncDelegate
// (world element plus method name), which a closure cannot be named as - so the house rule is that the
// action is a method on a component and the component works out which button fired. Listing rows are
// POOLED and rebound as the list scrolls, so what gets registered here is the ROW, never the item: the
// user, the role column or the capability comes off row.Item at fire time. -xlinka
[ComponentCategory("Hidden")]
public sealed class SessionPermissionRelay : Component
{
    internal readonly struct Binding
    {
        public readonly ListingRow Row;
        public readonly int Index;

        public Binding(ListingRow row, int index)
        {
            Row = row;
            Index = index;
        }
    }

    internal SessionPermissionsPanel? Panel;

    private readonly Dictionary<Button, Binding> _bindings = new();

    internal void Register(Button button, ListingRow row, int index)
    {
        if (button != null && row != null)
            _bindings[button] = new Binding(row, index);
    }

    private bool Resolve(Button button, out ListingRow row, out int index)
    {
        row = null!;
        index = -1;
        if (button == null || Panel == null || Panel.IsDetached)
            return false;
        if (!_bindings.TryGetValue(button, out var binding))
            return false;
        row = binding.Row;
        index = binding.Index;
        return row.Item != null;
    }

    [SyncMethod]
    public void OnRolePillPressed(Button button, UIInteractionContext context)
    {
        if (Resolve(button, out var row, out int index) && row.Item?.Tag is User user)
            Panel!.AssignRole(user, index);
    }

    // Every plain segmented row on the page: the joiner role and the violation response.
    [SyncMethod]
    public void OnPillPressed(Button button, UIInteractionContext context)
    {
        if (!Resolve(button, out var row, out int index) || row.Item is not ListingChoice choice)
            return;
        if (!choice.Interactable || index < 0 || index >= choice.Options.Count)
            return;
        choice.Write(index);
        row.Bind(choice);
    }

    // The scale bounds. A capability cell is a switch and answers through OnCapTogglePressed, so the
    // single-pill path is down to the two rows that still are one pill.
    [SyncMethod]
    public void OnMatrixCellPressed(Button button, UIInteractionContext context)
    {
        if (!Resolve(button, out var row, out int index) || row.Item is not PermMatrixItem matrix)
            return;
        if (!matrix.Interactable)
            return;
        var roles = Panel!.AssignableRoles();
        if (index < 0 || index >= roles.Count)
            return;

        string roleName = roles[index].Name;
        switch (matrix.Row)
        {
            case PermMatrixKind.MinScale:
                Panel.ToggleScaleEditor(roleName, minimum: true);
                break;
            case PermMatrixKind.MaxScale:
                Panel.ToggleScaleEditor(roleName, minimum: false);
                break;
        }
    }

    // A switch flips the answer the role gets NOW, read off the gate at press time rather than off the
    // paint: an answer another peer or the mode moved since the last bind flips from where it really is.
    [SyncMethod]
    public void OnCapTogglePressed(Button button, UIInteractionContext context)
    {
        if (!Resolve(button, out var row, out int index) || row.Item is not PermMatrixItem matrix)
            return;
        if (!matrix.Interactable || matrix.Row != PermMatrixKind.Capability)
            return;
        var roles = Panel!.AssignableRoles();
        if (index < 0 || index >= roles.Count || Panel.ModeForcesOff(matrix.Domain))
            return;
        bool allowed = Panel.CapAllows(roles[index], matrix.Domain);
        Panel.SetCap(roles[index].Name, matrix.Domain,
            allowed ? DataModelPermissionToggle.Deny : DataModelPermissionToggle.Grant);
    }

    [SyncMethod]
    public void OnResetCapsPressed(Button button, UIInteractionContext context)
    {
        if (Resolve(button, out var row, out _) && row.Item is PermMatrixItem { Row: PermMatrixKind.Header } head
            && head.ShowReset)
            Panel!.ResetCapsToMode();
    }

    [SyncMethod]
    public void OnKickPressed(Button button, UIInteractionContext context)
    {
        if (Resolve(button, out var row, out _) && row.Item?.Tag is User user)
            Panel!.RequestKick(user);
    }

    [SyncMethod]
    public void OnTempBanPressed(Button button, UIInteractionContext context)
    {
        if (Resolve(button, out var row, out _) && row.Item?.Tag is User user)
            Panel!.RequestTempBan(user);
    }
}
