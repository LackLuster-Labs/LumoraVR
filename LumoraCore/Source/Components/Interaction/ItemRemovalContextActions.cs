// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Components.UI;

namespace Lumora.Core.Components.Interaction;

// Point at something another person brought into the world and open the menu: a moderator, the host or an admin
// can take it away, in a frozen world as anywhere. The gate decides and this only offers what it would allow, so
// somebody without the right never sees the item. A thing in someone's body or hands, a user root and the world's
// own content are never offered, and the gate refuses them anyway. -xlinka
[ComponentCategory("Interaction")]
public class ItemRemovalContextActions : ContextMenuItemSource
{
    private static readonly float[] RemoveFill = { 0.32f, 0.10f, 0.10f, 0.92f };

    public override void PopulateContextMenu(ContextMenuPage page, ContextMenuContext context)
    {
        var world = World;
        var local = world?.LocalUser;
        if (world == null || local == null || Slot?.ActiveUserRoot?.ActiveUser != local)
            return;

        var item = ItemOf(context?.Target, local);
        if (item == null || !item.MayDestroy())
            return;

        page.AddItem(new ContextMenuItem
        {
            Group = ContextMenuGroup.Content,
            Label = "Remove",
            Detail = OwnerOf(world, item),
            FillColor = RemoveFill,
            OnPressed = _ => Remove(item),
        });
    }

    // The whole thing somebody brought: up from what the beam hit through the parts in that person's byte, stopping
    // short of the world root. Null for the world's own content, anything in a body or hands, and the local user's
    // own things, which have the held menu.
    public static Slot? ItemOf(Slot? hit, User local)
    {
        if (hit == null || hit.IsDestroyed || hit.IsRootSlot || hit.IsLocalElement || hit.ActiveUserRoot != null)
            return null;
        byte owner = hit.ReferenceID.GetUserByte();
        if (!RefIDConstants.IsValidUserByte(owner) || local.HoldsByte(owner))
            return null;
        // Someone else's body parts can read as nobody's on this machine, their cached root lagging or the slot not
        // yet hung anywhere, so the walk up asks for a user root itself and has to end at the world root.
        var up = hit;
        for (; up.Parent != null; up = up.Parent)
        {
            if (up.GetComponent<UserRoot>() != null)
                return null;
        }
        if (!up.IsRootSlot)
            return null;
        var item = hit;
        while (item.Parent is { IsRootSlot: false } parent && parent.ReferenceID.GetUserByte() == owner)
            item = parent;
        return item;
    }

    private static string OwnerOf(World world, Slot item)
    {
        byte owner = item.ReferenceID.GetUserByte();
        foreach (var user in world.GetAllUsers())
        {
            if (user.HoldsByte(owner))
                return $"{user.UserName.Value}'s item";
        }
        return "Someone's item";
    }

    private static void Remove(Slot item)
    {
        if (item.IsDestroyed)
            return;
        try
        {
            item.Destroy();
        }
        catch (UnauthorizedAccessException ex)
        {
            Logging.Logger.Log($"Remove refused for '{item.Name.Value}': {ex.Message}");
        }
    }
}
