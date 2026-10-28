// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI.Listing;
using Lumora.Core.Localization;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// What a pressed control in the Groups window means. Rows carry one per pill, so the relay stays a lookup
// and every decision lives on the screen. -xlinka
internal enum GroupAction
{
    None,
    Open,
    Join,
    AcceptInvite,
    DeclineInvite,
    Leave,
    Represent,
    Unrepresent,
    Approve,
    Deny,
    Kick,
    Ban,
    Unban,
    MakeOwner,
    Withdraw,
    Invite,
    Save,
    PostAnnouncement,
    DeleteAnnouncement,
    CreateEvent,
    DeleteEvent,
    HostEventWorld,
    JoinEventWorld,
    DeleteGroup,
    Create,
    CancelCreate,
    EvenShare,
}

internal static class GroupMetrics
{
    public const float Pad = 12f;
    public const float SplitGap = 12f;
    // Below this a window stacks the list and the group instead of putting them side by side.
    public const float SplitMinWidth = 760f;
    public const float ListMinWidth = 270f;
    public const float ListMaxWidth = 340f;
    public const float SearchHeight = 36f;
    public const float SwitchHeight = 30f;
    public const float NewWidth = 112f;
    public const float IconButton = 36f;

    public const float RowHeight = 56f;
    public const float InviteRowHeight = 70f;
    public const float RowMark = 38f;
    public const float InvitePill = 62f;

    public const float BannerHeight = 76f;
    public const float MarkSize = 74f;
    public const float MarkRing = 4f;
    public const float MarkTop = 38f;
    public const float MarkLeft = 18f;
    public const float NameLeft = MarkLeft + MarkSize + 14f;
    public const float TagTop = 108f;
    public const float TagHeight = 18f;
    public const float ActionHeight = 30f;
    public const float Segment = 96f;
    public const float TitleHeight = 44f;
    public const float MoreWidth = 34f;

    public const float PillHeight = 26f;
    public const float PillGap = 8f;
    public const float ActionWidth = 104f;

    public const float NoteHeight = 26f;
    public const float MemberHeight = 54f;
    public const float MemberBand = 34f;
    public const float MemberFace = 36f;
    public const float MemberPill = 64f;
    public const float OwnerPill = 104f;
    public const float RolePill = 86f;
    public const float PersonHeight = 52f;
    public const float EventHeight = 64f;
    public const float DateTile = 46f;
    public const float EventPill = 64f;
    public const float FormLine = 60f;
    public const float FormField = 32f;
    public const float FormBio = 96f;
    public const float FormPill = 96f;
    public const float SegmentHeight = 68f;
    public const float ActionsHeight = 40f;
    public const float StorageHeight = 58f;
    public const float ShareHeight = 88f;
    public const float PictureHeight = 96f;
    public const float PicturePreview = 56f;
    public const float BannerPreview = 150f;
}

// One group in the list: a group to open, or an invite waiting on you with its two answers beside it.
internal sealed class GroupRowItem : ListingItem
{
    public string GroupId = string.Empty;
    public string Name = string.Empty;
    public string GroupTag = string.Empty;
    public string? IconHash;
    public color Tint = DashTheme.Accent;
    public color Ink = DashTheme.Text;
    public string Line = string.Empty;
    public string Chip = string.Empty;
    public color ChipTint = DashTheme.TextDim;
    public bool Invite;
    public Func<string, bool>? IsShown;

    public GroupRowItem(string key) : base(key)
    {
        Kind = GroupsScreen.KindRow;
    }
}

// One member. Read only for most; for an admin the role ladder, the storage slice and the escalations.
internal sealed class GroupMemberItem : ListingItem
{
    public string UserId = string.Empty;
    public string Name = string.Empty;
    public string Role = string.Empty;
    public string Line = string.Empty;
    public bool IsSelf;

    public IReadOnlyList<string> Roles = Array.Empty<string>();
    public bool ShowStorage;
    public long Allocated;
    public long UsedBytes;
    public long SliderMax;
    public Func<string, long>? PendingSlice;

    public bool ShowKick;
    public bool ShowBan;
    public bool ShowMakeOwner;

    public bool HasActions => ShowKick || ShowBan || ShowMakeOwner;

    public GroupMemberItem(string key) : base(key)
    {
        Kind = GroupsScreen.KindMember;
    }
}

// A person with a note and up to two answers: a join request, an invite out, a ban.
internal sealed class GroupPersonItem : ListingItem
{
    public string UserId = string.Empty;
    public string Name = string.Empty;
    public string Note = string.Empty;

    public string PrimaryLabel = string.Empty;
    public GroupAction Primary;
    public bool PrimaryLit;
    public bool PrimaryDanger;
    public string SecondaryLabel = string.Empty;
    public GroupAction Secondary;
    public bool SecondaryDanger;

    public GroupPersonItem(string key) : base(key)
    {
        Kind = GroupsScreen.KindPerson;
    }
}

// An event. Host opens the Worlds create form filled in and hosts nothing on its own; Live is lit when the
// session browser is actually listing a session for this group under this event's name.
internal sealed class GroupEventItem : ListingItem
{
    public string EventId = string.Empty;
    public string Title = string.Empty;
    public string Meta = string.Empty;
    public string Month = string.Empty;
    public string Day = string.Empty;
    public color Tint = DashTheme.Accent;
    public bool CanDelete;
    public bool CanHost;
    public bool IsLive;

    public GroupEventItem(string key) : base(key)
    {
        Kind = GroupsScreen.KindEvent;
    }
}

// An announcement: who and when over the whole text, wrapped. Its height is worked out from the pane's width.
internal sealed class GroupPostItem : ListingItem
{
    public string PostId = string.Empty;
    public string Text = string.Empty;
    public string Meta = string.Empty;
    public bool CanDelete;
    public float Height = 72f;

    public GroupPostItem(string key) : base(key)
    {
        Kind = GroupsScreen.KindPost;
    }
}

// One card of typed values, a label over each, on one or two lines, with the pill that sends them. The row
// is pooled, so typing writes through the cell to the screen's buffer, never to the row.
internal sealed class GroupFormItem : ListingItem
{
    internal sealed class Cell
    {
        public string Label = string.Empty;
        public string Placeholder = string.Empty;
        public int MaxLength = 64;
        public float Weight = 1f;
        public int Line;
        public Func<string>? Read;
        public Action<string>? Write;
    }

    public readonly List<Cell> Cells = new();
    public bool Multiline;
    public string PillLabel = string.Empty;
    public GroupAction Pill;
    // Enter in any of the cells.
    public Action? Submit;

    // One line of fields with no labels over them: a composer whose section header already says what it is.
    public bool Compact
    {
        get
        {
            if (Multiline)
                return false;
            for (int i = 0; i < Cells.Count; i++)
            {
                if (Cells[i].Line != 0 || Cells[i].Label.Length > 0)
                    return false;
            }
            return true;
        }
    }

    public int Lines
    {
        get
        {
            int lines = 1;
            for (int i = 0; i < Cells.Count; i++)
                lines = System.Math.Max(lines, Cells[i].Line + 1);
            return lines;
        }
    }

    public GroupFormItem(string key) : base(key)
    {
        Kind = GroupsScreen.KindForm;
    }

    public GroupFormItem Add(LocaleText label, LocaleText placeholder, int maxLength, Func<string> read, Action<string> write,
        float weight = 1f, int line = 0)
    {
        Cells.Add(new Cell
        {
            Label = label.IsEmpty ? string.Empty : label.Resolve(),
            Placeholder = placeholder.Resolve(),
            MaxLength = maxLength,
            Weight = weight,
            Line = line,
            Read = read,
            Write = write,
        });
        return this;
    }
}

// One of a few: who may join.
internal sealed class GroupSegmentItem : ListingItem
{
    public string[] Options = Array.Empty<string>();
    public Func<int>? Read;
    public Action<int>? Write;

    public GroupSegmentItem(string key, LocaleText label) : base(key)
    {
        LabelText = label;
        Kind = GroupsScreen.KindSegment;
    }
}

internal sealed class GroupActionsItem : ListingItem
{
    internal readonly struct Entry
    {
        public readonly string Label;
        public readonly GroupAction Action;
        public readonly bool Danger;
        public readonly bool Lit;
        public readonly float Width;

        public Entry(string label, GroupAction action, bool danger, bool lit, float width)
        {
            Label = label;
            Action = action;
            Danger = danger;
            Lit = lit;
            Width = width;
        }
    }

    public readonly List<Entry> Buttons = new();

    public GroupActionsItem(string key) : base(key)
    {
        Kind = GroupsScreen.KindActions;
    }

    public GroupActionsItem Add(string label, GroupAction action, bool danger = false, bool lit = false,
        float width = GroupMetrics.ActionWidth)
    {
        Buttons.Add(new Entry(label, action, danger, lit, width));
        return this;
    }
}

// The group's icon or its banner on the Admin page: what it is now, or what was just picked, and the two ways
// to change it.
internal sealed class GroupPictureItem : ListingItem
{
    public bool Banner;
    public string? Hash;
    public string Name = string.Empty;
    public string GroupTag = string.Empty;
    public color Tint = DashTheme.Accent;
    public string Status = string.Empty;

    public GroupPictureItem(string key, LocaleText label) : base(key)
    {
        LabelText = label;
        Kind = GroupsScreen.KindPicture;
    }
}

internal sealed class GroupStorageItem : ListingItem
{
    public long Used;
    public long Allocated;
    public long Quota;

    public GroupStorageItem(string key, LocaleText label) : base(key)
    {
        LabelText = label;
        Kind = GroupsScreen.KindStorage;
        Interactable = false;
    }
}

internal sealed class GroupShareItem : ListingItem
{
    public long Min;
    public long Max;
    public long Sliced;
    public Func<long>? Pending;

    public GroupShareItem(string key, LocaleText label) : base(key)
    {
        LabelText = label;
        Kind = GroupsScreen.KindShare;
    }
}
