// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Components.UI.Worlds;
using Lumora.Core.Math;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// SHARING: one panel over the inventory for one folder or item. On top, everyone who can reach it now, each
// with what they may do (look, or save into it too) and a way to take it back; grants on the folders above it
// are listed under them, greyed, since they reach it too. Below, the friends and groups it can go to, a press
// each, and the open link: on, it can be copied and pasted into any inventory. Stop sharing takes every grant
// back at once. -xlinka
public sealed partial class InventoryScreen
{
    private const float ShareWidth = 560f;
    private const float ShareHeight = 500f;
    private const float ShareInset = 22f;
    private const float ShareRow = 34f;
    private const int ShareRowsShown = 4;
    private const int ShareChipsPerPage = 6;
    private const float ShareChipWidth = 160f;
    private const float PickerTop = 290f;

    private enum ShareTab { Friends, Groups }

    private Slot? _shareRoot;
    private Slot? _shareBody;
    private bool _shareOpen;
    private InventoryEntry _shareEntry;
    private InventoryShareListing? _shareListing;
    private string? _shareProblem;
    private bool _shareBusy;
    private ShareTab _shareTab = ShareTab.Friends;
    private string _shareAccess = Inventory.AccessRead;
    private int _sharePage;
    private int _shareSerial;

    private void BuildShareDialog(Slot root)
    {
        _shareRoot = root.AddSlot("Share");
        BrowserParts.Fill(_shareRoot.AttachComponent<RectTransform>());
        _shareRoot.AttachComponent<IgnoreLayout>();
        _shareRoot.ActiveSelf.Value = false;

        // Every card is a chunk of its own, so the panel needs a render band above them or the grid draws
        // through it: the scrim on the first overlay band, the panel on the next. -xlinka
        var backdrop = _shareRoot.AddSlot("Backdrop");
        BrowserParts.Fill(backdrop.AttachComponent<RectTransform>());
        backdrop.AttachComponent<GraphicChunkRoot>().OverlayLevel = 1;
        backdrop.AttachComponent<Button>().Clicked += (_, _) => CloseShare();
        backdrop.AttachComponent<Image>().Tint.Value = DashTheme.ModalScrim;

        var panel = _shareRoot.AddSlot("Panel");
        var panelRect = panel.AttachComponent<RectTransform>();
        panelRect.AnchorMin.Value = new float2(0.5f, 0.5f);
        panelRect.AnchorMax.Value = new float2(0.5f, 0.5f);
        panelRect.OffsetMin.Value = new float2(-ShareWidth * 0.5f, -ShareHeight * 0.5f);
        panelRect.OffsetMax.Value = new float2(ShareWidth * 0.5f, ShareHeight * 0.5f);
        panel.AttachComponent<GraphicChunkRoot>().OverlayLevel = 2;
        BrowserParts.Panel(panel, DashTheme.Panel, DashTheme.RadiusCard, InventoryCard.ShareInk.WithAlpha(0.45f), 1.5f);
        panel.AttachComponent<Button>();

        // A teal edge along the top says which panel this is at a glance.
        var band = panel.AddSlot("Band");
        BrowserParts.PinTop(band.AttachComponent<RectTransform>(), 0f, 4f, DashTheme.RadiusCard);
        BrowserParts.Panel(band, InventoryCard.ShareInk.WithAlpha(0.85f), 2f);

        _shareBody = BrowserParts.Child(panel, "Body");
    }

    private void OpenShare(InventoryEntry entry)
    {
        if (_shareRoot == null || entry.IsLink || !_canShare)
            return;
        if (_carried.HasValue)
            CancelCarry();
        CloseDialog();
        _shareOpen = true;
        _shareEntry = entry;
        _shareListing = null;
        _shareProblem = null;
        _shareBusy = true;
        _sharePage = 0;
        _shareTab = ShareTab.Friends;
        _shareAccess = Inventory.AccessRead;
        _shareRoot.ActiveSelf.Value = true;
        RebuildShare();
        int serial = ++_shareSerial;
        Await(_source.Shares(entry), answer => ShareAnswered(serial, answer));
    }

    private void CloseShare()
    {
        if (!_shareOpen || _shareRoot == null)
            return;
        _shareOpen = false;
        _shareSerial++;
        _shareRoot.ActiveSelf.Value = false;
        MarkDirty();
        Reload();
    }

    private void ShareAnswered(int serial, ApiResponse<InventoryShareListing> answer)
    {
        if (serial != _shareSerial || !_shareOpen)
            return;
        _shareBusy = false;
        if (answer.Success && answer.Data != null)
        {
            _shareListing = answer.Data;
            _shareProblem = null;
        }
        else
            _shareProblem = Inventory.ShareProblem(answer);
        RebuildShare();
    }

    private void ShareCall(System.Threading.Tasks.Task<ApiResponse<InventoryShareListing>> call, string? done = null)
    {
        _shareBusy = true;
        _shareProblem = null;
        RebuildShare();
        int serial = ++_shareSerial;
        Await(call, answer =>
        {
            ShareAnswered(serial, answer);
            if (answer.Success && done != null)
                SetStatus(done, DashTheme.TextDim);
        });
    }

    private void Grant(string kind, string? id, string name)
        => ShareCall(_source.Share(_shareEntry, kind, id, _shareAccess),
            kind == Inventory.SharePublic ? $"Anyone with the link can see \"{_shareEntry.Name}\"." : $"Shared \"{_shareEntry.Name}\" with {name}.");

    private void SetGrantAccess(InventoryShareGrant grant, string access)
        => ShareCall(_source.Share(_shareEntry, grant.Kind, grant.Id, access));

    private void TakeBack(InventoryShareGrant grant)
        => ShareCall(_source.Unshare(_shareEntry, grant.Kind, grant.Id),
            grant.Kind == Inventory.SharePublic ? "The link is off." : $"{GrantName(grant)} can no longer reach \"{_shareEntry.Name}\".");

    private void StopSharing()
        => ShareCall(_source.Unshare(_shareEntry, null, null), $"\"{_shareEntry.Name}\" is no longer shared.");

    // A round mark holds three letters at most; a longer tag gives way to the name's initials.
    private static string ShortTag(string tag, string name) => tag.Length is > 0 and <= 3 ? tag : PeopleFormat.Initials(name);

    private static string GrantName(InventoryShareGrant grant) => grant.Kind switch
    {
        Inventory.SharePublic => "Anyone with the link",
        _ => grant.Name.Length > 0 ? grant.Name : grant.Id,
    };

    // The whole panel again: it is small, and every change to it comes back as a fresh listing.
    private void RebuildShare()
    {
        var body = _shareBody;
        if (body == null || body.IsDestroyed)
            return;
        body.DestroyChildren();
        var entry = _shareEntry;
        var listing = _shareListing;

        var title = PinnedTop(body, "Title", 18f, 28f);
        _parts.Label(title, $"Share \"{BrowserParts.Truncate(entry.Name, 24)}\"", DashTheme.FontHeading, DashTheme.Text, BrowserParts.Weight.Bold,
            TextHorizontalAlignment.Left, 0f, 140f);
        var spawn = _parts.AddButton(body, "Spawn link card", 132f, 28f, ShareFill, InventoryCard.ShareInk.WithAlpha(0.45f),
            InventoryCard.ShareInk.WithAlpha(0.55f), InventoryCard.ShareInk, BrowserParts.Weight.Semibold, () => SpawnLinkCard(_shareEntry));
        var spawnRect = spawn.Root.GetComponent<RectTransform>()!;
        spawnRect.AnchorMin.Value = new float2(1f, 1f);
        spawnRect.AnchorMax.Value = new float2(1f, 1f);
        spawnRect.OffsetMin.Value = new float2(-(ShareInset + 132f), -(18f + 28f));
        spawnRect.OffsetMax.Value = new float2(-ShareInset, -18f);
        var sub = PinnedTop(body, "Sub", 46f, 18f);
        _parts.Label(sub, entry.IsFolder ? "Everything inside it goes along, and anything saved in it later." : "Who can see it, spawn it and keep a copy.",
            DashTheme.FontSmall, DashTheme.TextDim, BrowserParts.Weight.Regular);

        float y = 76f;
        SectionLabel(body, "WHO CAN REACH IT", y);
        y += 22f;

        var grants = listing?.Grants ?? new List<InventoryShareGrant>();
        InventoryShareGrant? open = null;
        foreach (var grant in grants)
        {
            if (grant.Kind == Inventory.SharePublic)
                open = grant;
        }
        int shown = 0;
        AddOwnerRow(body, y);
        y += ShareRow;
        foreach (var grant in grants)
        {
            if (grant.Kind == Inventory.SharePublic)
                continue;
            if (shown == ShareRowsShown)
                break;
            AddGrantRow(body, grant, y);
            y += ShareRow;
            shown++;
        }
        int hidden = grants.Count - (open != null ? 1 : 0) - shown;
        if (listing != null)
        {
            foreach (var inherited in listing.Inherited)
            {
                if (shown + 1 > ShareRowsShown)
                {
                    hidden++;
                    continue;
                }
                AddInheritedRow(body, inherited, y);
                y += ShareRow - 6f;
                shown++;
            }
        }
        if (hidden > 0)
        {
            var more = PinnedTop(body, "More", y, 18f);
            _parts.Label(more, $"and {hidden} more", DashTheme.FontSmall, DashTheme.TextMuted, BrowserParts.Weight.Regular);
            y += 20f;
        }
        if (listing == null && _shareBusy)
        {
            var wait = PinnedTop(body, "Wait", y, 20f);
            _parts.Label(wait, "Asking who can reach it…", DashTheme.FontSmall, DashTheme.TextDim, BrowserParts.Weight.Regular);
        }

        AddPicker(body, listing, grants);
        AddFooter(body, open);
    }

    private Slot PinnedTop(Slot parent, string name, float y, float height)
    {
        var slot = parent.AddSlot(name);
        BrowserParts.PinTop(slot.AttachComponent<RectTransform>(), y, height, ShareInset);
        return slot;
    }

    private void SectionLabel(Slot parent, string text, float y)
    {
        var slot = PinnedTop(parent, "Section", y, 18f);
        _parts.Label(slot, text, DashTheme.FontLabel, DashTheme.TextMuted, BrowserParts.Weight.Semibold);
    }

    // A small round mark: a person's initial, a group's tag on its colour, the link as a teal ring.
    private void Mark(Slot row, string letters, in color tint, bool ring = false)
    {
        var slot = row.AddSlot("Mark");
        var rect = slot.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(0f, 0.5f);
        rect.AnchorMax.Value = new float2(0f, 0.5f);
        rect.OffsetMin.Value = new float2(0f, -12f);
        rect.OffsetMax.Value = new float2(24f, 12f);
        BrowserParts.Panel(slot, ring ? tint.WithAlpha(0.15f) : tint.WithAlpha(0.55f), 12f, ring ? tint : null, 1.5f);
        _parts.Label(slot, letters, DashTheme.FontLabel, DashTheme.Text, BrowserParts.Weight.Bold, TextHorizontalAlignment.Center);
    }

    private Slot ShareRowAt(Slot body, float y, string name)
    {
        var row = PinnedTop(body, name, y + 3f, ShareRow - 6f);
        return row;
    }

    // The name on the left; a quiet note right-aligned before whatever pills the row has (pillRoom wide).
    private void RowText(Slot row, string text, in color ink, string? note = null, float pillRoom = 186f)
    {
        var slot = row.AddSlot("Name");
        var rect = slot.AttachComponent<RectTransform>();
        BrowserParts.Fill(rect);
        rect.OffsetMin.Value = new float2(34f, 0f);
        rect.OffsetMax.Value = new float2(-pillRoom, 0f);
        _parts.Label(slot, BrowserParts.Truncate(text, 24), DashTheme.FontBody, ink, BrowserParts.Weight.Semibold);
        if (note != null)
            _parts.Label(slot, note, DashTheme.FontSmall, DashTheme.TextMuted, BrowserParts.Weight.Regular, TextHorizontalAlignment.Right, 0f, 8f);
    }

    private PillButton RowPill(Slot row, string text, float right, float width, in color fill, in color hover, in color ink, Action pressed)
    {
        var pill = _parts.AddButton(row, text, width, ShareRow - 8f, fill, hover, hover, ink, BrowserParts.Weight.Semibold, pressed);
        BrowserParts.PinRight(pill.Root.GetComponent<RectTransform>()!, right, width, 1f);
        pill.SetEnabled(!_shareBusy);
        return pill;
    }

    private void AddOwnerRow(Slot body, float y)
    {
        var row = ShareRowAt(body, y, "Owner");
        var root = ActiveRoot;
        if (Here.IsGroup && root.IsGroup)
        {
            Mark(row, ShortTag(root.Tag, root.Place.Name), root.Tint);
            RowText(row, root.Place.Name, root.Ink, "builders save, members look", 0f);
        }
        else
        {
            Mark(row, "Me", DashTheme.Accent);
            RowText(row, "You", DashTheme.Text, "owner", 0f);
        }
    }

    private void AddGrantRow(Slot body, InventoryShareGrant grant, float y)
    {
        var row = ShareRowAt(body, y, "Grant");
        bool group = grant.Kind == Inventory.ShareGroup;
        var groupInfo = group ? GroupOf(grant.Id) : null;
        var tint = group ? GroupsFormat.Tint(groupInfo?.Color) : GroupsFormat.PersonTint(grant.Name);
        string letters = group ? ShortTag(groupInfo?.Tag ?? string.Empty, grant.Name) : PeopleFormat.Initials(grant.Name);
        Mark(row, letters, tint);
        RowText(row, GrantName(grant), group ? GroupsFormat.Ink(groupInfo?.Color) : DashTheme.Text, group ? "group" : null);
        bool writes = grant.Access == Inventory.AccessWrite;
        RowPill(row, "Remove", 0f, 78f, GroupPaint.DangerFill, GroupPaint.DangerHover, DashTheme.Negative, () => TakeBack(grant));
        // One pill that says what they may do now and flips it on a press.
        RowPill(row, writes ? "Can save" : "Can look", 86f, 96f, writes ? ShareFill : DashTheme.Surface, DashTheme.SurfaceHover,
            writes ? InventoryCard.ShareInk : DashTheme.TextDim,
            () => SetGrantAccess(grant, writes ? Inventory.AccessRead : Inventory.AccessWrite));
    }

    private void AddInheritedRow(Slot body, InventoryInheritedGrant grant, float y)
    {
        var row = PinnedTop(body, "Inherited", y + 1f, ShareRow - 10f);
        string who = grant.Kind == Inventory.SharePublic ? "Anyone with the link" : grant.Name;
        string what = grant.Access == Inventory.AccessWrite ? "can save" : "can look";
        var slot = row.AddSlot("Text");
        var rect = slot.AttachComponent<RectTransform>();
        BrowserParts.Fill(rect);
        rect.OffsetMin.Value = new float2(34f, 0f);
        _parts.Label(slot, $"{who} · {what}, through \"{BrowserParts.Truncate(grant.FromName, 22)}\"", DashTheme.FontSmall, DashTheme.TextMuted,
            BrowserParts.Weight.Regular);
    }

    // Friends or groups not reached yet, a page at a time, and what a press gives them.
    private void AddPicker(Slot body, InventoryShareListing? listing, List<InventoryShareGrant> grants)
    {
        const float Top = PickerTop;
        SectionLabel(body, "SHARE WITH", Top);

        var tabs = body.AddSlot("Tabs");
        var tabsRect = tabs.AttachComponent<RectTransform>();
        BrowserParts.PinTop(tabsRect, Top - 4f, 26f, ShareInset);
        TabPill(tabs, "Friends", 104f, _shareTab == ShareTab.Friends, () => { _shareTab = ShareTab.Friends; _sharePage = 0; RebuildShare(); });
        TabPill(tabs, "Groups", 180f, _shareTab == ShareTab.Groups, () => { _shareTab = ShareTab.Groups; _sharePage = 0; RebuildShare(); });
        bool writes = _shareAccess == Inventory.AccessWrite;
        var gives = _parts.AddButton(tabs, writes ? "They can save" : "They can look", 130f, 26f, writes ? ShareFill : DashTheme.Surface,
            DashTheme.SurfaceHover, DashTheme.SurfacePressed, writes ? InventoryCard.ShareInk : DashTheme.TextDim, BrowserParts.Weight.Semibold,
            () => { _shareAccess = writes ? Inventory.AccessRead : Inventory.AccessWrite; RebuildShare(); });
        BrowserParts.PinRight(gives.Root.GetComponent<RectTransform>()!, 0f, 130f, 0f);

        var reached = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in grants)
            reached.Add(grant.Kind + ":" + grant.Id);
        var choices = new List<(string kind, string id, string name, string letters, color tint)>();
        if (_shareTab == ShareTab.Friends)
        {
            foreach (var friend in _source.Friends)
            {
                if (!reached.Contains(Inventory.ShareUser + ":" + friend.UserId))
                    choices.Add((Inventory.ShareUser, friend.UserId, friend.Username, PeopleFormat.Initials(friend.Username), GroupsFormat.PersonTint(friend.Username)));
            }
        }
        else
        {
            foreach (var group in _groups)
            {
                if (group.Id != Here.OwnerId && !reached.Contains(Inventory.ShareGroup + ":" + group.Id))
                    choices.Add((Inventory.ShareGroup, group.Id, group.Name, ShortTag(group.Tag, group.Name), GroupsFormat.Tint(group.Color)));
            }
        }
        choices.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

        int pages = System.Math.Max(1, (choices.Count + ShareChipsPerPage - 1) / ShareChipsPerPage);
        _sharePage = System.Math.Clamp(_sharePage, 0, pages - 1);
        if (choices.Count == 0)
        {
            var none = PinnedTop(body, "None", Top + 30f, 20f);
            _parts.Label(none, _shareTab == ShareTab.Friends
                ? (_source.Friends.Count == 0 ? "No friends yet. Add some in the Friends window." : "Every friend can reach it already.")
                : (_groups.Count == 0 ? "You are in no groups." : "Every group of yours can reach it already."),
                DashTheme.FontSmall, DashTheme.TextDim, BrowserParts.Weight.Regular);
            return;
        }
        int first = _sharePage * ShareChipsPerPage;
        for (int i = first; i < System.Math.Min(choices.Count, first + ShareChipsPerPage); i++)
        {
            var (kind, id, name, letters, tint) = choices[i];
            int at = i - first;
            float x = ShareInset + (at % 3) * (ShareChipWidth + 8f);
            float cy = Top + 30f + (at / 3) * 32f;
            var chip = _parts.AddButton(body, string.Empty, ShareChipWidth, 28f, DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.SurfacePressed,
                DashTheme.Text, BrowserParts.Weight.Semibold, () => Grant(kind, id, name));
            var rect = chip.Root.GetComponent<RectTransform>()!;
            rect.AnchorMin.Value = new float2(0f, 1f);
            rect.AnchorMax.Value = new float2(0f, 1f);
            rect.OffsetMin.Value = new float2(x, -(cy + 28f));
            rect.OffsetMax.Value = new float2(x + ShareChipWidth, -cy);
            chip.SetEnabled(!_shareBusy);
            Mark(chip.Root, letters, tint);
            var mark = chip.Root.FindChild("Mark", recursive: false)!.GetComponent<RectTransform>()!;
            mark.OffsetMin.Value = new float2(3f, -11f);
            mark.OffsetMax.Value = new float2(25f, 11f);
            var label = chip.Text.Slot.GetComponent<RectTransform>()!;
            label.OffsetMin.Value = new float2(32f, 0f);
            label.OffsetMax.Value = new float2(-8f, 0f);
            chip.Text.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
            BrowserParts.SetText(chip.Text, "+ " + BrowserParts.Truncate(name, 15));
        }
        if (pages > 1)
        {
            var pager = PinnedTop(body, "Pager", Top + 30f + 2 * 32f, 22f);
            _parts.Label(pager, $"{_sharePage + 1} / {pages}", DashTheme.FontSmall, DashTheme.TextDim, BrowserParts.Weight.Regular,
                TextHorizontalAlignment.Right, 0f, 92f);
            var next = _parts.AddButton(pager, "›", 36f, 22f, DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.Text,
                BrowserParts.Weight.Semibold, () => { _sharePage = (_sharePage + 1) % pages; RebuildShare(); });
            BrowserParts.PinRight(next.Root.GetComponent<RectTransform>()!, 0f, 36f, 0f);
            var prev = _parts.AddButton(pager, "‹", 36f, 22f, DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.Text,
                BrowserParts.Weight.Semibold, () => { _sharePage = (_sharePage + pages - 1) % pages; RebuildShare(); });
            BrowserParts.PinRight(prev.Root.GetComponent<RectTransform>()!, 42f, 36f, 0f);
        }
    }

    private void TabPill(Slot parent, string text, float left, bool on, Action pressed)
    {
        var pill = _parts.AddButton(parent, text, 70f, 26f, on ? DashTheme.Accent : DashTheme.Surface, on ? DashTheme.AccentHover : DashTheme.SurfaceHover,
            on ? DashTheme.AccentPressed : DashTheme.SurfacePressed, on ? DashTheme.OnAccent : DashTheme.TextDim, BrowserParts.Weight.Semibold, pressed);
        var rect = pill.Root.GetComponent<RectTransform>()!;
        rect.AnchorMin.Value = new float2(0f, 0f);
        rect.AnchorMax.Value = new float2(0f, 1f);
        rect.OffsetMin.Value = new float2(left, 0f);
        rect.OffsetMax.Value = new float2(left + 70f, 0f);
    }

    // The link on one side, the problem line, and the two ways out on the other.
    private void AddFooter(Slot body, InventoryShareGrant? open)
    {
        var problem = body.AddSlot("Problem");
        BrowserParts.PinBottom(problem.AttachComponent<RectTransform>(), 58f, 18f, ShareInset);
        _parts.Label(problem, _shareProblem ?? (_shareBusy && _shareListing != null ? "Working…" : string.Empty), DashTheme.FontSmall,
            _shareProblem != null ? DashTheme.Warning : DashTheme.TextDim, BrowserParts.Weight.Regular);

        var foot = body.AddSlot("Foot");
        BrowserParts.PinBottom(foot.AttachComponent<RectTransform>(), 16f, DashTheme.ControlHeight, ShareInset);

        var done = _parts.AddButton(foot, "Done", 84f, DashTheme.ControlHeight, DashTheme.Accent, DashTheme.AccentHover, DashTheme.AccentPressed,
            DashTheme.OnAccent, BrowserParts.Weight.Semibold, CloseShare);
        BrowserParts.PinRight(done.Root.GetComponent<RectTransform>()!, 0f, 84f, 0f);
        bool any = _shareListing != null && _shareListing.Grants.Count > 0;
        if (any)
        {
            var stop = _parts.AddButton(foot, "Stop sharing", 116f, DashTheme.ControlHeight, GroupPaint.DangerFill, GroupPaint.DangerHover,
                GroupPaint.DangerHover, DashTheme.Negative, BrowserParts.Weight.Semibold, StopSharing);
            BrowserParts.PinRight(stop.Root.GetComponent<RectTransform>()!, 92f, 116f, 0f);
            stop.SetEnabled(!_shareBusy);
        }

        if (_shareListing == null)
            return;
        if (open == null)
        {
            var make = _parts.AddButton(foot, "Turn the link on", 150f, DashTheme.ControlHeight, DashTheme.Surface, DashTheme.SurfaceHover,
                DashTheme.SurfacePressed, InventoryCard.ShareInk, BrowserParts.Weight.Semibold, () => Grant(Inventory.SharePublic, null, string.Empty));
            var rect = make.Root.GetComponent<RectTransform>()!;
            rect.AnchorMin.Value = new float2(0f, 0f);
            rect.AnchorMax.Value = new float2(0f, 1f);
            rect.OffsetMin.Value = new float2(0f, 0f);
            rect.OffsetMax.Value = new float2(150f, 0f);
            make.SetEnabled(!_shareBusy);
            return;
        }
        var copy = _parts.AddButton(foot, "Copy link", 104f, DashTheme.ControlHeight, ShareFill, InventoryCard.ShareInk.WithAlpha(0.45f),
            InventoryCard.ShareInk.WithAlpha(0.55f), InventoryCard.ShareInk, BrowserParts.Weight.Semibold, () => CopyLink(_shareEntry));
        var copyRect = copy.Root.GetComponent<RectTransform>()!;
        copyRect.AnchorMin.Value = new float2(0f, 0f);
        copyRect.AnchorMax.Value = new float2(0f, 1f);
        copyRect.OffsetMin.Value = new float2(0f, 0f);
        copyRect.OffsetMax.Value = new float2(104f, 0f);
        var off = _parts.AddButton(foot, "Link off", 84f, DashTheme.ControlHeight, DashTheme.Surface, DashTheme.SurfaceHover,
            DashTheme.SurfacePressed, DashTheme.TextDim, BrowserParts.Weight.Semibold, () => TakeBack(open));
        var offRect = off.Root.GetComponent<RectTransform>()!;
        offRect.AnchorMin.Value = new float2(0f, 0f);
        offRect.AnchorMax.Value = new float2(0f, 1f);
        offRect.OffsetMin.Value = new float2(110f, 0f);
        offRect.OffsetMax.Value = new float2(194f, 0f);
        off.SetEnabled(!_shareBusy);
    }
}
