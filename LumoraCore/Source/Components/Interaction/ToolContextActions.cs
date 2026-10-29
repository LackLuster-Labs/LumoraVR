// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.UI;
using Lumora.Core.Input;

namespace Lumora.Core.Components.Interaction;

// Root actions for the hand that summoned the menu. Create New stays available with any equipped
// tool, independently of the current selection. - xlinka
[ComponentCategory("Interaction")]
public class ToolContextActions : ContextMenuItemSource
{
    private CreateNewPanel? _createPanel;
    private string _createCategory = "";

    public override void PopulateContextMenu(ContextMenuPage page, ContextMenuContext context)
    {
        if (Slot?.ActiveUserRoot?.ActiveUser != World?.LocalUser)
            return;

        var side = context?.Side;
        if (side == null)
            return;

        HandTool? hand = null;
        foreach (var tool in Slot!.ActiveUserRoot!.Slot.GetComponentsInChildren<HandTool>())
        {
            if (tool.Side.Value == side)
            {
                hand = tool;
                break;
            }
        }

        if (hand == null)
            return;

        var item = hand.ActiveToolItem.Target;
        var grabber = hand.Grabber;
        bool holdingSomething = grabber != null && grabber.IsHoldingObjects;
        if ((item == null || item.IsDestroyed) && !holdingSomething)
            return;

        // Unequip sits with the equip items; Create New goes with the tool's own actions. Create New is building, so a
        // world that is frozen for everyone leaves it out even under a gadget a script works. -xlinka
        if (item != null && !item.IsDestroyed)
        {
            if (World?.AllowsWorldEditing == true)
            {
                page.AddItem(new ContextMenuItem
                {
                    Group = ContextMenuGroup.Tool,
                    Label = "Create New",
                    FillColor = new[] { 0.35f, 0.28f, 0.62f, 0.95f },
                    OnPressed = _ => ToggleCreateNew(),
                });
            }
            page.AddItem(new ContextMenuItem
            {
                Group = ContextMenuGroup.Equip,
                Label = "Unequip Tool",
                FillColor = new[] { 0.30f, 0.22f, 0.12f, 0.92f },
                OnPressed = _ => hand.EquipToolItem(null),
            });
        }

        // Holding a tool: offer to equip it into this hand. Allowed even with a tool already equipped (the
        // equip swaps, popping the old one off) - the hand carries a default tool, so a held-only gate would
        // make this item unreachable. Release the grab first or the grabber keeps a ref to a docked slot. -xlinka
        if (grabber == null || !grabber.IsHoldingObjects)
            return;

        foreach (var grabbable in grabber.GrabbedObjects)
        {
            var slot = (grabbable as Component)?.Slot;
            var heldItem = slot?.GetComponent<ToolItem>();
            if (heldItem == null || heldItem.IsDestroyed || heldItem.IsEquipped || ReferenceEquals(heldItem, item))
                continue;

            var equipTarget = heldItem;
            var heldGrabbable = grabbable;
            // Shown off with its reason, not left out: the radial closes before an action runs, so a refusal has to
            // be on the item itself.
            bool mayEquip = heldItem.AllowsEquip(World?.LocalUser);
            page.AddItem(new ContextMenuItem
            {
                Group = ContextMenuGroup.Equip,
                Label = "Equip Tool",
                Detail = mayEquip ? null : World?.AllowsWorldEditing == false ? "Tools are off here" : "Not for your role",
                IsEnabled = mayEquip,
                FillColor = mayEquip ? new[] { 0.14f, 0.30f, 0.18f, 0.92f } : new[] { 0.22f, 0.16f, 0.16f, 0.92f },
                OnPressed = mayEquip
                    ? _ =>
                    {
                        grabber.Release(heldGrabbable);
                        hand.EquipToolItem(equipTarget);
                    }
                    : null,
            });
            break; // one equip item even if several tools are held
        }
    }

    private void ToggleCreateNew()
    {
        // The panel closes itself once it has made something, and the next one opens on the category it
        // was left on. A closed panel still holds the path.
        if (_createPanel != null)
            _createCategory = _createPanel.CategoryPath.Value ?? "";
        if (_createPanel is { IsDestroyed: false })
        {
            _createPanel.Slot.Destroy();
            _createPanel = null;
            return;
        }
        if (World?.RootSlot == null)
            return;
        _createPanel = CreateNewPanel.Spawn(World);
        if (_createPanel != null && _createCategory.Length > 0)
            _createPanel.CategoryPath.Value = _createCategory;
    }
}
