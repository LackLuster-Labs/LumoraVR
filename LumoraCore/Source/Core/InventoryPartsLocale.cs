// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Localization;

namespace Lumora.Core;

// What the inventory says about a saved item whose parts the server no longer has.
public static class InventoryPartsLocale
{
    // Under the card's name.
    public static LocaleText Missing(int count) => count == 1
        ? LocaleText.Keyed("Inventory.Parts.MissingOne", "1 part missing")
        : LocaleText.Keyed("Inventory.Parts.MissingMany", "{0} parts missing", count);

    // The selected card, for whoever can save over it.
    public static LocaleText FixOwn(int count) => count == 1
        ? LocaleText.Keyed("Inventory.Parts.FixOwnOne",
            "1 part is missing on the server. Open or spawn it and save it again from a copy that loads for you to upload it again.")
        : LocaleText.Keyed("Inventory.Parts.FixOwnMany",
            "{0} parts are missing on the server. Open or spawn it and save it again from a copy that loads for you to upload them again.", count);

    // The selected card, for anyone else.
    public static LocaleText FixOther(int count) => count == 1
        ? LocaleText.Keyed("Inventory.Parts.FixOtherOne",
            "1 part is missing on the server. Whoever saved it has to save it again from a copy that loads for them.")
        : LocaleText.Keyed("Inventory.Parts.FixOtherMany",
            "{0} parts are missing on the server. Whoever saved it has to save it again from a copy that loads for them.", count);

    // A save that went up naming parts neither the server nor this machine has.
    public static LocaleText Lost(string name, int count) => count == 1
        ? LocaleText.Keyed("Inventory.Parts.LostOne",
            "\"{0}\" went up, but 1 part it uses is missing on the server and not on this machine either.", name)
        : LocaleText.Keyed("Inventory.Parts.LostMany",
            "\"{0}\" went up, but {1} parts it uses are missing on the server and not on this machine either.", name, count);
}
