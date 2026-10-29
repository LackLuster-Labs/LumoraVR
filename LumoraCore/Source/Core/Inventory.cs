// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Lumora.Core.Math;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core;

public enum InventoryEntryKind
{
    Folder,
    Item,
    World,
}

// One thing the inventory can show: a folder, a saved item, or a saved world, as the service lists
// it. Everything the screen needs to draw a card without another round trip.
public readonly struct InventoryEntry
{
    public readonly string Id;
    public readonly string Name;
    public readonly InventoryEntryKind Kind;
    public readonly DateTime ModifiedUtc;
    public readonly long SizeBytes;
    public readonly int ChildCount;
    // What a saved item was: "Avatar" or "Object". For a world, the mode it was saved in. Empty when
    // the service never stamped it.
    public readonly string ItemKind;
    public readonly string Hash;
    public readonly string? ThumbnailHash;
    // The folder this entry sits in, "root" at the top. Search rows also carry a readable Path.
    public readonly string FolderId;
    public readonly string? Path;

    public InventoryEntry(string id, string name, InventoryEntryKind kind, DateTime modifiedUtc, long sizeBytes,
        int childCount, string itemKind, string hash, string? thumbnailHash, string folderId, string? path)
    {
        Id = id;
        Name = name;
        Kind = kind;
        ModifiedUtc = modifiedUtc;
        SizeBytes = sizeBytes;
        ChildCount = childCount;
        ItemKind = itemKind ?? string.Empty;
        Hash = hash ?? string.Empty;
        ThumbnailHash = thumbnailHash;
        FolderId = string.IsNullOrEmpty(folderId) ? Inventory.Root : folderId;
        Path = path;
    }

    // Whose inventory the row sits in: null for the signed-in account's own, else a group's id or the account
    // that shared it.
    public string? OwnerId { get; init; }
    // On a row that leads into someone else's inventory: a kept link, or something shared with this account or
    // its group. The row's Id is then the link's or the share's, and the target is Link.OwnerId/TargetId.
    public InventoryLinkInfo? Link { get; init; }
    // Grants on it, told only to whoever may share it.
    public int ShareCount { get; init; }
    public bool IsPublic { get; init; }
    // Who saved it, when that is not the inventory's owner.
    public string? SavedByName { get; init; }
    // In a group: someone asked for it to be deleted and a moderator has not decided yet.
    public InventoryDeleteMark? DeleteRequest { get; init; }
    // Files it lists that the store no longer has, built-in ones left out. Null when none.
    public IReadOnlyList<string>? MissingParts { get; init; }

    public bool IsBroken => MissingParts is { Count: > 0 };
    public bool IsFolder => Kind == InventoryEntryKind.Folder;
    public bool IsAvatar => Kind == InventoryEntryKind.Item && string.Equals(ItemKind, Inventory.AvatarKind, StringComparison.OrdinalIgnoreCase);
    public bool IsImage => Kind == InventoryEntryKind.Item && string.Equals(ItemKind, Inventory.ImageKind, StringComparison.OrdinalIgnoreCase);
    public bool IsLink => Link != null;
    public bool IsShared => ShareCount > 0 || IsPublic;
    // A link this account keeps, as against something shared straight to it or its group.
    public bool IsKeptLink => Link?.Via == "link";
    // The game's own library, at the top of every inventory.
    public bool IsLibrary => Link?.Via == Inventory.ShareLibrary;
    public InventoryAccess LinkAccess => Link == null ? InventoryAccess.Owner : InventoryAccessNames.Parse(Link.Access);
    // The thing itself, wherever it lives: a link's target, or the row.
    public string TargetId => Link?.TargetId ?? Id;
    public string? TargetOwnerId => Link?.OwnerId ?? OwnerId;
}

public readonly struct InventoryResult
{
    public readonly bool Ok;
    public readonly string Message;
    // The item a save made or wrote over; null for anything else.
    public readonly string? ItemId;
    // The service's code for a refusal the caller can act on, such as Inventory.NameTakenCode; null otherwise.
    public string? Code { get; init; }
    public InventoryResult(bool ok, string message, string? itemId = null) { Ok = ok; Message = message; ItemId = itemId; Code = null; }
    public static InventoryResult Success(string message, string? itemId = null) => new(true, message, itemId);
    public static InventoryResult Failure(string message) => new(false, message);
}

public sealed class InventoryListing
{
    public readonly List<InventoryEntry> Entries = new();
    public string? Problem;
    // The account's in the folder listed, and whether it may share what is in it.
    public InventoryAccess Access = InventoryAccess.Owner;
    public bool CanShare = true;
}

// The inventory is the account's, kept on the assets service, but a save is made on this machine first:
// it is stored in the local database and listed at once, and InventorySync takes it up in the background,
// one blob per file by hash and an item in a folder. So a save never waits on the network, only on a
// signed in account, whose saves are all the queue ever sends. Listings are the service's with this
// account's saves that are not up yet laid over them, and fetching serves one of those from this machine
// and anything else from the service, into the local asset cache to load the way a file would. -xlinka
public static partial class Inventory
{
    public const string Root = LumoraClient.InventoryRoot;
    public const string AvatarKind = "Avatar";
    public const string ImageKind = "Image";
    public const string ObjectKind = "Object";
    public const string WorldKind = "World";
    private const string ItemExtension = ".litem";
    private const string WorldExtension = ".lworld";
    private const int ThumbnailWidth = 256;
    private const int ThumbnailHeight = 144;

    // hash -> local file path of a blob already brought down this run.
    private static readonly ConcurrentDictionary<string, string> Cached = new(StringComparer.OrdinalIgnoreCase);

    public static LumoraClient? Client => Engine.Current?.CDNClient;
    public static bool IsSignedIn => InventorySync.SignedIn;

    internal static InventoryResult NotSignedIn() => InventoryResult.Failure("Sign in with the profile widget on the workspace to use your inventory.");

    // LISTING

    // The folder as the service lists it, with this account's saves that are not up yet in it as well. They
    // show when the service cannot be reached too, beside the reason it could not.
    public static Task<InventoryListing> ListAsync(string folderId) => ListAsync(folderId, null);

    // A folder of any inventory the account can reach: its own (ownerId null), a group's, or one shared with it.
    public static async Task<InventoryListing> ListAsync(string folderId, string? ownerId)
    {
        var listing = new InventoryListing();
        var service = InventorySync.Service;
        if (service == null || !service.SignedIn)
        {
            listing.Problem = NotSignedIn().Message;
            return listing;
        }
        var result = await service.ListAsync(folderId, ownerId).ConfigureAwait(false);
        if (result.Failed || result.Data == null)
            listing.Problem = result.Message ?? "The inventory service did not answer.";
        else
            Fill(listing, result.Data, ownerId);
        InventorySync.MergeInto(listing, folderId, ownerId);
        return listing;
    }

    public static Task<InventoryListing> SearchAsync(string query) => SearchAsync(query, null);

    public static async Task<InventoryListing> SearchAsync(string query, string? ownerId)
    {
        var listing = new InventoryListing();
        var service = InventorySync.Service;
        if (service == null || !service.SignedIn)
        {
            listing.Problem = NotSignedIn().Message;
            return listing;
        }
        var result = await service.SearchAsync(query, ownerId).ConfigureAwait(false);
        if (result.Failed || result.Data == null)
            listing.Problem = result.Message ?? "The inventory service did not answer.";
        else
            Fill(listing, result.Data, ownerId);
        InventorySync.MergeSearch(listing, query, ownerId);
        return listing;
    }

    // Each row under the folder the service says it is in, so a search hit knows where it lives; null is the
    // top. An item shows its whole size, not just its saved file's. A row that leads elsewhere keeps where it
    // leads. -xlinka
    private static void Fill(InventoryListing listing, FolderContents contents, string? ownerId)
    {
        listing.Access = InventoryAccessNames.Parse(contents.Access);
        listing.CanShare = contents.CanShare || listing.Access == InventoryAccess.Owner;
        foreach (var folder in contents.Folders)
        {
            listing.Entries.Add(new InventoryEntry(folder.Id, folder.Name, InventoryEntryKind.Folder, folder.CreatedAt,
                0, folder.ItemCount + folder.FolderCount, string.Empty, string.Empty, null, folder.ParentId ?? Root, folder.Path)
            {
                OwnerId = ownerId,
                Link = folder.Link,
                ShareCount = folder.ShareCount,
                IsPublic = folder.IsPublic,
                DeleteRequest = folder.DeleteRequest,
            });
        }
        foreach (var item in contents.Items)
        {
            bool world = string.Equals(item.Kind, WorldKind, StringComparison.OrdinalIgnoreCase);
            string itemKind = world ? (item.WorldMode ?? string.Empty) : item.Kind;
            long size = item.TotalBytes > 0 ? item.TotalBytes : item.FileBytes;
            var missing = MissingOf(item.MissingParts);
            listing.Entries.Add(new InventoryEntry(item.Id, item.Name, world ? InventoryEntryKind.World : InventoryEntryKind.Item,
                item.UpdatedAt, size, 0, itemKind, item.FileHash, item.ThumbnailHash, item.FolderId ?? Root, item.Path)
            {
                OwnerId = ownerId,
                Link = item.Link,
                ShareCount = item.ShareCount,
                IsPublic = item.IsPublic,
                SavedByName = item.SavedByName,
                DeleteRequest = item.DeleteRequest,
                MissingParts = missing,
            });
            // The account's own item: whatever of it this machine still has goes back up on its own.
            if (missing != null && string.IsNullOrEmpty(ownerId) && item.Link == null)
                InventorySync.Repair(missing);
        }
    }

    // The parts a listing says an item lost, as hashes the store names, each once; a built-in file is the build's
    // and never missing. Null when none are left.
    internal static IReadOnlyList<string>? MissingOf(List<string>? parts)
    {
        if (parts == null || parts.Count == 0)
            return null;
        var kept = new List<string>(parts.Count);
        foreach (var part in parts)
        {
            var hash = part?.Trim().ToLowerInvariant();
            if (hash == null || hash.Length != 64 || !IsHex(hash) || kept.Contains(hash) || Assets.BuiltinAssetRegistry.ResUriFor(hash) != null)
                continue;
            kept.Add(hash);
        }
        return kept.Count == 0 ? null : kept;
    }

    private static bool IsHex(string text)
    {
        foreach (var c in text)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        }
        return true;
    }

    // One item row as the inventory shows it, from the service's answer about it.
    internal static InventoryEntry EntryOf(InventoryItemInfo item, string? ownerId)
    {
        bool world = string.Equals(item.Kind, WorldKind, StringComparison.OrdinalIgnoreCase);
        long size = item.TotalBytes > 0 ? item.TotalBytes : item.FileBytes;
        return new InventoryEntry(item.Id, item.Name, world ? InventoryEntryKind.World : InventoryEntryKind.Item, item.UpdatedAt, size, 0,
            world ? (item.WorldMode ?? string.Empty) : item.Kind, item.FileHash, item.ThumbnailHash, item.FolderId ?? Root, item.Path)
        {
            OwnerId = ownerId,
            SavedByName = item.SavedByName,
            MissingParts = MissingOf(item.MissingParts),
        };
    }

    // FOLDERS AND MOVES

    // The service's refusal when a folder beside it holds the name already, whatever its case.
    public const string NameTakenCode = "name-taken";

    // Under an id made here, so a request answered too late to be seen cannot make the folder twice. A name a
    // folder there holds already is refused with NameTakenCode.
    public static Task<InventoryResult> CreateFolderAsync(string parentId, string name) => CreateFolderAsync(parentId, name, null);

    public static async Task<InventoryResult> CreateFolderAsync(string parentId, string name, string? ownerId)
    {
        if (!ValidName(name, out var error))
            return InventoryResult.Failure(error!);
        name = CleanName(name);
        var client = Client;
        if (client == null || !client.IsAuthenticated)
            return NotSignedIn();
        var result = await client.UpsertFolder(Guid.NewGuid().ToString(), name, parentId, ownerId).ConfigureAwait(false);
        if (result.Error == NameTakenCode)
            return NameTaken(InventoryFolderLocale.NameTakenHere(RefusalField(result, "name") ?? name).Resolve());
        if (result.Error == AccountEmail.NotVerifiedCode)
        {
            AccountEmail.Refused(saves: true, joins: false);
            return new InventoryResult(false, AccountEmailLocale.SaveRefused.Resolve()) { Code = AccountEmail.NotVerifiedCode };
        }
        return result.Success
            ? InventoryResult.Success($"Folder \"{name}\" is here.")
            : InventoryResult.Failure(result.Message ?? "Couldn't make that folder.");
    }

    private static InventoryResult NameTaken(string message) => new(false, message) { Code = NameTakenCode };

    // One field of the service's refusal, such as the name of the folder in the way; null when it sent none.
    private static string? RefusalField(Lumora.Nexus.Cloud.Cdn.ApiResponse refusal, string key)
    {
        if (string.IsNullOrEmpty(refusal.RawBody))
            return null;
        try
        {
            using var body = System.Text.Json.JsonDocument.Parse(refusal.RawBody);
            return body.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && body.RootElement.TryGetProperty(key, out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // A save that has not gone up yet is changed here, everything else on the service. A link kept here is
    // renamed as a link; something shared straight to the account is its sharer's to name.
    public static async Task<InventoryResult> RenameAsync(InventoryEntry entry, string name)
    {
        if (!ValidName(name, out var error))
            return InventoryResult.Failure(error!);
        name = CleanName(name);
        if (!IsSignedIn)
            return NotSignedIn();
        if (entry.IsLibrary)
            return InventoryResult.Failure(InventoryLibraryLocale.Stays.Resolve());
        if (entry.IsLink && !entry.IsKeptLink)
            return InventoryResult.Failure($"\"{entry.Name}\" is shared with you; only {OwnerLabel(entry)} can rename it.");
        if (!entry.IsFolder && !entry.IsLink && await InventorySync.RenameAsync(entry.Id, name).ConfigureAwait(false) is { } renamed)
            return renamed;
        var client = Client;
        if (client == null || !client.IsAuthenticated)
            return NotSignedIn();
        var result = entry.IsLink
            ? await client.UpdateInventoryLink(entry.Id, name, null).ConfigureAwait(false)
            : entry.IsFolder
                ? await client.RenameFolder(entry.Id, name, entry.OwnerId).ConfigureAwait(false)
                : await client.RenameInventoryItem(entry.Id, name, entry.OwnerId).ConfigureAwait(false);
        if (entry.IsFolder && result.Error == NameTakenCode)
            return NameTaken(InventoryFolderLocale.NameTakenHere(RefusalField(result, "name") ?? name).Resolve());
        return result.Success
            ? InventoryResult.Success($"Renamed to \"{name}\".")
            : InventoryResult.Failure(result.Message ?? "Couldn't rename that.");
    }

    // Within one inventory only: something saved in a group stays the group's, and a copy is how it travels.
    public static Task<InventoryResult> MoveAsync(InventoryEntry entry, string targetFolderId, string targetName)
        => MoveAsync(entry, targetFolderId, targetName, entry.OwnerId);

    public static async Task<InventoryResult> MoveAsync(InventoryEntry entry, string targetFolderId, string targetName, string? targetOwnerId)
    {
        if (!IsSignedIn)
            return NotSignedIn();
        if (entry.IsLibrary)
            return InventoryResult.Failure(InventoryLibraryLocale.Stays.Resolve());
        if (!SameOwner(entry.OwnerId, targetOwnerId))
            return InventoryResult.Failure($"\"{entry.Name}\" can only move inside the inventory it is in.");
        if (entry.IsLink && !entry.IsKeptLink)
            return InventoryResult.Failure($"\"{entry.Name}\" is shared with you and stays at the top of your inventory.");
        if (string.Equals(entry.FolderId, targetFolderId, StringComparison.Ordinal))
            return InventoryResult.Success("It is already there.");
        if (entry.IsFolder && string.Equals(entry.Id, targetFolderId, StringComparison.Ordinal))
            return InventoryResult.Failure("A folder cannot go inside itself.");
        if (!entry.IsFolder && !entry.IsLink && await InventorySync.MoveAsync(entry.Id, targetFolderId, targetName).ConfigureAwait(false) is { } moved)
            return moved;
        var client = Client;
        if (client == null || !client.IsAuthenticated)
            return NotSignedIn();
        var result = entry.IsLink
            ? await client.UpdateInventoryLink(entry.Id, null, targetFolderId).ConfigureAwait(false)
            : entry.IsFolder
                ? await client.MoveFolder(entry.Id, targetFolderId, entry.OwnerId).ConfigureAwait(false)
                : await client.MoveItemToFolder(entry.Id, targetFolderId, entry.OwnerId).ConfigureAwait(false);
        // The screen offers to merge the two instead.
        if (entry.IsFolder && result.Error == NameTakenCode)
            return NameTaken(InventoryFolderLocale.NameTakenThere(RefusalField(result, "name") ?? entry.Name, targetName).Resolve());
        return result.Success
            ? InventoryResult.Success($"Moved \"{entry.Name}\" to {targetName}.")
            : InventoryResult.Failure(result.Message ?? "Couldn't move that.");
    }

    // The folder into the target, merged into the folder of its name there, so everything in both ends up in that
    // one; with no such folder there it is a plain move. The saves made here that were still going into a folder
    // that went follow it into the one it went into. -xlinka
    public static async Task<InventoryResult> MergeAsync(InventoryEntry entry, string targetFolderId, string targetName)
    {
        if (!IsSignedIn)
            return NotSignedIn();
        if (!entry.IsFolder || entry.IsLink)
            return InventoryResult.Failure(InventoryFolderLocale.MergeFailed.Resolve());
        var client = Client;
        if (client == null || !client.IsAuthenticated)
            return NotSignedIn();
        var result = await client.MergeFolder(entry.Id, targetFolderId, entry.OwnerId).ConfigureAwait(false);
        if (result.Failed)
        {
            string held = RefusalField(result, "name") ?? entry.Name;
            return InventoryResult.Failure(result.Error switch
            {
                "shared" => InventoryFolderLocale.MergeShared(held).Resolve(),
                "delete-requested" => InventoryFolderLocale.MergeAsked(held).Resolve(),
                _ => result.Message ?? InventoryFolderLocale.MergeFailed.Resolve(),
            });
        }
        var folded = result.Data?.Folded;
        if (folded != null)
        {
            foreach (var fold in folded)
                await InventorySync.RefolderAsync(fold.From, fold.Into).ConfigureAwait(false);
        }
        return folded is { Count: > 0 }
            ? InventoryResult.Success(InventoryFolderLocale.Merged(entry.Name, targetName).Resolve())
            : InventoryResult.Success($"Moved \"{entry.Name}\" to {targetName}.");
    }

    // A kept link goes and its folder stays where it is. Something shared straight to the account is taken out
    // of its inventory; a group's share stays for as long as the account is in the group.
    public static async Task<InventoryResult> DeleteAsync(InventoryEntry entry)
    {
        if (!IsSignedIn)
            return NotSignedIn();
        var client = Client;
        if (entry.IsLink)
        {
            if (client == null || !client.IsAuthenticated)
                return NotSignedIn();
            if (entry.IsLibrary)
                return InventoryResult.Failure(InventoryLibraryLocale.Stays.Resolve());
            if (entry.Link!.Via == "group")
                return InventoryResult.Failure($"\"{entry.Name}\" is shared with {entry.Link.ViaName ?? "your group"}; it stays while you are in it.");
            var gone = entry.IsKeptLink
                ? await client.DeleteInventoryLink(entry.Id).ConfigureAwait(false)
                : await client.DeclineInventoryShare(entry.Id).ConfigureAwait(false);
            return gone.Success
                ? InventoryResult.Success($"\"{entry.Name}\" is out of your inventory. {OwnerLabel(entry)} still has it.")
                : InventoryResult.Failure(gone.Message ?? "Couldn't take that out.");
        }
        if (!entry.IsFolder && await InventorySync.DeleteAsync(entry.Id).ConfigureAwait(false) is { } deleted)
            return deleted;
        if (client == null || !client.IsAuthenticated)
            return NotSignedIn();
        var result = entry.IsFolder
            ? await client.DeleteFolder(entry.Id, entry.OwnerId).ConfigureAwait(false)
            : await client.DeleteInventoryItem(entry.Id, entry.OwnerId).ConfigureAwait(false);
        // A group's builders ask a moderator rather than delete.
        if (result.Error == "needs-approval")
            return await RequestDeleteAsync(entry).ConfigureAwait(false);
        if (result.Failed)
            return InventoryResult.Failure(result.Message ?? "Couldn't delete that.");
        if (entry.IsFolder)
            await InventorySync.DropFolderAsync(entry.Id).ConfigureAwait(false);
        InventorySync.SpaceFreed();
        return InventoryResult.Success($"Deleted \"{entry.Name}\".");
    }

    private static bool SameOwner(string? a, string? b)
        => string.IsNullOrEmpty(a) ? string.IsNullOrEmpty(b) || b == InventorySync.Account : string.Equals(a, b, StringComparison.Ordinal);

    // "bo", "Lantern Village": who a shared row belongs to, for a line of text.
    public static string OwnerLabel(in InventoryEntry entry)
        => entry.Link is { OwnerName.Length: > 0 } link ? link.OwnerName : "its owner";

    // SAVING

    // Serialises on the calling (world) thread, then keeps the save on this machine off it, and the sync
    // takes it up from there. The permission question is asked here as well as in the UI, so a route added
    // later cannot skip it. -xlinka
    public static Task<InventoryResult> SaveItemAsync(Slot slot, string? name, string? folderId)
        => SaveItemAsync(slot, name, OwnFolder(folderId), null);

    public static Task<InventoryResult> SaveItemAsync(Slot slot, string? name, string? folderId, byte[]? picture)
        => SaveItemAsync(slot, name, OwnFolder(folderId), picture);

    // A picture handed over (a photo's own image) is made into the card the way any picture's is; anything else
    // has its picture taken here, once it is packed, so what is saved is the thing as it was when the save was
    // asked for.
    public static Task<InventoryResult> SaveItemAsync(Slot slot, string? name, InventoryPlace place, byte[]? picture = null)
    {
        if (slot == null || slot.IsDestroyed || slot.World == null)
            return Task.FromResult(InventoryResult.Failure("There is nothing to save."));
        if (!Components.ItemProtection.AllowsSaveCopy(slot, null, out var refusal))
            return Task.FromResult(InventoryResult.Failure(refusal ?? "That cannot be saved."));
        if (!IsSignedIn)
            return Task.FromResult(NotSignedIn());

        string label = CleanName(name, CleanName(slot.Name));
        string kind = ItemKindOf(slot);
        Persistence.DataTreeDictionary tree;
        try
        {
            tree = slot.SaveObject(Persistence.DependencyHandling.CollectAssets).Root;
        }
        catch (Exception ex)
        {
            return Task.FromResult(InventoryResult.Failure($"Couldn't pack \"{label}\": {ex.Message}"));
        }
        var image = picture == null && kind == ImageKind ? PictureOf(slot)?.URL.Value : null;
        return SavePicturedAsync(slot, tree, label, kind, place, picture, image);
    }

    private static InventoryPlace OwnFolder(string? folderId) => new(null, string.IsNullOrEmpty(folderId) ? Root : folderId!, string.Empty);

    // A picture's card is the picture fitted into a card, from the image handed over or from its file; anything
    // else, or a picture whose file is not on this machine, has its picture taken.
    //
    // Never the handed image itself. The store files by content, so a photo kept as its own card was the photo's
    // file twice over, and once the save was up dropping the card's copy deleted the photo out from under the
    // world it stood in; a save of that world went up without it. Every chat and inventory card fetched the
    // whole photo too. -xlinka
    private static async Task<InventoryResult> SavePicturedAsync(Slot slot, Persistence.DataTreeDictionary tree, string label,
        string kind, InventoryPlace place, byte[]? picture, Uri? image)
    {
        byte[]? card = null;
        // Read and decoded away from the world: a large picture is a long decode.
        if (picture != null)
            card = await Task.Run(() => Photos.ItemThumbnail.FromImage(picture)).ConfigureAwait(false);
        else if (image != null && image.Scheme == "local" && Engine.Current?.LocalDB is { } db)
        {
            card = await Task.Run(async () => await db.ReadAssetBytesAsync(image.ToString()).ConfigureAwait(false) is { } file
                ? Photos.ItemThumbnail.FromImage(file)
                : null).ConfigureAwait(false);
        }
        card ??= await Photos.ItemThumbnail.CaptureAsync(slot).ConfigureAwait(false);
        return await InventorySync.SaveAsync(tree, ItemExtension, label, kind, place, card, null).ConfigureAwait(false);
    }

    // The folder of that name at the top, made the first time it is asked for; null when that cannot be done. A
    // make refused because the name is taken means another device made it meanwhile, and the second look finds it.
    public static async Task<string?> TopFolderAsync(string name)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var listing = await ListAsync(Root).ConfigureAwait(false);
            foreach (var entry in listing.Entries)
            {
                if (entry.IsFolder && !entry.IsLink && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                    return entry.Id;
            }
            if (attempt == 0 && await CreateFolderAsync(Root, name).ConfigureAwait(false) is { Ok: false } made && made.Code != NameTakenCode)
                return null;
        }
        return null;
    }

    // The world as it is now, with a fresh picture of it. Both are kept here and go up as blobs; the item
    // links them. With an item id it writes over that item instead of making a new one.
    public static Task<InventoryResult> SaveWorldAsync(World world, string? name, string? folderId, string? itemId = null)
        => SaveWorldAsync(world, name, OwnFolder(folderId), itemId);

    public static Task<InventoryResult> SaveWorldAsync(World world, string? name, InventoryPlace place, string? itemId = null)
    {
        if (world == null || world.IsDestroyed)
            return Task.FromResult(InventoryResult.Failure("There is no world to save."));
        if (world.IsSessionStartPending)
            return Task.FromResult(InventoryResult.Failure("That world hasn't finished loading."));
        if (!IsSignedIn)
            return Task.FromResult(NotSignedIn());

        string label = CleanName(name, CleanName(world.Name, WorldKind));
        Persistence.DataTreeDictionary tree;
        try
        {
            tree = world.SaveWorld();
        }
        catch (Exception ex)
        {
            return Task.FromResult(InventoryResult.Failure($"Couldn't pack \"{label}\": {ex.Message}"));
        }
        return InventorySync.SaveAsync(tree, WorldExtension, label, WorldKind, place, CardOf(world), world.Mode.ToString(), itemId);
    }

    // The view only pictures the world on it, so a world saved from the background used to go up with a picture
    // of wherever its saver stood. Any other world, or one with no clean view taken yet, has the last picture of
    // its own: this machine's from when it was on view, or the one its session goes out with. -xlinka
    private static byte[]? CardOf(World world)
    {
        try
        {
            var input = Engine.Current?.InputInterface;
            if (ReferenceEquals(Engine.Current?.WorldManager?.FocusedWorld, world) && input != null
                && input.TryCaptureWorldView(ThumbnailWidth, ThumbnailHeight, out var jpeg) && jpeg != null && jpeg.Length > 0)
                return jpeg;
            return world.PreviewThumbnailBase64 is { Length: > 0 } kept ? Convert.FromBase64String(kept) : null;
        }
        catch
        {
            return null;
        }
    }

    // Something a friend sent in a chat, kept: fetched by its hash and saved the way any save is, its picture
    // with it. Nothing that is up already goes up again, and from then on its files count as this account's,
    // so it outlives the chat and the sender deleting theirs.
    public static Task<InventoryResult> KeepSentAsync(InventoryEntry sent, string? folderId) => KeepSentAsync(sent, OwnFolder(folderId));

    public static async Task<InventoryResult> KeepSentAsync(InventoryEntry sent, InventoryPlace place)
    {
        if (!IsSignedIn)
            return NotSignedIn();
        var (path, problem) = await FetchToCacheAsync(sent).ConfigureAwait(false);
        if (path == null)
            return InventoryResult.Failure(problem ?? $"Couldn't fetch \"{sent.Name}\".");
        Persistence.DataTreeDictionary? tree;
        try
        {
            tree = Persistence.DataTreeConverter.LoadFromBytes(Persistence.LocalEncryption.Decrypt(await File.ReadAllBytesAsync(path).ConfigureAwait(false)))
                as Persistence.DataTreeDictionary;
        }
        catch (Exception ex)
        {
            return InventoryResult.Failure($"Couldn't read \"{sent.Name}\": {ex.Message}");
        }
        if (tree == null)
            return InventoryResult.Failure($"Couldn't read \"{sent.Name}\".");
        // Keeping is a copy: another account's sealed thing stays theirs.
        var keeper = InventorySync.Account ?? Client?.AccountUserId;
        foreach (var owner in Security.SealTree.OwnersIn(tree))
        {
            if (!string.Equals(owner, keeper, StringComparison.Ordinal))
                return InventoryResult.Failure(Security.ProtectionLocale.By(Security.ItemSeals.NameOf(null, owner)).Resolve());
        }
        var picture = await FetchThumbnailAsync(sent).ConfigureAwait(false);
        bool world = sent.Kind == InventoryEntryKind.World;
        string kind = world ? WorldKind : sent.ItemKind.Length > 0 ? sent.ItemKind : ObjectKind;
        return await InventorySync.SaveAsync(tree, world ? WorldExtension : ItemExtension, CleanName(sent.Name), kind, place, picture,
            world ? WorldModeOrNull(sent.ItemKind) : null).ConfigureAwait(false);
    }

    // A world's ItemKind is its mode. Anything that is not a mode, such as the kind name "World" a chat item
    // used to carry in its place, is no mode at all.
    public static string? WorldModeOrNull(string? mode)
        => Enum.TryParse<WorldMode>(mode, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            && !int.TryParse(mode, out _) ? parsed.ToString() : null;

    // FETCHING

    // The blob for an entry, as a file in the local cache. The same hash is only fetched once per run.
    public static async Task<(string? path, string? problem)> FetchToCacheAsync(InventoryEntry entry)
    {
        // A save that has not gone up yet is on this machine already. One that has is the service's, under the
        // hash it went up as, and its copy here is dropped once nothing else needs it.
        var hash = entry.Hash;
        if (InventorySync.Find(entry.Id) is { } row)
        {
            if (row.State != InventoryRowState.Synced || string.IsNullOrEmpty(row.CloudHash))
                return await FetchLocalAsync(row).ConfigureAwait(false);
            hash = row.CloudHash!;
        }
        if (string.IsNullOrEmpty(hash))
            return (null, "That item has no content.");
        if (Cached.TryGetValue(hash, out var have) && File.Exists(have))
            return (have, null);
        var client = Client;
        var db = Engine.Current?.LocalDB;
        if (client == null || !client.IsAuthenticated)
            return (null, NotSignedIn().Message);
        if (db == null)
            return (null, "The local cache isn't up yet.");
        var fetched = await client.FetchContent(hash).ConfigureAwait(false);
        if (fetched.Failed || fetched.Data == null)
            return (null, fetched.Message ?? "The download did not go through.");

        // Everything the graph names comes down before the graph is loaded, so the provider finds
        // every asset in the local database and nothing pops in late or stays blank.
        try
        {
            if (Persistence.DataTreeConverter.LoadFromBytes(Persistence.LocalEncryption.Decrypt(fetched.Data)) is Persistence.DataTreeDictionary tree)
            {
                var problems = new List<string>();
                await Persistence.CloudAssetPacker.PrefetchAsync(tree, client, db, problems).ConfigureAwait(false);
                if (problems.Count > 0)
                    Logging.Logger.Warn($"Inventory: \"{entry.Name}\" is missing {problems.Count} asset(s): {string.Join("; ", problems)}");
            }
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"Inventory: could not read the asset list of \"{entry.Name}\": {ex.Message}");
        }

        try
        {
            var path = db.GetTempFilePath();
            await File.WriteAllBytesAsync(path, SealedCopy(fetched.Data)).ConfigureAwait(false);
            Cached[hash] = path;
            return (path, null);
        }
        catch (Exception ex)
        {
            return (null, $"Couldn't keep the download: {ex.Message}");
        }
    }

    // Copies handed to the loaders sit in the database's temp folder, sealed like everything else there, under
    // a name that says nothing. Every reader of them decrypts (ReadItem, WorldStorage.ReadTree). -xlinka
    private static byte[] SealedCopy(byte[] bytes)
        => Persistence.LocalEncryption.IsEncrypted(bytes) ? bytes : Persistence.LocalEncryption.Encrypt(bytes);

    // The saved copy, read through the database so a copy kept encrypted comes out plain, into a file of
    // its own: whatever opens it may write it back, and the database's file must stay as it was.
    private static async Task<(string? path, string? problem)> FetchLocalAsync(InventoryRow row)
    {
        if (Cached.TryGetValue(row.LocalUri, out var have) && File.Exists(have))
            return (have, null);
        var db = Engine.Current?.LocalDB;
        if (db == null)
            return (null, "The local cache isn't up yet.");
        var bytes = await db.ReadAssetBytesAsync(row.LocalUri).ConfigureAwait(false);
        if (bytes == null)
            return (null, $"The saved copy of \"{row.Name}\" is no longer on this machine.");
        try
        {
            var path = db.GetTempFilePath();
            await File.WriteAllBytesAsync(path, SealedCopy(bytes)).ConfigureAwait(false);
            Cached[row.LocalUri] = path;
            return (path, null);
        }
        catch (Exception ex)
        {
            return (null, $"Couldn't open the saved copy: {ex.Message}");
        }
    }

    // The picture is read here while this machine keeps it; once the save is up the copy may be gone, and the
    // service has it under the same hash.
    public static async Task<byte[]?> FetchThumbnailAsync(InventoryEntry entry)
    {
        if (InventorySync.Find(entry.Id) is { ThumbnailUri: { } picture } && Engine.Current?.LocalDB is { } db
            && await db.ReadAssetBytesAsync(picture).ConfigureAwait(false) is { } kept)
            return kept;
        if (string.IsNullOrEmpty(entry.ThumbnailHash))
            return null;
        var client = Client;
        if (client == null || !client.IsAuthenticated)
            return null;
        var fetched = await client.FetchContent(entry.ThumbnailHash!).ConfigureAwait(false);
        return fetched.Success ? fetched.Data : null;
    }

    // The image an image item is, by the hash its file is filed under: what a picture chosen out of the
    // inventory is set to. Its card is only the listing's small picture of it, and a profile or a group set
    // to that showed a 256 square of the picture, padded to fit. Read from the saved object itself, the one
    // image on its own quad that PictureOf finds; null for anything else, or when the save cannot be read.
    // -xlinka
    public static async Task<string?> ImageHashAsync(InventoryEntry entry)
    {
        if (!entry.IsImage)
            return null;
        byte[]? bytes = null;
        string hash = entry.Hash;
        if (InventorySync.Find(entry.Id) is { } row)
        {
            if (row.State != InventoryRowState.Synced || string.IsNullOrEmpty(row.CloudHash))
                bytes = Engine.Current?.LocalDB is { } db ? await db.ReadAssetBytesAsync(row.LocalUri).ConfigureAwait(false) : null;
            else
                hash = row.CloudHash!;
        }
        if (bytes == null && !string.IsNullOrEmpty(hash))
        {
            if (Cached.TryGetValue(hash, out var kept) && File.Exists(kept))
                bytes = Persistence.LocalEncryption.Decrypt(await File.ReadAllBytesAsync(kept).ConfigureAwait(false));
            else if (Client is { IsAuthenticated: true } client)
            {
                var fetched = await client.FetchContent(hash).ConfigureAwait(false);
                bytes = fetched.Success ? fetched.Data : null;
            }
        }
        if (bytes == null)
            return null;

        Persistence.DataTreeDictionary? tree;
        try
        {
            tree = Persistence.DataTreeConverter.LoadFromBytes(Persistence.LocalEncryption.Decrypt(bytes)) as Persistence.DataTreeDictionary;
        }
        catch (Exception)
        {
            return null;
        }
        if (tree?.TryGetNode("Object") is not Persistence.DataTreeDictionary saved || saved.TryGetList("Components") is not { } components)
            return null;
        foreach (var node in components.Children)
        {
            if (node is Persistence.DataTreeDictionary component
                && component.TryGetNode("Type") is Persistence.DataTreeValue { Value: string type }
                && type == typeof(Assets.ImageProvider).FullName
                && component.TryGetNode("Data") is Persistence.DataTreeDictionary data
                && data.TryGetNode("URL") is Persistence.DataTreeValue { IsUrl: true } url)
                return ContentHashOf(((string)url.Value!).Substring(1));
        }
        return null;
    }

    // A lumora address names its hash; a local one is this machine's file under the same hash, the one the
    // service files it under once it is up.
    private static string? ContentHashOf(string address)
    {
        if (address.StartsWith(Persistence.CloudAssetPacker.CloudScheme + ":", StringComparison.Ordinal))
            return Uri.TryCreate(address, UriKind.Absolute, out var uri) ? Assets.CloudVariants.HashOf(uri) : null;
        if (!address.StartsWith("local://", StringComparison.Ordinal))
            return null;
        var key = address.Substring(address.LastIndexOf('/') + 1);
        return Assets.TextureGpuCache.NamesItsContent(key) && key.IndexOf('.') < 0 ? key.ToLowerInvariant() : null;
    }

    // Loads a fetched item into the world. Must run on the world's update thread; the fetch happens
    // before, on whatever thread the caller awaited on.
    public static Slot? LoadItem(World world, string path, string name)
    {
        if (world?.RootSlot == null || !File.Exists(path))
            return null;
        try
        {
            // What someone takes out of their inventory is theirs, on the host as on a guest.
            using var own = world.EnterSpawnAllocation();
            var slot = world.RootSlot.AddSlot(string.IsNullOrWhiteSpace(name) ? "Item" : name);
            slot.LoadObjectFromFile(path);
            return slot;
        }
        catch (Exception ex)
        {
            Logging.Logger.Error($"Inventory: failed to load '{name}': {ex.Message}");
            return null;
        }
    }

    // A fetched item's saved tree: read, decrypted and parsed, on any thread. That much of a big item's spawn
    // needs no world, and it was a good part of the spawn frame. Null when the file holds no item.
    public static Persistence.DataTreeDictionary? ReadItem(string path)
    {
        if (!File.Exists(path))
            return null;
        return Persistence.DataTreeConverter.LoadFromBytes(Persistence.LocalEncryption.Decrypt(File.ReadAllBytes(path)))
            as Persistence.DataTreeDictionary;
    }

    // Loads an item already read (ReadItem) into the world. Must run on the world's update thread.
    public static Slot? LoadItem(World world, Persistence.DataTreeDictionary tree, string name)
    {
        if (world?.RootSlot == null)
            return null;
        try
        {
            // What someone takes out of their inventory is theirs, on the host as on a guest.
            using var own = world.EnterSpawnAllocation();
            var slot = world.RootSlot.AddSlot(string.IsNullOrWhiteSpace(name) ? "Item" : name);
            slot.LoadObject(tree);
            return slot;
        }
        catch (Exception ex)
        {
            Logging.Logger.Error($"Inventory: failed to load '{name}': {ex.Message}");
            return null;
        }
    }

    // The same over several frames when it is big (Slot.LoadObjectStaged): the slot comes back at once, standing
    // where the item was saved; whenStarted may move it, and whenDone hears it whole, or null when it failed.
    // Must run on the world's update thread.
    public static Slot? LoadItemStaged(World world, Persistence.DataTreeDictionary tree, string name, Action<Slot>? whenStarted, Action<Slot?> whenDone)
    {
        if (world?.RootSlot == null)
            return null;
        Slot? slot = null;
        try
        {
            using (world.EnterSpawnAllocation())
                slot = world.RootSlot.AddSlot(string.IsNullOrWhiteSpace(name) ? "Item" : name);
            // What someone takes out of their inventory is theirs, on the host as on a guest, every bite of it.
            var spawned = slot;
            slot.LoadObjectStaged(tree, world.EnterSpawnAllocation, whenStarted, loaded =>
            {
                // Half an item is not left standing in the world.
                if (loaded == null && !spawned.IsDestroyed)
                {
                    Logging.Logger.Error($"Inventory: failed to load '{name}'.");
                    spawned.Destroy();
                }
                whenDone(loaded);
            });
            return slot;
        }
        catch (Exception ex)
        {
            Logging.Logger.Error($"Inventory: failed to load '{name}': {ex.Message}");
            if (slot != null && !slot.IsDestroyed)
                slot.Destroy();
            return null;
        }
    }

    // HELPERS

    internal static string ExtensionFor(string kind) => string.Equals(kind, WorldKind, StringComparison.OrdinalIgnoreCase) ? WorldExtension : ItemExtension;

    // What just went up is what a spawn of it would fetch, so it is kept on hand.
    internal static void Keep(string hash, byte[] packed, bool world)
    {
        try
        {
            var db = Engine.Current?.LocalDB;
            if (db == null)
                return;
            var path = db.GetTempFilePath();
            File.WriteAllBytes(path, SealedCopy(packed));
            Cached[hash] = path;
        }
        catch { }
    }

    // The refusal for a save whose files could not all go up, naming the first few of them.
    public static string NotUploaded(string name, List<string> problems)
    {
        const int Named = 4;
        var named = string.Join("; ", problems.GetRange(0, System.Math.Min(Named, problems.Count)));
        var more = problems.Count > Named ? $"; and {problems.Count - Named} more" : string.Empty;
        return problems.Count == 1
            ? $"Couldn't save \"{name}\": {named}."
            : $"Couldn't save \"{name}\", {problems.Count} files it uses did not go up: {named}{more}.";
    }

    // What a save that went up without the files this machine no longer has says about them.
    public static string LeftOut(string name, List<string> dropped)
    {
        const int Named = 4;
        var named = string.Join("; ", dropped.GetRange(0, System.Math.Min(Named, dropped.Count)));
        var more = dropped.Count > Named ? $"; and {dropped.Count - Named} more" : string.Empty;
        return dropped.Count == 1
            ? $"Saved \"{name}\" without {named}, which is missing on this machine."
            : $"Saved \"{name}\" without {dropped.Count} files missing on this machine: {named}{more}.";
    }

    public static string ItemKindOf(Slot slot)
        => slot.GetComponentInChildren<global::Lumora.Core.Components.Avatar.AvatarForm>() != null ? AvatarKind
            : PictureOf(slot) != null ? ImageKind
            : ObjectKind;

    // A picture: one image on a flat quad on the slot itself, the way an imported or pasted image stands.
    // Null for anything else.
    public static Assets.ImageProvider? PictureOf(Slot slot)
    {
        var image = slot.GetComponent<Assets.ImageProvider>();
        if (image == null)
            return null;
        foreach (var renderer in slot.GetComponents<Components.MeshRenderer>())
        {
            if (renderer.Mesh.Target is Components.Meshes.QuadMesh)
                return image;
        }
        return null;
    }

    // NAMES
    //
    // A slot or a world may carry markup in its name for whoever draws it, but the inventory shows names as
    // plain text, and the service files folders by path and refuses a slash in any name. So the name kept is
    // the text the markup shows (a closing tag left with nothing to close goes too), line breaks and other
    // control characters as spaces, a slash or backslash as a dash, cut to what the service takes. A save
    // always goes up under some name: <color=red>Box</color> goes up as Box, a/b as a-b. -xlinka

    public const int NameLimit = 64;

    private static readonly System.Text.RegularExpressions.Regex StrayCloseTag = new(@"</\s*[A-Za-z][A-Za-z-]*\s*>");

    public static string CleanName(string? name, string fallback = "Item")
    {
        string plain = PlainName(name);
        if (plain.Length > NameLimit)
            plain = plain[..global::Lumora.Core.Input.TextElements.Floor(plain, NameLimit)].TrimEnd();
        return plain.Length > 0 ? plain : fallback;
    }

    private static string PlainName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;
        string shown = StrayCloseTag.Replace(Helio.UI.RichTextParser.Plain(name), string.Empty);
        var kept = new System.Text.StringBuilder(shown.Length);
        foreach (char c in shown)
            kept.Append(c is '/' or '\\' ? '-' : char.IsControl(c) ? ' ' : c);
        return kept.ToString().Trim();
    }

    // A typed name is refused only when nothing is left of it once cleaned, or it is longer than the service
    // keeps; markup and slashes are taken out, not refused.
    public static bool ValidName(string? name, out string? error)
    {
        error = null;
        string plain = PlainName(name);
        if (plain.Length == 0)
        {
            error = "Give it a name.";
            return false;
        }
        if (plain.Length > NameLimit)
        {
            error = "That name is too long.";
            return false;
        }
        return true;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.0} GB";
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0):0.0} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:0} KB";
        return $"{bytes} B";
    }
}
