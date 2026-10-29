// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core;

// Something a link card asks the inventory window to show: a folder of OwnerId's to open, or an item already read
// (Item) to spawn, wear or open the way the window does it.
public sealed record InventoryOpenRequest(string OwnerId, string OwnerName, string TargetKind, string TargetId, string Name,
    InventoryEntry? Item = null);
