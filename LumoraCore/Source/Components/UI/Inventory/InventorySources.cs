// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// Where the Inventory screen gets its rows and sends its changes. The cloud is the only real source;
// the demo one exists so a screen capture can draw the screen on a machine with no account. -xlinka
internal interface IInventorySource
{
    bool SignedIn { get; }
    Task<InventoryListing> List(string folderId);
    // A folder of the account's own inventory, a group's, or one shared with it.
    Task<InventoryListing> List(InventoryPlace place);
    Task<InventoryListing> Search(string query, string? ownerId = null);
    Task<InventoryResult> CreateFolder(InventoryPlace parent, string name);
    Task<InventoryResult> Rename(InventoryEntry entry, string name);
    Task<InventoryResult> Move(InventoryEntry entry, InventoryPlace target);
    // A folder moved into the folder of its name in the target, the two made one; what a move refused with
    // Inventory.NameTakenCode offers.
    Task<InventoryResult> Merge(InventoryEntry folder, InventoryPlace target);
    Task<InventoryResult> Delete(InventoryEntry entry);
    Task<InventoryResult> SaveItem(Slot slot, string name, InventoryPlace place);
    Task<(string? path, string? problem)> FetchToCache(InventoryEntry entry);
    Task<byte[]?> FetchThumbnail(InventoryEntry entry);
    // A save someone sent, made one of yours, in the folder the inventory has open
    Task<InventoryResult> KeepSentHere(InventoryEntry sent);
    // How far the saves made here have got going up; null where nothing syncs.
    InventorySyncStatus? SyncStatus { get; }

    // The groups the account is in, each a root of its own; null when they could not be read.
    Task<List<GroupInfo>?> Groups();
    // The account's friends, the people a share can name.
    IReadOnlyList<FriendInfo> Friends { get; }
    Task<ApiResponse<InventoryShareListing>> Shares(InventoryEntry entry);
    Task<ApiResponse<InventoryShareListing>> Share(InventoryEntry entry, string kind, string? granteeId, string access);
    Task<ApiResponse<InventoryShareListing>> Unshare(InventoryEntry entry, string? kind, string? granteeId);
    Task<InventoryResult> AddLink(string text, string folderId);
}

internal sealed class CloudInventorySource : IInventorySource
{
    public bool SignedIn => Inventory.IsSignedIn;
    public InventorySyncStatus? SyncStatus => InventorySync.SignedIn ? InventorySync.Status : null;
    public Task<InventoryListing> List(string folderId) => Inventory.ListAsync(folderId);
    public Task<InventoryListing> List(InventoryPlace place) => Inventory.ListAsync(place.FolderId, place.OwnerId);
    public Task<InventoryListing> Search(string query, string? ownerId = null) => Inventory.SearchAsync(query, ownerId);
    public Task<InventoryResult> CreateFolder(InventoryPlace parent, string name) => Inventory.CreateFolderAsync(parent.FolderId, name, parent.OwnerId);
    public Task<InventoryResult> Rename(InventoryEntry entry, string name) => Inventory.RenameAsync(entry, name);
    public Task<InventoryResult> Move(InventoryEntry entry, InventoryPlace target) => Inventory.MoveAsync(entry, target.FolderId, target.Name, target.OwnerId);
    public Task<InventoryResult> Merge(InventoryEntry folder, InventoryPlace target) => Inventory.MergeAsync(folder, target.FolderId, target.Name);
    public Task<InventoryResult> Delete(InventoryEntry entry) => Inventory.DeleteAsync(entry);
    public Task<InventoryResult> SaveItem(Slot slot, string name, InventoryPlace place) => Inventory.SaveItemAsync(slot, name, place);
    public Task<(string? path, string? problem)> FetchToCache(InventoryEntry entry) => Inventory.FetchToCacheAsync(entry);
    public Task<byte[]?> FetchThumbnail(InventoryEntry entry) => Inventory.FetchThumbnailAsync(entry);
    public Task<InventoryResult> KeepSentHere(InventoryEntry sent) => Inventory.KeepSentHereAsync(sent);

    public async Task<List<GroupInfo>?> Groups()
    {
        var client = Inventory.Client;
        if (client == null || !client.IsAuthenticated)
            return null;
        var mine = await client.GetMyGroups().ConfigureAwait(false);
        return mine.Success ? mine.Data ?? new List<GroupInfo>() : null;
    }

    public IReadOnlyList<FriendInfo> Friends => Engine.Current?.Social?.Friends ?? Array.Empty<FriendInfo>();
    public Task<ApiResponse<InventoryShareListing>> Shares(InventoryEntry entry) => Inventory.SharesAsync(entry);
    public Task<ApiResponse<InventoryShareListing>> Share(InventoryEntry entry, string kind, string? granteeId, string access)
        => Inventory.ShareAsync(entry, kind, granteeId, access);
    public Task<ApiResponse<InventoryShareListing>> Unshare(InventoryEntry entry, string? kind, string? granteeId)
        => Inventory.UnshareAsync(entry, kind, granteeId);
    public Task<InventoryResult> AddLink(string text, string folderId) => Inventory.AddLinkAsync(text, folderId);
}

// An in-memory tree with a few folders and saves in it, a group the account builds in, and a folder a friend
// shared with it. Only a screen capture switches to it, so the screen can be looked at without a signed-in
// account and without a service to talk to. Nothing it holds can be spawned or opened. -xlinka
internal sealed class DemoInventorySource : IInventorySource
{
    private const string Me = "U-demo-me";
    public const string GroupId = "G-demo-lantern";
    private const string FriendId = "U-demo-bo";

    private sealed class Node
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Name = "";
        public InventoryEntryKind Kind;
        public string ItemKind = "";
        public string Owner = Me;
        public string Parent = Inventory.Root;
        public DateTime Modified = DateTime.UtcNow;
        public long Size;
        public string? Picture;
        public string? SavedBy;
        public List<string>? Missing;
    }

    private sealed record Grant(string TargetId, string Kind, string Id, string Name, string Access);

    private readonly List<Node> _nodes = new();
    private readonly List<Grant> _grants = new();
    private readonly List<GroupInfo> _groups = new()
    {
        new GroupInfo { Id = GroupId, Name = "Lantern Village", Tag = "LV", Color = "#FF8833", MyRole = "Builder", MyAllocatedBytes = 256L << 20 },
        new GroupInfo { Id = "G-demo-djs", Name = "Rooftop DJs", Tag = "DJ", Color = "#5CBFF2", MyRole = "Member" },
    };
    private readonly List<FriendInfo> _friends = new()
    {
        new FriendInfo { UserId = FriendId, Username = "bo" },
        new FriendInfo { UserId = "U-demo-kes", Username = "kestrel" },
        new FriendInfo { UserId = "U-demo-lumi", Username = "lumi" },
    };
    private Node _sharedIn = null!;

    public DemoInventorySource()
    {
        var props = Add("Props", InventoryEntryKind.Folder, "", Inventory.Root);
        var avatars = Add("Avatars", InventoryEntryKind.Folder, "", Inventory.Root);
        Add("Lamps", InventoryEntryKind.Folder, "", props.Id);
        Add("Events", InventoryEntryKind.Folder, "", Inventory.Root);
        // A save the server lost three parts of, under made-up hashes, so the broken mark can be looked at.
        var chair = Add("Chair", InventoryEntryKind.Item, Inventory.ObjectKind, Inventory.Root, 48_120);
        chair.Missing = new List<string>();
        for (int i = 0; i < 3; i++)
            chair.Missing.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"demo-missing:{i}"))).ToLowerInvariant());
        Add("Lamp", InventoryEntryKind.Item, Inventory.ObjectKind, Inventory.Root, 22_400);
        Add("Fox", InventoryEntryKind.Item, Inventory.AvatarKind, avatars.Id, 3_140_000);
        Add("Rooftop", InventoryEntryKind.World, "Social", Inventory.Root, 12_600_000);
        Add("Workshop", InventoryEntryKind.World, "Builder", Inventory.Root, 8_200_000);
        Add("Desk lamp", InventoryEntryKind.Item, Inventory.ObjectKind, props.Id, 18_000);
        for (int i = 1; i <= 30; i++)
            Add($"Prop {i:00}", InventoryEntryKind.Item, Inventory.ObjectKind, props.Id, 10_000 + i * 700);
        // Photos carry a picture the way a real one does, under a made-up hash: a picker can choose them, and
        // nothing can fetch them.
        var photos = Add(Photos.PhotoPrint.PhotosFolder, InventoryEntryKind.Folder, "", Inventory.Root);
        foreach (var place in new[] { "Rooftop at dusk", "Lantern market", "Workshop bench", "Fog test", "Group shot" })
        {
            var photo = Add("Photo in " + place, InventoryEntryKind.Item, Inventory.ObjectKind, photos.Id, 420_000);
            photo.Picture = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("demo-photo:" + place))).ToLowerInvariant();
        }
        _grants.Add(new Grant(props.Id, Inventory.ShareUser, FriendId, "bo", Inventory.AccessRead));

        var stalls = Add("Market stalls", InventoryEntryKind.Folder, "", Inventory.Root, owner: GroupId);
        Add("Lantern", InventoryEntryKind.Item, Inventory.ObjectKind, Inventory.Root, 64_000, GroupId, "bo");
        Add("Square", InventoryEntryKind.World, "Event", Inventory.Root, 22_000_000, GroupId, "kestrel");
        Add("Awning", InventoryEntryKind.Item, Inventory.ObjectKind, stalls.Id, 31_000, GroupId, "bo");

        _sharedIn = Add("Bo's kitbash", InventoryEntryKind.Folder, "", Inventory.Root, owner: FriendId);
        Add("Crate", InventoryEntryKind.Item, Inventory.ObjectKind, _sharedIn.Id, 12_000, FriendId);
        Add("Barrel", InventoryEntryKind.Item, Inventory.ObjectKind, _sharedIn.Id, 15_500, FriendId);
    }

    private Node Add(string name, InventoryEntryKind kind, string itemKind, string parent, long size = 0, string owner = Me, string? savedBy = null)
    {
        var node = new Node { Name = name, Kind = kind, ItemKind = itemKind, Parent = parent, Size = size, Owner = owner, SavedBy = savedBy };
        _nodes.Add(node);
        return node;
    }

    public bool SignedIn => true;
    public InventorySyncStatus? SyncStatus => null;

    private static string OwnerOf(string? ownerId) => string.IsNullOrEmpty(ownerId) ? Me : ownerId!;

    private InventoryEntry ToEntry(Node n, InventoryLinkInfo? link = null)
    {
        int children = 0;
        if (n.Kind == InventoryEntryKind.Folder)
        {
            foreach (var m in _nodes)
                if (m.Parent == n.Id) children++;
        }
        int grants = _grants.FindAll(g => g.TargetId == n.Id).Count;
        return new InventoryEntry(link?.Id ?? n.Id, n.Name, n.Kind, n.Modified, n.Size, children, n.ItemKind, "demo-" + n.Id, n.Picture,
            link != null ? Inventory.Root : n.Parent, null)
        {
            OwnerId = link != null || n.Owner == Me ? null : n.Owner,
            Link = link,
            ShareCount = link == null ? grants : 0,
            IsPublic = link == null && _grants.Exists(g => g.TargetId == n.Id && g.Kind == Inventory.SharePublic),
            SavedByName = n.SavedBy,
            MissingParts = Inventory.MissingOf(n.Missing),
        };
    }

    public Task<InventoryListing> List(string folderId) => List(new InventoryPlace(null, folderId, string.Empty));

    public Task<InventoryListing> List(InventoryPlace place)
    {
        string owner = OwnerOf(place.OwnerId);
        var listing = new InventoryListing();
        foreach (var n in _nodes)
            if (n.Owner == owner && n.Parent == place.FolderId) listing.Entries.Add(ToEntry(n));
        if (owner == Me && place.IsRoot)
        {
            listing.Entries.Add(ToEntry(_sharedIn, new InventoryLinkInfo
            {
                Via = Inventory.ShareUser, Id = "S-demo-kitbash", OwnerId = FriendId, OwnerName = "bo",
                TargetKind = "folder", TargetId = _sharedIn.Id, Access = Inventory.AccessRead,
            }));
        }
        listing.Access = owner == Me ? InventoryAccess.Owner : owner == GroupId ? InventoryAccess.Write : InventoryAccess.Read;
        listing.CanShare = owner == Me;
        return Task.FromResult(listing);
    }

    public Task<InventoryListing> Search(string query, string? ownerId = null)
    {
        string owner = OwnerOf(ownerId);
        var listing = new InventoryListing();
        foreach (var n in _nodes)
            if (n.Owner == owner && n.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) listing.Entries.Add(ToEntry(n));
        return Task.FromResult(listing);
    }

    public Task<InventoryResult> CreateFolder(InventoryPlace parent, string name)
    {
        if (FolderNamed(OwnerOf(parent.OwnerId), parent.FolderId, name.Trim(), null) is { } taken)
            return Task.FromResult(new InventoryResult(false, InventoryFolderLocale.NameTakenHere(taken.Name).Resolve()) { Code = Inventory.NameTakenCode });
        Add(name.Trim(), InventoryEntryKind.Folder, "", parent.FolderId, owner: OwnerOf(parent.OwnerId));
        return Task.FromResult(InventoryResult.Success($"Folder \"{name.Trim()}\" is here."));
    }

    // A folder of that name in the parent, whatever its case, other than the one named.
    private Node? FolderNamed(string owner, string parent, string name, string? besides)
        => _nodes.Find(n => n.Kind == InventoryEntryKind.Folder && n.Owner == owner && n.Parent == parent && n.Id != besides
            && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));

    public Task<InventoryResult> Rename(InventoryEntry entry, string name)
    {
        var n = _nodes.Find(x => x.Id == entry.Id);
        if (n != null) n.Name = name.Trim();
        return Task.FromResult(InventoryResult.Success($"Renamed to \"{name.Trim()}\"."));
    }

    public Task<InventoryResult> Move(InventoryEntry entry, InventoryPlace target)
    {
        var n = _nodes.Find(x => x.Id == entry.Id);
        if (n != null && n.Kind == InventoryEntryKind.Folder && FolderNamed(n.Owner, target.FolderId, n.Name, n.Id) is { } taken)
            return Task.FromResult(new InventoryResult(false, InventoryFolderLocale.NameTakenThere(taken.Name, target.Name).Resolve()) { Code = Inventory.NameTakenCode });
        if (n != null) n.Parent = target.FolderId;
        return Task.FromResult(InventoryResult.Success($"Moved \"{entry.Name}\" to {target.Name}."));
    }

    // Children of a folder of one name merge in turn, the way the service does it.
    public Task<InventoryResult> Merge(InventoryEntry folder, InventoryPlace target)
    {
        var n = _nodes.Find(x => x.Id == folder.Id);
        if (n == null)
            return Task.FromResult(InventoryResult.Failure(InventoryFolderLocale.MergeFailed.Resolve()));
        var into = FolderNamed(n.Owner, target.FolderId, n.Name, n.Id);
        if (into == null)
            return Move(folder, target);
        Fold(n, into);
        return Task.FromResult(InventoryResult.Success(InventoryFolderLocale.Merged(folder.Name, target.Name).Resolve()));
    }

    private void Fold(Node from, Node into)
    {
        foreach (var child in _nodes.FindAll(x => x.Parent == from.Id))
        {
            var same = child.Kind != InventoryEntryKind.Folder ? null : _nodes.Find(x => x.Kind == InventoryEntryKind.Folder && x.Parent == into.Id
                && x != child && x != from && string.Equals(x.Name, child.Name, StringComparison.OrdinalIgnoreCase));
            if (same != null)
                Fold(child, same);
            else
                child.Parent = into.Id;
        }
        _nodes.Remove(from);
    }

    public Task<InventoryResult> Delete(InventoryEntry entry)
    {
        _nodes.RemoveAll(x => x.Id == entry.Id || x.Parent == entry.Id);
        return Task.FromResult(InventoryResult.Success($"Deleted \"{entry.Name}\"."));
    }

    public Task<InventoryResult> SaveItem(Slot slot, string name, InventoryPlace place)
        => Task.FromResult(InventoryResult.Failure("The demo inventory cannot save."));

    public Task<(string? path, string? problem)> FetchToCache(InventoryEntry entry)
        => Task.FromResult<(string?, string?)>((null, "The demo inventory has no content."));

    public Task<byte[]?> FetchThumbnail(InventoryEntry entry) => Task.FromResult<byte[]?>(null);

    public Task<InventoryResult> KeepSentHere(InventoryEntry sent)
    {
        _nodes.Add(new Node { Name = sent.Name, Kind = sent.Kind, ItemKind = sent.ItemKind, Size = sent.SizeBytes, Picture = sent.ThumbnailHash });
        return Task.FromResult(InventoryResult.Success($"Kept \"{sent.Name}\"."));
    }

    public Task<List<GroupInfo>?> Groups() => Task.FromResult<List<GroupInfo>?>(new List<GroupInfo>(_groups));

    public IReadOnlyList<FriendInfo> Friends => _friends;

    public Task<ApiResponse<InventoryShareListing>> Shares(InventoryEntry entry) => Task.FromResult(Listing(entry));

    public Task<ApiResponse<InventoryShareListing>> Share(InventoryEntry entry, string kind, string? granteeId, string access)
    {
        string id = kind == Inventory.SharePublic ? string.Empty : granteeId ?? string.Empty;
        _grants.RemoveAll(g => g.TargetId == entry.Id && g.Kind == kind && g.Id == id);
        string name = kind == Inventory.ShareGroup ? _groups.Find(g => g.Id == id)?.Name ?? id
            : kind == Inventory.ShareUser ? _friends.Find(f => f.UserId == id)?.Username ?? id : string.Empty;
        _grants.Add(new Grant(entry.Id, kind, id, name, kind == Inventory.SharePublic ? Inventory.AccessRead : access));
        return Task.FromResult(Listing(entry));
    }

    public Task<ApiResponse<InventoryShareListing>> Unshare(InventoryEntry entry, string? kind, string? granteeId)
    {
        _grants.RemoveAll(g => g.TargetId == entry.Id && (kind == null || (g.Kind == kind && g.Id == (granteeId ?? string.Empty))));
        return Task.FromResult(Listing(entry));
    }

    public Task<InventoryResult> AddLink(string text, string folderId)
        => Task.FromResult(InventoryResult.Failure("The demo inventory keeps no links."));

    private ApiResponse<InventoryShareListing> Listing(InventoryEntry entry)
    {
        var grants = new List<InventoryShareGrant>();
        foreach (var g in _grants)
        {
            if (g.TargetId == entry.Id)
                grants.Add(new InventoryShareGrant { Kind = g.Kind, Id = g.Id, Name = g.Name, Access = g.Access, CreatedAt = DateTime.UtcNow });
        }
        if (entry.OwnerId != null && entry.OwnerId != GroupId)
            return ApiResponse<InventoryShareListing>.Fail(HttpStatusCode.Forbidden, "Only its owner can share this.");
        return ApiResponse<InventoryShareListing>.Ok(new InventoryShareListing
        {
            TargetId = entry.Id,
            TargetKind = entry.IsFolder ? "folder" : "item",
            Name = entry.Name,
            CanShare = true,
            Grants = grants,
        });
    }
}
