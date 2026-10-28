// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Lumora.Core.Components.UI.Worlds;
using Lumora.Core.Math;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// ROOTS: your own inventory and one per group you are in, the way down from the open root to the folder
// being looked at, and what this account may do in that folder. Whatever folder is open here is where a
// save lands, so every move tells the inventory where that is and whether a save may land there. The game's
// library is a group to the services but no root here: it is the gold folder at the top of your own, which
// staff save into and everyone else opens to look, with its role carried on that folder's row. -xlinka
public sealed partial class InventoryScreen
{
    private static readonly color ShareFill = InventoryCard.ShareInk.WithAlpha(0.32f);

    // The way down from the open root, the root first; the last one is the folder being looked at. A step
    // into something shared in carries its owner, so the chain can cross from your inventory into theirs.
    private readonly List<InventoryPlace> _chain = new() { InventoryPlace.OwnRoot };
    private readonly List<InventoryRoot> _roots = new();
    private readonly List<GroupInfo> _groups = new();
    private int _rootIndex;
    private int _groupsSerial;
    private string? _rootsAccount;
    private InventoryAccess _access = InventoryAccess.Owner;
    private bool _canShare = true;
    private GroupInventoryQuota? _groupQuota;
    // Per group: its storage as this account sees it (status, room), and how many delete requests wait for it.
    private readonly Dictionary<string, GroupInventoryQuota> _quotas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _requestCounts = new(StringComparer.Ordinal);
    private int _statesSerial;
    // This account's name, to tell its own delete requests from others'.
    private string? _myName;
    // This account's role in the library when it is on staff, and the library's name, from the row at the top.
    private string? _libraryRole;
    private string? _libraryName;

    private string LibraryName => _libraryName ?? (Here.IsLibrary ? Here.OwnerName : string.Empty);

    private InventoryPlace Here => _chain[_chain.Count - 1];
    private string FolderName => Here.Name;
    private InventoryRoot ActiveRoot => _rootIndex < _roots.Count ? _roots[_rootIndex] : OwnRoot();

    // Links pasted in always land in your own inventory: here when here is yours, else at your top.
    private InventoryPlace LinkHome => Here.IsOwn ? Here : InventoryPlace.OwnRoot;

    private static InventoryRoot OwnRoot()
        => new(InventoryPlace.OwnRoot, string.Empty, null, DashTheme.Accent, DashTheme.Text, string.Empty, false, true, null);

    private static int RoleRank(string? role) => role switch
    {
        "Owner" => 0,
        "Admin" => 1,
        "Moderator" => 2,
        "Builder" => 3,
        "Member" => 4,
        _ => 5,
    };

    // A group's builders and up save into it out of the slice its owner set them; its owner and admins with none
    // draw from the group's main pool. Anyone else with no slice has no storage there: they browse and spawn, and
    // change nothing. A group whose owner stopped paying is locked: what is there stays, and nothing new goes in. -xlinka
    private InventoryRoot GroupRoot(GroupInfo group)
    {
        int rank = RoleRank(group.MyRole);
        bool builds = rank <= 3;
        _quotas.TryGetValue(group.Id, out var quota);
        _requestCounts.TryGetValue(group.Id, out int requests);
        bool locked = quota is { IsLocked: true };
        bool grace = quota is { InGrace: true };
        bool storage = HasStorage(group);
        bool room = quota == null || quota.HasRoom;
        string? refusal = !builds ? $"{group.Name} is view only for you."
            : locked ? $"{group.Name}'s storage is locked: its owner's support has lapsed."
            : !storage ? $"No storage set for you in {group.Name}."
            : !room ? (quota!.FromPool ? $"{group.Name}'s shared storage is full." : $"Your share of {group.Name} is full.")
            : null;
        string note = locked ? "locked" : !builds ? "view only" : !storage ? "no storage" : !room ? "full" : grace ? "grace"
            : (group.MyRole ?? "member").ToLowerInvariant();
        return new InventoryRoot(new InventoryPlace(group.Id, Inventory.Root, group.Name, group.Name), group.Tag, group.IconHash,
            GroupsFormat.Tint(group.Color), GroupsFormat.Ink(group.Color), note, true, builds && !locked && storage && room, refusal,
            rank <= 2 ? requests : 0, locked || grace || (builds && !storage));
    }

    // Storage of this account's in the group: a slice its owner set, or the main pool for its owner and admins. The
    // service's quota has the last word; before it answers, the slice the group list carries does.
    private bool HasStorage(GroupInfo group)
    {
        if (RoleRank(group.MyRole) <= 1)
            return true;
        return _quotas.TryGetValue(group.Id, out var quota) ? !quota.NoStorage : group.MyAllocatedBytes > 0;
    }

    // Each group's storage and, for its moderators, its waiting delete requests: what the roots say and decide.
    private void RefreshGroupStates()
    {
        int serial = ++_statesSerial;
        if (_source is not CloudInventorySource || Inventory.Client is not { IsAuthenticated: true } client)
            return;
        var groups = new List<GroupInfo>(_groups);
        string? libraryRole = _libraryRole;
        Await(System.Threading.Tasks.Task.Run(async () =>
        {
            if (_myName == null && await client.GetCurrentUser().ConfigureAwait(false) is { Success: true, Data: { } me })
                _myName = me.Username;
            var quotas = new Dictionary<string, GroupInventoryQuota>(StringComparer.Ordinal);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var group in groups)
            {
                var quota = await client.GetGroupInventoryQuota(group.Id).ConfigureAwait(false);
                if (quota.Success && quota.Data != null)
                    quotas[group.Id] = quota.Data;
                if (RoleRank(group.MyRole) <= 2)
                {
                    var waiting = await client.GetInventoryDeleteRequests(group.Id).ConfigureAwait(false);
                    if (waiting.Success && waiting.Data != null)
                        counts[group.Id] = waiting.Data.Count;
                }
            }
            if (RoleRank(libraryRole) <= 2)
            {
                var waiting = await client.GetInventoryDeleteRequests(Inventory.LibraryId).ConfigureAwait(false);
                if (waiting.Success && waiting.Data != null)
                    counts[Inventory.LibraryId] = waiting.Data.Count;
            }
            return (quotas, counts);
        }), states =>
        {
            if (serial != _statesSerial)
                return;
            _quotas.Clear();
            foreach (var pair in states.quotas)
                _quotas[pair.Key] = pair.Value;
            _requestCounts.Clear();
            foreach (var pair in states.counts)
                _requestCounts[pair.Key] = pair.Value;
            if (Here.IsGroup && _quotas.TryGetValue(Here.OwnerId!, out var here))
                _groupQuota = here;
            RebuildRoots();
            RebuildStatusRow();
        });
    }

    // The open group's storage is locked: nothing new goes in, though what is there can still be taken out.
    private bool LockedHere => Here.IsGroup && _quotas.TryGetValue(Here.OwnerId!, out var quota) && quota.IsLocked;

    // The open group gives this account no storage: a builder or moderator with no slice set.
    private bool NoStorageHere => Here.IsGroup && GroupOf(Here.OwnerId) is { } group && RoleRank(group.MyRole) <= 3 && !HasStorage(group);

    // Saving, renaming, moving and new folders: what this account may change here, a locked group or no storage
    // there taking it away.
    private bool CanEditHere => _access.CanChange() && !LockedHere && !NoStorageHere;

    // Why a rename, move or new folder cannot happen here, for the status line.
    private string EditRefusal(string what) => LockedHere ? $"{ActiveRoot.Place.Name}'s storage is locked, so {what}."
        : NoStorageHere ? $"No storage set for you in {ActiveRoot.Place.Name}, so {what}. Its owner can give you a share."
        : $"\"{FolderName}\" is view only for you, so {what}.";

    private int RankIn(string? groupId) => RoleRank(groupId == Inventory.LibraryId ? _libraryRole : GroupOf(groupId)?.MyRole);

    private void RefreshGroups()
    {
        int serial = ++_groupsSerial;
        if (!_source.SignedIn)
        {
            _groups.Clear();
            RebuildRoots();
            return;
        }
        Await(_source.Groups(), groups =>
        {
            if (serial != _groupsSerial || groups == null)
                return;
            _groups.Clear();
            _groups.AddRange(groups);
            _groups.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            RebuildRoots();
            RefreshGroupStates();
        });
    }

    // The roots again, keeping the open one open; a group you left closes back to your own.
    private void RebuildRoots()
    {
        string? open = _rootIndex < _roots.Count ? _roots[_rootIndex].Place.OwnerId : null;
        _roots.Clear();
        _roots.Add(OwnRoot());
        foreach (var group in _groups)
            _roots.Add(GroupRoot(group));
        int found = 0;
        for (int i = 1; i < _roots.Count; i++)
        {
            if (_roots[i].Place.OwnerId == open)
                found = i;
        }
        if (open != null && found == 0)
        {
            NavigateToRoot();
            return;
        }
        _rootIndex = found;
        if (_chain.Count > 0 && _chain[0].OwnerId == _roots[_rootIndex].Place.OwnerId)
            _chain[0] = _roots[_rootIndex].Place;
        if (Here.IsGroup && Here.OwnerId == ActiveRoot.Place.OwnerId)
            _access = ProvisionalAccess();
        RebuildTree();
        TellOpenFolder();
        RebuildBar();
    }

    private void RebuildTree()
    {
        _sidebar?.SetTree(_roots, _rootIndex, _chain);
        _sidebar?.SetActions(_source.SignedIn && CanEditHere, _source.SignedIn);
    }

    // A different account signed in since the roots were drawn: back to its own top, with its own groups.
    private bool AccountMoved()
    {
        var account = InventorySync.Account ?? InventorySync.Service?.Account;
        if (account == _rootsAccount)
            return false;
        _rootsAccount = account;
        _groups.Clear();
        _libraryRole = null;
        _libraryName = null;
        RefreshGroups();
        NavigateToRoot();
        return true;
    }

    // NAVIGATION

    private void NavigateToRoot()
    {
        _rootIndex = 0;
        _chain.Clear();
        _chain.Add(InventoryPlace.OwnRoot);
        AfterNavigate();
    }

    private void OpenRoot(int index)
    {
        if (index < 0 || index >= _roots.Count)
            return;
        if (_carried.HasValue)
            CancelCarry();
        _rootIndex = index;
        _chain.Clear();
        _chain.Add(_roots[index].Place);
        AfterNavigate();
    }

    // A folder card, or one shared in: a shared folder opens in its owner's inventory, as far as its share
    // reaches, with the chain still hanging off the root it was found in.
    private void NavigateInto(InventoryEntry folder)
    {
        if (!folder.IsFolder)
            return;
        if (folder.IsLink && folder.LinkAccess == InventoryAccess.None)
        {
            SetStatus($"{Inventory.OwnerLabel(folder)} no longer shares \"{folder.Name}\". Remove it to tidy up.", DashTheme.Warning);
            return;
        }
        if (_carried.HasValue)
            CancelCarry();
        // A search result can sit anywhere in the tree; opening it lands there without the steps
        // between, which the rail shows as root then the folder.
        if (_search.Length > 0)
            _chain.RemoveRange(1, _chain.Count - 1);
        InventoryPlace place;
        if (folder.IsLibrary)
            NoteLibrary(folder);
        if (folder.IsLink)
        {
            place = new InventoryPlace(folder.Link!.OwnerId, folder.Link.TargetId, folder.Name, folder.Link.OwnerName);
            _access = folder.LinkAccess;
        }
        else
        {
            string ownerName = folder.OwnerId == Here.OwnerId ? Here.OwnerName : _chain[0].OwnerName;
            place = new InventoryPlace(folder.OwnerId, folder.Id, folder.Name, ownerName);
        }
        _chain.Add(place);
        AfterNavigate();
    }

    // A step of the chain by its index, 0 being the root.
    private void NavigateBackTo(int index)
    {
        if (_carried.HasValue)
            CancelCarry();
        if (index < 0 || index >= _chain.Count)
            return;
        _chain.RemoveRange(index + 1, _chain.Count - index - 1);
        AfterNavigate();
    }

    private void GoUp()
    {
        if (_chain.Count <= 1)
            return;
        if (_carried.HasValue)
            CancelCarry();
        _chain.RemoveAt(_chain.Count - 1);
        AfterNavigate();
    }

    private void AfterNavigate()
    {
        _selectedId = null;
        _mergeOffer = null;
        _pickForMove = false;
        _search = string.Empty;
        _focus = Field.None;
        _page = 0;
        _access = ProvisionalAccess();
        _canShare = _access == InventoryAccess.Owner || (Here.IsGroup && RankIn(Here.OwnerId) <= 1);
        CloseShare();
        RebuildTree();
        TellOpenFolder();
        FetchGroupQuota();
        Reload();
    }

    // What this account may do here before the listing says: all of it in its own, what its role gives in a
    // group's, and in someone's shared folder what the share gave when it was opened.
    private InventoryAccess ProvisionalAccess()
    {
        if (Here.IsOwn)
            return InventoryAccess.Owner;
        if (Here.IsLibrary)
            return RankIn(Here.OwnerId) <= 3 ? InventoryAccess.Write : InventoryAccess.Read;
        if (Here.IsGroup && GroupOf(Here.OwnerId) is { } group)
            return RoleRank(group.MyRole) <= 3 ? InventoryAccess.Write : InventoryAccess.Read;
        return _access == InventoryAccess.Owner ? InventoryAccess.Read : _access;
    }

    private GroupInfo? GroupOf(string? id)
    {
        foreach (var group in _groups)
        {
            if (group.Id == id)
                return group;
        }
        return null;
    }

    // The listing's word on this folder, which the service has the last say on.
    private void Listed(InventoryListing listing)
    {
        if (Here.IsOwn && Here.IsRoot)
        {
            foreach (var entry in listing.Entries)
            {
                if (entry.IsLibrary)
                    NoteLibrary(entry);
            }
        }
        if (listing.Problem != null && listing.Entries.Count == 0 && !Here.IsOwn)
            return;
        bool changed = listing.Access != _access || listing.CanShare != _canShare;
        _access = listing.Access;
        _canShare = listing.CanShare;
        TellOpenFolder();
        if (changed)
            RebuildTree();
    }

    // The library row says this account's role there, which decides whether deleting asks first and who reviews.
    private void NoteLibrary(in InventoryEntry row)
    {
        string? role = row.Link?.Role;
        _libraryName = row.Name;
        if (role == _libraryRole)
            return;
        _libraryRole = role;
        if (RoleRank(role) <= 2)
            RefreshGroupStates();
    }

    // Where every save goes while this folder is open, and why a save cannot land here when it cannot. The demo
    // tree a screen capture draws is never where a real save goes.
    private void TellOpenFolder()
    {
        if (_source is DemoInventorySource)
            return;
        Inventory.ShowingFolder(Here, SaveRefusal());
    }

    private string? SaveRefusal()
    {
        if (Here.IsOwn)
            return null;
        if (Here.IsLibrary && !_access.CanChange())
            return InventoryLibraryLocale.ViewOnly.Resolve();
        if (!_access.CanChange())
            return $"\"{FolderName}\" is view only for you.";
        var root = ActiveRoot;
        if (root.IsGroup && Here.OwnerId == root.Place.OwnerId && !root.CanSave)
            return root.Refusal;
        return null;
    }

    private InventoryPlace SaveHere(out string? notice)
    {
        if (_source is DemoInventorySource)
        {
            notice = null;
            return Here;
        }
        return Inventory.SavePlace(out notice);
    }

    private void FetchGroupQuota()
    {
        _groupQuota = Here.IsGroup && _quotas.TryGetValue(Here.OwnerId!, out var known) ? known : null;
        if (!Here.IsGroup || _source is not CloudInventorySource || Inventory.Client is not { IsAuthenticated: true } client)
            return;
        // everyone looks in the library; only its staff have figures there
        if (Here.IsLibrary && !_access.CanChange())
            return;
        string group = Here.OwnerId!;
        Await(client.GetGroupInventoryQuota(group), answer =>
        {
            if (answer.Success && answer.Data != null && Here.OwnerId == group)
            {
                bool lockedBefore = LockedHere;
                bool storageBefore = NoStorageHere;
                _groupQuota = answer.Data;
                _quotas[group] = answer.Data;
                if (lockedBefore != LockedHere || storageBefore != NoStorageHere)
                {
                    RebuildRoots();
                    return;
                }
                TellOpenFolder();
                RebuildStatusRow();
            }
        });
    }

    // HOW HERE LOOKS

    // The folder's name takes its root's colour in a group, and the share colour in someone's shared folder.
    private color HereInk()
    {
        if (Here.IsOwn)
            return DashTheme.TextDim;
        if (Here.IsLibrary)
            return InventoryCard.LibraryInk;
        var root = ActiveRoot;
        return root.IsGroup && Here.OwnerId == root.Place.OwnerId ? root.Ink : InventoryCard.ShareInk;
    }

    // A chip after the name says whose folder this is and what you may do in it.
    private void AddPlaceChip(Slot nav)
    {
        if (Here.IsOwn)
            return;
        var root = ActiveRoot;
        bool library = Here.IsLibrary;
        bool group = !library && root.IsGroup && Here.OwnerId == root.Place.OwnerId;
        bool warn = LockedHere || NoStorageHere;
        string text = library ? (_access.CanChange() ? InventoryLibraryLocale.ChipStaff : InventoryLibraryLocale.ChipLook).Resolve()
            : group
            ? (LockedHere ? "Group · locked" : NoStorageHere ? "Group · no storage" : _access.CanChange() ? "Group" : "Group · view only")
            : _access.CanChange() ? $"{Here.OwnerName}'s · can save" : $"{Here.OwnerName}'s · view only";
        var tint = library ? InventoryCard.LibraryInk : group ? (warn ? DashTheme.Warning : root.Tint) : InventoryCard.ShareInk;
        var slot = nav.AddSlot("Place");
        slot.AttachComponent<RectTransform>();
        float width = 22f + text.Length * 7.2f;
        var element = BrowserParts.Size(slot, width, BarHeight);
        element.MinWidth.Value = width;
        var chip = slot.AddSlot("Chip");
        var rect = chip.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(0f, 0.5f);
        rect.AnchorMax.Value = new float2(1f, 0.5f);
        rect.OffsetMin.Value = new float2(0f, -DashTheme.ChipHeight * 0.5f - 1f);
        rect.OffsetMax.Value = new float2(0f, DashTheme.ChipHeight * 0.5f + 1f);
        BrowserParts.Panel(chip, tint.WithAlpha(0.18f), DashTheme.RadiusChip, tint.WithAlpha(0.6f), 1f);
        _parts.Label(chip, text, DashTheme.FontLabel, library ? InventoryCard.LibraryInk : group ? (warn ? DashTheme.Warning : root.Ink) : InventoryCard.ShareInk,
            BrowserParts.Weight.Semibold, TextHorizontalAlignment.Center);
    }

    // What the status line adds after the counts: whose folder this is, and room left in a group.
    private string PlaceLine()
    {
        if (Here.IsOwn)
            return string.Empty;
        if (Here.IsLibrary)
        {
            if (!_access.CanChange())
                return " · " + InventoryLibraryLocale.LineLook.Resolve();
            return " · " + (_groupQuota is { } room
                ? InventoryLibraryLocale.LineStaff(Inventory.FormatBytes(room.RoomUsed), Inventory.FormatBytes(room.MyLimitBytes)).Resolve()
                : InventoryLibraryLocale.LineStaffSaves.Resolve());
        }
        var root = ActiveRoot;
        if (root.IsGroup && Here.OwnerId == root.Place.OwnerId)
        {
            var quota = _groupQuota;
            if (quota is { IsLocked: true })
                return " · locked: its owner's support has lapsed, so nothing new can go in. What is here stays.";
            string grace = quota is { InGrace: true }
                ? $" · storage locks {(quota.LockAt is { } at ? at.ToLocalTime().ToString("d MMM") : "soon")} unless its owner's support resumes"
                : string.Empty;
            if (!_access.CanChange())
                return " · view only for you" + grace;
            if (NoStorageHere)
                return " · no storage set for you here: browse and spawn, and saves go to your inventory" + grace;
            if (quota != null)
                return (quota.FromPool
                    ? $" · group pool: {Inventory.FormatBytes(quota.PoolUsedBytes)} of {Inventory.FormatBytes(quota.PoolBytes)}"
                    : $" · your share: {Inventory.FormatBytes(quota.MyUsedBytes)} of {Inventory.FormatBytes(quota.MyLimitBytes)}") + grace;
            return root.CanSave ? " · saves land here" : " · no storage of yours here";
        }
        return _access.CanChange()
            ? $" · {Here.OwnerName} shares this with you; saves land here"
            : $" · {Here.OwnerName} shares this with you to look at";
    }

    // The status line goes amber in a group that is locked or about to be, or that gives this account no storage.
    private bool PlaceWarns() => Here.IsGroup && (_groupQuota is { } quota && (quota.IsLocked || quota.InGrace) || NoStorageHere);

    // A link card in a world asking to show what it leads to: the window opens, a folder opens at the top of your
    // own inventory the way a shared folder does, an item comes out the way the window spawns things. The card
    // already checked that this account reaches it.
    private void OnOpenRequested(InventoryOpenRequest request) => OnUi(() => ShowShared(request));

    private void ShowShared(InventoryOpenRequest request)
    {
        var dash = Dashboard.Of(Slot);
        var local = UserspaceDashboard.LocalInstance;
        if (local != null && ReferenceEquals(local.Dashboard, dash) && !local.IsOpen.Value)
            local.Open();
        dash?.SwitchTo(this);
        if (request.Item is { } item)
        {
            if (item.Kind == InventoryEntryKind.World)
                OpenWorld(item);
            else
                SpawnItem(item);
            return;
        }
        if (_carried.HasValue)
            CancelCarry();
        bool mine = request.OwnerId == InventorySync.Account;
        _rootIndex = 0;
        _chain.Clear();
        _chain.Add(InventoryPlace.OwnRoot);
        _chain.Add(new InventoryPlace(mine ? null : request.OwnerId, request.TargetId, request.Name, mine ? string.Empty : request.OwnerName));
        _access = mine ? InventoryAccess.Owner : InventoryAccess.Read;
        AfterNavigate();
    }

    private void CopyLink(InventoryEntry entry)
    {
        var text = Inventory.LinkText(entry);
        var clipboard = Engine.Current?.InputInterface?.ClipboardText;
        if (text == null || clipboard == null)
        {
            SetStatus("Couldn't copy that link.", DashTheme.Warning);
            return;
        }
        clipboard.SetText(text);
        SetStatus("Link copied. Whoever it is shared with can paste it with + Link.", DashTheme.TextDim);
    }
}
