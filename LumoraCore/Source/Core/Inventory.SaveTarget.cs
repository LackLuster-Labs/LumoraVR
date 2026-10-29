// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Threading.Tasks;

namespace Lumora.Core;

// Where a save lands. Every save, from the inventory's own buttons, the hand menu, the session page or a chat,
// goes into the folder the inventory window has open, or had open last, the way a file lands in the folder an
// explorer window is showing. A folder this account may not save into (a share it can only look at, a group
// where it is not a builder or has no storage) gives way to the top of its own inventory, and the save's answer
// says so. -xlinka
public static partial class Inventory
{
    private static readonly object OpenGate = new();
    private static InventoryPlace? _open;
    private static string? _openRefusal;
    private static string? _openAccount;

    // The inventory window, whenever it moves: the folder it shows, and why a save could not land there (null
    // when it can). Only for the account signed in now; another account starts again at its own top.
    public static void ShowingFolder(InventoryPlace place, string? refusal)
    {
        lock (OpenGate)
        {
            _open = place;
            _openRefusal = refusal;
            _openAccount = AccountNow;
        }
    }

    // The signed-in account, known as soon as the sign in is, before the sync has its database.
    private static string? AccountNow => InventorySync.Account ?? InventorySync.Service?.Account;

    public static InventoryPlace? OpenFolder
    {
        get
        {
            lock (OpenGate)
                return _openAccount != null && _openAccount == AccountNow ? _open : null;
        }
    }

    // The folder a save goes to now, and what to tell the user when it cannot go where they are looking.
    public static InventoryPlace SavePlace(out string? notice)
    {
        notice = null;
        InventoryPlace? open;
        string? refusal;
        lock (OpenGate)
        {
            bool current = _openAccount != null && _openAccount == AccountNow;
            open = current ? _open : null;
            refusal = current ? _openRefusal : null;
        }
        if (open == null)
            return InventoryPlace.OwnRoot;
        if (refusal == null)
            return open.Value;
        notice = $"{refusal} Saved to your inventory.";
        return InventoryPlace.OwnRoot;
    }

    // A save's answer when it went to your inventory instead: the notice, carrying on with what the save itself
    // adds past the plain "Saved" (still syncing, or files left out).
    public static string WentToOwn(string notice, string saveMessage)
        => saveMessage.EndsWith(SyncingTail, System.StringComparison.Ordinal)
            ? notice.TrimEnd('.') + SyncingTail
            : $"{notice} {saveMessage}";

    internal const string SyncingTail = ", syncing in the background.";

    // Short words for where a save goes now, for a menu: "Props", "Lantern Village", "Inventory".
    public static string SavePlaceLabel()
    {
        var place = SavePlace(out _);
        if (place.IsRoot)
            return place.IsOwn ? "Inventory" : place.OwnerName.Length > 0 ? place.OwnerName : place.Name;
        return place.Name;
    }

    public static Task<InventoryResult> SaveItemHereAsync(Slot slot, string? name)
    {
        var place = SavePlace(out var notice);
        return WithNotice(SaveItemAsync(slot, name, place), notice);
    }

    public static Task<InventoryResult> SaveWorldHereAsync(World world, string? name)
    {
        var place = SavePlace(out var notice);
        return WithNotice(SaveWorldAsync(world, name, place), notice);
    }

    public static Task<InventoryResult> KeepSentHereAsync(InventoryEntry sent)
    {
        var place = SavePlace(out var notice);
        return WithNotice(KeepSentAsync(sent, place), notice);
    }

    private static async Task<InventoryResult> WithNotice(Task<InventoryResult> save, string? notice)
    {
        var result = await save.ConfigureAwait(false);
        if (notice == null || !result.Ok)
            return result;
        return InventoryResult.Success(WentToOwn(notice, result.Message), result.ItemId);
    }
}
