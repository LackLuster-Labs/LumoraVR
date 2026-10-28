// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// One root of the inventory's side tree: the account's own, or a group it is in, drawn in the group's colour
// with its tag or icon. Note is the short word on the right of the row ("builder", "no storage", "locked");
// CanSave says whether a save may land in it, and Refusal why not. Requests is how many delete requests wait for
// this account to decide, and Warns paints the note in the warning colour (a group that is locked or about to be,
// or that gives this account no storage).
internal readonly record struct InventoryRoot(InventoryPlace Place, string Tag, string? IconHash, color Tint, color Ink, string Note,
    bool IsGroup, bool CanSave, string? Refusal, int Requests = 0, bool Warns = false);
