// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core;

// What the signed-in account may do in one folder: its own (Owner), save and change things (Write, a group's
// builders or a share that lets it save), or only look and take copies (Read). None is a link whose folder was
// unshared or deleted.
public enum InventoryAccess
{
    Owner,
    Write,
    Read,
    None,
}

public static class InventoryAccessNames
{
    public static InventoryAccess Parse(string? wire) => wire?.Trim().ToLowerInvariant() switch
    {
        "write" => InventoryAccess.Write,
        "read" => InventoryAccess.Read,
        "none" => InventoryAccess.None,
        _ => InventoryAccess.Owner,
    };

    public static bool CanChange(this InventoryAccess access) => access is InventoryAccess.Owner or InventoryAccess.Write;
}
