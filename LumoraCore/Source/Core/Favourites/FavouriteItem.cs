// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Core;

// The inventory item a favourite points at, as it was last seen: enough to find it again and to fetch it
// when the service cannot be asked where it went.
//
// Id and Owner are the item itself wherever it lives: Owner null for the account's own, else the group or
// the account it belongs to. A favourite picked from a share or a kept link points at what that row leads
// to, not at the row, so a share given again or a link kept twice is still the same favourite; Via and
// ViaFolder remember the row it was picked from, the quickest way back to it. Folder is the item's own
// folder when it is known, which it is not for something reached through a row. -xlinka
public readonly record struct FavouriteItem(string Id, string Hash, string Name, string? Folder, bool IsWorld, string ItemKind,
    string? Owner = null, string? Via = null, string? ViaFolder = null)
{
    public static FavouriteItem From(in InventoryEntry entry)
    {
        bool through = entry.Link != null;
        return new(entry.TargetId, entry.Hash, entry.Name, through ? null : entry.FolderId, entry.Kind == InventoryEntryKind.World,
            entry.ItemKind, entry.TargetOwnerId, through ? entry.Id : null, through ? entry.FolderId : null);
    }

    // The row is this item: the item itself, or a row that leads to it.
    public bool Is(in InventoryEntry entry)
        => !entry.IsFolder && entry.TargetId == Id && string.Equals(entry.TargetOwnerId, Owner, StringComparison.Ordinal);

    public InventoryEntry ToEntry()
        => new(Id, Name, IsWorld ? InventoryEntryKind.World : InventoryEntryKind.Item, DateTime.MinValue, 0, 0,
            ItemKind, Hash, null, Folder ?? Inventory.Root, null) { OwnerId = Owner };
}
