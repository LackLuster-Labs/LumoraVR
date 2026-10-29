// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Localization;

namespace Lumora.Core;

// What the inventory says about folder names and merging folders.
public static class InventoryFolderLocale
{
    public static LocaleText NameTakenHere(string name)
        => LocaleText.Keyed("Inventory.Folder.NameTakenHere", "A folder named \"{0}\" is already here.", name);

    public static LocaleText NameTakenThere(string name, string place)
        => LocaleText.Keyed("Inventory.Folder.NameTakenThere", "A folder named \"{0}\" is already in {1}.", name, place);

    public static LocaleText Merged(string name, string place)
        => LocaleText.Keyed("Inventory.Folder.Merged", "Merged \"{0}\" into the folder of that name in {1}.", name, place);

    public static LocaleText MergeShared(string name)
        => LocaleText.Keyed("Inventory.Folder.MergeShared", "\"{0}\" is shared. Stop sharing it first, then merge.", name);

    public static LocaleText MergeAsked(string name)
        => LocaleText.Keyed("Inventory.Folder.MergeAsked", "Someone asked for \"{0}\" to be deleted. A moderator decides that before it can merge.", name);

    public static readonly LocaleText MergeFailed = LocaleText.Keyed("Inventory.Folder.MergeFailed", "Couldn't merge that.");

    public static LocaleText MergeOffer(string name, string place)
        => LocaleText.Keyed("Inventory.Merge.Offer", "A folder named \"{0}\" is already in {1}. Merge puts everything from both into that one.", name, place);

    public static LocaleText MergeQuestion(string name, string place)
        => LocaleText.Keyed("Inventory.Merge.Question", "Merge \"{0}\" into the one in {1}?", name, place);

    public static readonly LocaleText MergeButton = LocaleText.Keyed("Inventory.Merge.Button", "Merge");
    public static readonly LocaleText Cancel = LocaleText.Keyed("Dialog.Cancel", "Cancel");
}
