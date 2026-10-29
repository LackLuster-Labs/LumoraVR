// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Localization;

namespace Lumora.Core;

// What the inventory says about the game's own library.
public static class InventoryLibraryLocale
{
    public static readonly LocaleText Badge = LocaleText.Keyed("Inventory.Library.Badge", "Lumora");

    public static readonly LocaleText MetaLook = LocaleText.Keyed("Inventory.Library.MetaLook", "for everyone");

    public static readonly LocaleText MetaStaff = LocaleText.Keyed("Inventory.Library.MetaStaff", "staff · can save");

    public static readonly LocaleText ChipLook = LocaleText.Keyed("Inventory.Library.ChipLook", "Library · look only");

    public static readonly LocaleText ChipStaff = LocaleText.Keyed("Inventory.Library.ChipStaff", "Library · staff");

    public static readonly LocaleText LineLook = LocaleText.Keyed("Inventory.Library.LineLook",
        "the game's own library, in every inventory: open, spawn and wear anything; saves go to your inventory");

    public static LocaleText LineStaff(string used, string limit)
        => LocaleText.Keyed("Inventory.Library.LineStaff", "library storage: {0} of {1}", used, limit);

    public static readonly LocaleText LineStaffSaves = LocaleText.Keyed("Inventory.Library.LineStaffSaves", "staff: saves land here");

    public static readonly LocaleText ViewOnly = LocaleText.Keyed("Inventory.Library.ViewOnly", "The Lumora Library is look only for you.");

    public static readonly LocaleText Stays = LocaleText.Keyed("Inventory.Library.Stays", "The Lumora Library stays at the top of every inventory.");

    public static LocaleText Describe(string name, int count)
        => LocaleText.Keyed("Inventory.Library.Describe", "{0} · the game's own library · {1} things inside", name, count);

    public static LocaleText Requests(int count)
        => LocaleText.Keyed("Inventory.Library.Requests", "Delete requests ({0})", count);
}
