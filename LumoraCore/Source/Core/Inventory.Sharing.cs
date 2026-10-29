// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core;

// Sharing a folder or an item: with friends by name, with a group, or with anyone who has its link. A share
// covers everything inside a folder, and someone it reaches finds it at the top of their own inventory, to look
// at and take copies of, or to save into as well when it lets them. A link is plain text, so it can go anywhere
// text goes; pasted into an inventory it keeps the folder or item there for as long as it stays shared. -xlinka
public static partial class Inventory
{
    public const string LinkPrefix = "lumora-inventory:";
    public const string ShareUser = "user";
    public const string ShareGroup = "group";
    public const string SharePublic = "public";
    // The row at the top of every inventory that leads into the game's own library.
    public const string ShareLibrary = "library";
    public const string LibraryId = LumoraClient.InventoryLibrary;
    public const string AccessRead = "read";
    public const string AccessWrite = "write";

    private static readonly Regex LinkPattern = new(@"^lumora-inventory:([A-Za-z0-9-]{3,80})/(folder|item)/([A-Za-z0-9-]{8,64})$", RegexOptions.Compiled);

    // The text someone pastes to reach this folder or item; for a link row, what that link reaches.
    public static string? LinkText(in InventoryEntry entry)
    {
        string? owner = entry.TargetOwnerId ?? InventorySync.Account ?? Client?.AccountUserId;
        if (string.IsNullOrEmpty(owner))
            return null;
        bool folder = entry.Link != null ? entry.Link.TargetKind == "folder" : entry.IsFolder;
        return $"{LinkPrefix}{owner}/{(folder ? "folder" : "item")}/{entry.TargetId}";
    }

    public static bool TryReadLink(string? text, out string ownerId, out string kind, out string id)
    {
        ownerId = kind = id = string.Empty;
        var match = LinkPattern.Match(text?.Trim() ?? string.Empty);
        if (!match.Success)
            return false;
        ownerId = match.Groups[1].Value;
        kind = match.Groups[2].Value;
        id = match.Groups[3].Value;
        return true;
    }

    // A pasted link kept in one of this account's own folders.
    public static async Task<InventoryResult> AddLinkAsync(string text, string folderId)
    {
        if (!TryReadLink(text, out var owner, out var kind, out var id))
            return InventoryResult.Failure("That is not an inventory link. Links start with " + LinkPrefix);
        var client = Client;
        if (client == null || !client.IsAuthenticated)
            return NotSignedIn();
        if (owner == InventorySync.Account)
            return InventoryResult.Failure("That link is to something of yours already.");
        var result = await client.AddInventoryLink(owner, kind, id, folderId).ConfigureAwait(false);
        if (result.Success)
        {
            InventorySync.Touch();
            return InventoryResult.Success(kind == "folder" ? "The shared folder is in your inventory now." : "The shared item is in your inventory now.");
        }
        if (result.Error == AccountEmail.NotVerifiedCode)
        {
            AccountEmail.Refused(saves: true, joins: false);
            return new InventoryResult(false, AccountEmailLocale.SaveRefused.Resolve()) { Code = AccountEmail.NotVerifiedCode };
        }
        return InventoryResult.Failure(result.Status switch
        {
            HttpStatusCode.Forbidden => "That isn't shared with you, or it isn't shared any more.",
            HttpStatusCode.NotFound => result.Error == "folder" ? "The folder you are in is gone." : "Nothing is at that link any more.",
            HttpStatusCode.Conflict => "That link is in your inventory already.",
            _ => result.Message ?? "Couldn't add that link.",
        });
    }

    // A link card asking the inventory window to show what it leads to. The window listens, the way the People
    // window listens for a profile, so nothing in a world needs to know where the window lives.
    public static event Action<InventoryOpenRequest>? OpenRequested;

    public static void RequestOpen(InventoryOpenRequest request) => OpenRequested?.Invoke(request);

    // The item a link leads to, when this account reaches it now.
    public static async Task<(InventoryEntry? entry, string? problem)> SharedItemAsync(string ownerId, string itemId)
    {
        var client = Client;
        if (client == null || !client.IsAuthenticated)
            return (null, "Sign in to open shared things.");
        var read = await client.GetInventoryItem(ownerId, itemId).ConfigureAwait(false);
        if (read.Success && read.Data != null)
            return (EntryOf(read.Data, ownerId == InventorySync.Account ? null : ownerId), null);
        return (null, read.Status == HttpStatusCode.Forbidden || read.Status == HttpStatusCode.NotFound
            ? "That isn't shared with you, or it isn't shared any more."
            : read.Message ?? "Couldn't reach it.");
    }

    // Whether this account may open a folder of ownerId's now.
    public static async Task<string?> SharedFolderProblemAsync(string ownerId, string folderId)
    {
        var client = Client;
        if (client == null || !client.IsAuthenticated)
            return "Sign in to open shared things.";
        var listed = await client.GetFolderContents(folderId, ownerId == InventorySync.Account ? null : ownerId).ConfigureAwait(false);
        if (listed.Success)
            return null;
        return listed.Status == HttpStatusCode.Forbidden || listed.Status == HttpStatusCode.NotFound
            ? "That isn't shared with you, or it isn't shared any more."
            : listed.Message ?? "Couldn't reach it.";
    }

    private static string? ShareOwner(in InventoryEntry entry) => entry.OwnerId ?? InventorySync.Account ?? Client?.AccountUserId;

    // Who can reach this folder or item, for an account allowed to share it.
    public static async Task<ApiResponse<InventoryShareListing>> SharesAsync(InventoryEntry entry)
    {
        var client = Client;
        var owner = ShareOwner(entry);
        if (client == null || !client.IsAuthenticated || owner == null)
            return ApiResponse<InventoryShareListing>.Fail(HttpStatusCode.Unauthorized, NotSignedIn().Message);
        return await client.GetInventoryShares(owner, entry.Id).ConfigureAwait(false);
    }

    public static async Task<ApiResponse<InventoryShareListing>> ShareAsync(InventoryEntry entry, string kind, string? granteeId, string access)
    {
        var client = Client;
        var owner = ShareOwner(entry);
        if (client == null || !client.IsAuthenticated || owner == null)
            return ApiResponse<InventoryShareListing>.Fail(HttpStatusCode.Unauthorized, NotSignedIn().Message);
        var answer = await client.SetInventoryShare(owner, entry.Id, kind, granteeId, access).ConfigureAwait(false);
        InventorySync.Touch();
        return answer;
    }

    // One grant taken back, or every grant when kind is null.
    public static async Task<ApiResponse<InventoryShareListing>> UnshareAsync(InventoryEntry entry, string? kind, string? granteeId)
    {
        var client = Client;
        var owner = ShareOwner(entry);
        if (client == null || !client.IsAuthenticated || owner == null)
            return ApiResponse<InventoryShareListing>.Fail(HttpStatusCode.Unauthorized, NotSignedIn().Message);
        var answer = await client.RemoveInventoryShare(owner, entry.Id, kind, granteeId).ConfigureAwait(false);
        InventorySync.Touch();
        return answer;
    }

    // A refusal the share page can show as it is.
    public static string ShareProblem(ApiResponse answer) => answer.Error switch
    {
        "not-friend" => "You can only share with your friends.",
        "not-member" => "You can only share with groups you are in.",
        "forbidden" or "read-only" => "Only its owner, or a group's admins, can share this.",
        _ => string.IsNullOrWhiteSpace(answer.Message) ? "That didn't go through." : answer.Message!,
    };
}
