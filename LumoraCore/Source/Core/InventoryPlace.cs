// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core;

// One folder in any inventory the account can reach. OwnerId null is the account's own; otherwise a group's id,
// or the account a shared folder belongs to. OwnerName is what the inventory calls that owner ("Lantern Village",
// "bo"), empty for the account's own.
public readonly record struct InventoryPlace(string? OwnerId, string FolderId, string Name, string OwnerName = "")
{
    public static InventoryPlace OwnRoot => new(null, Inventory.Root, "Inventory");

    public bool IsOwn => string.IsNullOrEmpty(OwnerId);

    public bool IsGroup => OwnerId != null && OwnerId.StartsWith("G-", System.StringComparison.Ordinal);

    // Somewhere in the game's own library, which is a group to the services but never a root of the side tree.
    public bool IsLibrary => OwnerId == Inventory.LibraryId;

    public bool IsRoot => FolderId == Inventory.Root;
}
