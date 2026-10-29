// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Components.UI;
using Lumora.Core.Scripting;
using Lumora.Core.Security;

namespace Lumora.Core.Components.Interaction;

// Contributes "Destroy" and "Duplicate" when the hand that summoned the menu
// is holding objects. - xlinka
[ComponentCategory("Interaction")]
public class GrabbedObjectContextActions : ContextMenuItemSource
{
    public override void PopulateContextMenu(ContextMenuPage page, ContextMenuContext context)
    {
        if (Slot?.ActiveUserRoot?.ActiveUser != World?.LocalUser)
            return;

        // The hand that opened the menu says what it holds; a context built elsewhere (an inspector
        // row) names only the side, so the hand is looked up from that. -xlinka
        var grabber = context?.Grabber ?? FindHandGrabber(context?.Side);
        if (grabber == null || grabber.IsDestroyed || !grabber.IsHoldingObjects)
            return;

        // An object marked to stay as it is keeps these off it; a hand holding only such objects shows them off.
        // Destroy also needs the right to destroy: holding something is not owning it.
        bool canDestroy = AnyGrabbed(grabber, slot => !DestroyBlock.Blocks(slot) && slot.MayDestroy());
        page.AddItem(new ContextMenuItem
        {
            Group = ContextMenuGroup.Held,
            Label = "Destroy",
            IsEnabled = canDestroy,
            FillColor = new[] { 0.32f, 0.10f, 0.10f, 0.92f },
            OnPressed = canDestroy ? _ => DestroyGrabbed(grabber) : null,
        });

        bool canDuplicate = AnyGrabbed(grabber, slot => !DuplicateBlock.Blocks(slot) && MayDuplicate(slot));
        page.AddItem(new ContextMenuItem
        {
            Group = ContextMenuGroup.Held,
            Label = "Duplicate",
            Detail = canDuplicate ? null : SealRefusal(grabber, SealRule.SaveCopy),
            IsEnabled = canDuplicate,
            FillColor = new[] { 0.10f, 0.28f, 0.14f, 0.92f },
            OnPressed = canDuplicate ? _ => DuplicateGrabbed(grabber) : null,
        });

        // Asked here rather than only in the handler so the refusal is something the user can SEE:
        // the radial menu has already closed by the time an action runs, and a button that quietly
        // does nothing reads as a broken button.
        bool canSave = AnySaveableGrabbed(grabber, out var saveRefusal);
        page.AddItem(new ContextMenuItem
        {
            Group = ContextMenuGroup.Held,
            Label = canSave ? "Save to Inventory" : saveRefusal ?? "Save Blocked",
            Detail = canSave ? SaveDetail() : SealRefusal(grabber, SealRule.SaveCopy),
            IsEnabled = canSave,
            FillColor = canSave
                ? new[] { 0.12f, 0.18f, 0.30f, 0.92f }
                : new[] { 0.22f, 0.16f, 0.16f, 0.92f },
            OnPressed = canSave ? _ => SaveGrabbedToInventory(grabber) : null,
        });

        // Equip on avatar: if a held object is an (unworn) avatar, offer to wear it from the held-object menu.
        // Releases the grab, then routes through the body-node dispatch. - xlinka
        var manager = Slot?.ActiveUserRoot?.GetRegisteredComponent<AvatarEquipManager>();
        if (manager != null)
        {
            foreach (var slot in CollectGrabbedSlots(grabber))
            {
                var avatarRoot = slot.GetComponent<AvatarForm>();
                if (avatarRoot == null || avatarRoot.IsEquipped || slot == manager.CurrentAvatar.Target)
                    continue;

                var targetSlot = slot;
                // A sealed avatar of someone else's says so here rather than after the press.
                if (ItemSeals.Judge(slot, World?.LocalUser, SealRule.Equip, out var equipRefusal) == SealVerdict.Refused)
                {
                    page.AddItem(new ContextMenuItem
                    {
                        Group = ContextMenuGroup.Held,
                        Label = ProtectionLocale.MenuProtected.Resolve(),
                        Detail = equipRefusal,
                        IsEnabled = false,
                        FillColor = new[] { 0.22f, 0.16f, 0.16f, 0.92f },
                    });
                    break;
                }
                page.AddItem(new ContextMenuItem
                {
                    Group = ContextMenuGroup.Held,
                    Label = "Equip Avatar",
                    FillColor = new[] { 0.14f, 0.30f, 0.18f, 0.92f },
                    OnPressed = _ =>
                    {
                        grabber.ReleaseAll();
                        manager.EquipAvatar(targetSlot);
                    },
                });
                break; // one equip item even if multiple avatars are held
            }
        }
    }

    // The seal's reason for the first held object one refuses, for the hub under a disabled item.
    private string? SealRefusal(Grabber grabber, SealRule rule)
    {
        foreach (var slot in CollectGrabbedSlots(grabber))
        {
            if (ItemSeals.Refuses(slot, rule, out var reason))
                return reason;
        }
        return null;
    }

    private Grabber? FindHandGrabber(Input.Chirality? side)
    {
        if (side == null)
            return null;
        var userRoot = Slot?.ActiveUserRoot?.Slot;
        if (userRoot == null)
            return null;
        foreach (var tool in userRoot.GetComponentsInChildren<HandTool>())
        {
            if (tool.Side.Value == side)
                return tool.Grabber;
        }
        return null;
    }

    private void SaveGrabbedToInventory(Grabber grabber)
    {
        var actor = World?.LocalUser;
        // The menu that asked is gone by the time a save answers, so a refusal for the email is said in front of the
        // eyes, once for the whole hand.
        if (AccountEmail.SavesHeld)
        {
            ToolNotice.Say(World, AccountEmailLocale.SaveRefusedTitle.Resolve(), AccountEmailLocale.SaveRefusedHint.Resolve());
            return;
        }
        foreach (var slot in CollectGrabbedSlots(grabber))
        {
            if (slot.IsDestroyed)
                continue;
            // Game-mechanic props are meant to be played with, not pocketed. Not a security
            // boundary - an inspector still copies whatever it likes - so nothing else in the save
            // path leans on this. - xlinka
            if (GrabSaveBlock.BlocksInventorySave(slot))
            {
                Logging.Logger.Log($"Save to Inventory refused for '{slot.Name}': marked not saveable.");
                continue;
            }
            if (IsGraphNode(slot))
            {
                Logging.Logger.Log($"Save to Inventory refused for '{slot.Name}': a graph node means nothing outside its graph.");
                continue;
            }
            // This one IS the boundary. Holding something is not owning it: a grab parents the object
            // under your hand, which reads as ownership everywhere else, so the copy question has to be
            // asked of the gate rather than of where the thing currently hangs. -xlinka
            if (!ItemProtection.AllowsSaveCopy(slot, actor, out var reason))
            {
                Logging.Logger.Log($"Save to Inventory refused for '{slot.Name}': {reason ?? "not allowed"}.");
                continue;
            }
            // Packed here on the world thread, uploaded off it; the answer only goes to the log because
            // the radial menu that asked is already gone. Lands in the folder the inventory has open, which
            // the hub said while the item was hovered. -xlinka
            var name = slot.Name;
            _ = Inventory.SaveItemHereAsync(slot, name).ContinueWith(t =>
            {
                var result = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion
                    ? t.Result
                    : InventoryResult.Failure(t.Exception?.GetBaseException().Message ?? "upload failed");
                Logging.Logger.Log($"Save to Inventory '{name}': {result.Message}");
                if (result.Code == AccountEmail.NotVerifiedCode)
                    World?.RunSynchronously(() => ToolNotice.Say(World, AccountEmailLocale.SaveRefusedTitle.Resolve(), AccountEmailLocale.SaveRefusedHint.Resolve()));
            });
        }
    }

    // Where the save will land, for the hub under the name: the folder the inventory has open, or the top of
    // the user's own when that folder is not theirs to save into.
    private static string SaveDetail()
    {
        static string Short(string name) => name.Length > 16 ? name[..15].TrimEnd() + "…" : name;
        Inventory.SavePlace(out var notice);
        return notice != null
            ? $"Into Inventory, not {Short(Inventory.OpenFolder?.Name ?? "there")}"
            : $"Into {Short(Inventory.SavePlaceLabel())}";
    }

    // Whether anything in the hand can actually be filed away, and a short label for why not when
    // nothing can. A mixed hand stays enabled and the handler skips the pieces it may not take.
    private bool AnySaveableGrabbed(Grabber grabber, out string? refusal)
    {
        refusal = null;
        var actor = World?.LocalUser;
        bool marked = false;
        bool denied = false;

        foreach (var slot in CollectGrabbedSlots(grabber))
        {
            if (slot.IsDestroyed)
                continue;
            if (GrabSaveBlock.BlocksInventorySave(slot) || IsGraphNode(slot))
            {
                marked = true;
                continue;
            }
            if (!ItemProtection.AllowsSaveCopy(slot, actor, out _))
            {
                denied = true;
                continue;
            }
            return true;
        }

        refusal = denied ? "Protected" : marked ? "Not Saveable" : null;
        return false;
    }

    private void DestroyGrabbed(Grabber grabber) => DestroyGrabbed(grabber, FindUndoManager());

    // Shared by the held-object menu and the desktop Delete shortcut.
    internal static void DestroyGrabbed(Grabber grabber, UndoManager? manager)
    {
        var slots = CollectGrabbedSlots(grabber);
        slots.RemoveAll(DestroyBlock.Blocks);
        slots.RemoveAll(NotTheirsToDestroy);
        if (slots.Count == 0)
            return;
        // Release only the objects being deleted; protected items in a mixed hand-load stay held.
        foreach (var held in new List<IGrabbable>(grabber.GrabbedObjects))
        {
            if (held is Component component && slots.Contains(component.Slot))
                grabber.Release(held);
        }

        // Nodes go through their graph, which takes a group that loses every node with them; the lot is one step.
        var nodes = TakeGraphNodes(slots);
        using var step = manager != null && nodes.Count > 0 ? manager.BeginBatch(UndoLocale.Destroy) : default;
        if (nodes.Count > 0)
        {
            ScriptGraphEditing.DeleteNodes(nodes, out var refused);
            if (refused != null)
                Logging.Logger.Log($"Destroy: {refused}.");
        }
        if (slots.Count == 0)
            return;

        // Undoable destroy: the batch parks the slots in the graveyard and only
        // destroys for real once it leaves the history.
        var batch = manager != null ? SlotExistenceUndoBatch.Destroy(grabber.World, slots) : null;
        if (batch != null)
        {
            manager!.Record(batch);
            return;
        }

        foreach (var slot in slots)
        {
            if (!slot.IsDestroyed)
                slot.Destroy();
        }
    }

    // Asked before anything is let go of, so a refused piece stays in the hand. The menu that asked is gone
    // by now, so the answer goes to the log.
    private static bool NotTheirsToDestroy(Slot slot)
    {
        if (slot.MayDestroy())
            return false;
        Logging.Logger.Log($"Destroy refused for '{slot.Name.Value}': not yours to destroy.");
        return true;
    }

    private void DuplicateGrabbed(Grabber grabber)
    {
        var root = World?.RootSlot;
        if (root == null)
            return;

        // A copy is the copier's own, the host's included.
        using var own = World!.EnterSpawnAllocation();

        // Nodes are copied into their own graphs, together, so a wire between two of them joins the copies.
        var held = CollectGrabbedSlots(grabber);
        held.RemoveAll(slot => DuplicateBlock.Blocks(slot) || !MayDuplicate(slot));
        var nodes = TakeGraphNodes(held);
        var duplicates = nodes.Count > 0 ? ScriptGraphEditing.CopyNodes(nodes) : new List<Slot>();
        foreach (var slot in held)
        {
            if (slot.IsDestroyed)
                continue;
            var copy = slot.Duplicate(root, preserveGlobalTransform: true);
            if (copy != null)
                duplicates.Add(copy);
        }

        var batch = SlotExistenceUndoBatch.Created(World, duplicates, UndoLocale.Duplicate);
        if (batch != null)
            FindUndoManager()?.Record(batch);
    }

    // A copy is a new thing of the copier's own, so it takes the right to bring things in. Where the world is frozen
    // it also takes the original being theirs: a copy of the world's own prop is the world multiplied, and the
    // copier's to edit and keep. Only this machine can tell a copy from a fresh build, so this is where it is asked.
    // -xlinka
    private bool MayDuplicate(Slot slot)
    {
        if (ItemSeals.Refuses(slot, SealRule.SaveCopy, out _))
            return false;
        var world = World;
        var local = world?.LocalUser;
        var permissions = world?.DataModelPermissions;
        if (local == null || permissions == null || !permissions.Enabled)
            return true;
        if (!permissions.AllowsDomain(local, DataModelPermissionDomain.Spawn))
            return false;
        if (!permissions.SocialLock)
            return true;
        byte owner = slot.ReferenceID.GetUserByte();
        return RefIDConstants.IsValidUserByte(owner) && local.HoldsByte(owner);
    }

    private UndoManager? FindUndoManager()
    {
        return Slot?.GetComponent<UndoManager>()
               ?? Slot?.ActiveUserRoot?.Slot?.GetComponentInChildren<UndoManager>();
    }

    private static bool AnyGrabbed(Grabber grabber, System.Predicate<Slot> match)
    {
        foreach (var slot in CollectGrabbedSlots(grabber))
        {
            if (match(slot))
                return true;
        }
        return false;
    }

    private static List<Slot> CollectGrabbedSlots(Grabber grabber)
    {
        var slots = new List<Slot>();
        foreach (var grabbable in grabber.GrabbedObjects)
        {
            var slot = (grabbable as Component)?.Slot;
            if (slot != null && !slot.IsDestroyed)
                slots.Add(slot);
        }
        return slots;
    }

    // A script or shader node means something only in its graph, so one is never filed away on its own.
    private static bool IsGraphNode(Slot slot) => slot.GetComponent<ScriptGraphNode>() is { IsDestroyed: false };

    // The nodes among held slots that are in a graph, taken out of the list: the graph deletes and copies those.
    private static List<ScriptGraphNode> TakeGraphNodes(List<Slot> slots)
    {
        var nodes = new List<ScriptGraphNode>();
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].GetComponent<ScriptGraphNode>() is not { IsDestroyed: false, Space: not null } node)
                continue;
            nodes.Add(node);
            slots.RemoveAt(i--);
        }
        return nodes;
    }
}
