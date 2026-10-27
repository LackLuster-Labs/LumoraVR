// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Lumora.Core.Security;
using Lumora.Warden;

namespace Lumora.Core.Components;

// Marks what it sits on as not-yours-to-take. The datamodel gate reads it on the copy path and
// refuses a SaveCopy or an Export of the marked object for anyone who does not own it.
//
// THE OWNER ALWAYS PASSES, and that is decided in the gate, not here: AuthorizeCopy answers yes for a
// target the actor owns before it ever looks for a marker, and yes for the authority (a host is
// already holding every byte of the world in its own memory, so a marker that claimed to stop it
// would be theatre). So marking your own prop costs you nothing, and marking world content stops
// visitors rather than the person who built it.
//
// Nearest marker wins: the gate walks component -> slot -> slot lineage and takes the FIRST one it
// finds, so marking a container covers everything inside it without every child carrying its own.
// The flip side is that a DISABLED marker low in the tree shadows an enabled one above it - an
// authoring foot-gun, not a hole, since attaching a component to content you do not own is refused
// well before any of this.
//
// Persisted on purpose. A protection that evaporated on save/load would protect the object exactly
// until someone reloaded the world, which is not a protection. -xlinka
[ComponentCategory("Utility")]
[SingleInstancePerSlot]
public class ItemProtection : Component, IDataModelCopyProtection, ICustomInspectorUI
{
    // Keep it out of other people's inventories.
    public readonly Sync<bool> BlockSaveCopy;

    // Keep it off other people's disks. Separate from the above because "keep it" and "take it off
    // this machine" are different asks and a world may want to allow the first and never the second.
    public readonly Sync<bool> BlockExport;

    public ItemProtection()
    {
        BlockSaveCopy = new Sync<bool>(this, true);
        BlockExport = new Sync<bool>(this, true);
    }

    public bool BlocksSaveCopy => IsActive && BlockSaveCopy.Value;

    public bool BlocksExport => IsActive && BlockExport.Value;

    private bool IsActive => !IsDestroyed && Enabled.Value;

    // THE COPY GATE, for the call sites that take content out of a world (save to inventory, export
    // to a file). Composes the action with the acting user and asks the world's gate, which is the
    // only thing that decides: ownership, the role's copy caps, the host's toggles and the marker
    // above all resolve in there. A world with no gate (or a slot with no world) answers yes, which
    // is the local single-player case and has no one to protect anything from.
    //
    // A null actor resolves to the local user, because every route into here is a thing the person at
    // this machine just asked for. Filled in HERE rather than left for the gate: a registered rule is
    // handed the request before the gate resolves the actor, so a rule would see nobody at all.
    public static bool AllowsSaveCopy(Slot? slot, User? actor, out string? reason)
        => AllowsSealed(slot, actor, SealRule.SaveCopy, out reason) && Allows(slot, actor, DataModelPermissionAction.SaveCopy, out reason);

    public static bool AllowsExport(Slot? slot, User? actor, out string? reason)
        => AllowsSealed(slot, actor, SealRule.Export, out reason) && Allows(slot, actor, DataModelPermissionAction.Export, out reason);

    // An account's seal in or over the object answers first, and it binds the host as much as anyone (AvatarProtection).
    // A seal still being checked refuses too: nothing on these paths can wait.
    private static bool AllowsSealed(Slot? slot, User? actor, SealRule rule, out string? reason)
    {
        reason = null;
        if (slot?.World == null || ItemSeals.CountIn(slot.World) == 0)
            return true;
        var account = actor != null ? actor.AccountId.Value : ItemSeals.ActingAccount(slot.World);
        return ItemSeals.Judge(slot, account, rule, out reason) == SealVerdict.Allowed;
    }

    private static bool Allows(Slot? slot, User? actor, DataModelPermissionAction action, out string? reason)
    {
        reason = null;
        if (slot == null || slot.IsDestroyed)
        {
            reason = "no object";
            return false;
        }

        // The world root is not an item. Taking a copy of it would pack the entire session - every other
        // user's body included - into one inventory entry, and the "item" would carry the world's
        // settings and permission config with it. -xlinka
        if (slot.IsRootSlot)
        {
            reason = "the world root is not an object you can take";
            return false;
        }

        var world = slot.World;
        var permissions = world?.DataModelPermissions;
        if (world == null || permissions == null)
            return true;

        var request = new DataModelPermissionRequest(
            world, actor ?? world.LocalUser, slot, slot.Parent, slot,
            DataModelPermissionSurface.Slot, action, isNetwork: false);
        return permissions.Authorize(request, out reason);
    }

    public void BuildInspectorBody(UIBuilder ui)
    {
        InspectorStats.AddRow(ui, "Save copy", BlocksSaveCopy ? "blocked" : "allowed");
        InspectorStats.AddRow(ui, "Export", BlocksExport ? "blocked" : "allowed");
        InspectorStats.AddRow(ui, "Owner", "always exempt");
    }
}
