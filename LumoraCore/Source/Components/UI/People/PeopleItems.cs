// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI.Listing;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// What a pressed control in the People window means. Rows carry one per pill, so the relay stays a lookup
// and every decision lives on the screen.
internal enum PeopleAction
{
    None,
    OpenPerson,
    OpenSelf,
    OpenRequests,
    OpenBlocked,
    AddFriend,
    Accept,
    Decline,
    CancelRequest,
    Unblock,
    LoadOlder,
    ChooseZone,
    LocalZone,
    Save,
    CancelEdit,
    OpenPatreon,
}

internal static class PeopleMetrics
{
    public const float Pad = 12f;
    public const float SplitGap = 12f;
    // Below this a window stacks the list and the person instead of putting them side by side.
    public const float SplitMinWidth = 760f;
    public const float ListMinWidth = 270f;
    public const float ListMaxWidth = 340f;
    public const float SearchHeight = 36f;

    public const float PersonRowHeight = 52f;
    public const float LinkRowHeight = 40f;
    public const float RowFace = 36f;
    public const float RowDot = 12f;
    public const float NoteHeight = 26f;

    public const float BannerHeight = 76f;
    public const float FaceSize = 74f;
    public const float FaceRing = 4f;
    public const float FaceTop = 38f;
    public const float FaceLeft = 18f;
    public const float NameLeft = FaceLeft + FaceSize + 14f;
    public const float ActionHeight = 30f;
    public const float SwitchSegment = 92f;
    public const float TitleHeight = 44f;
    public const float ComposeHeight = 44f;

    public const float PillGap = 8f;
    public const float ActionWidth = 104f;

    public const float WhereHeight = 76f;
    public const float BadgeHeight = 50f;
    public const float GroupHeight = 50f;
    public const float FactHeight = 30f;
    public const float AnswerHeight = 52f;
    public const float DayHeight = 28f;
    public const float ItemCardHeight = 86f;
    public const float ItemCardWidth = 300f;
    public const float InviteCardWidth = 320f;
    public const float InviteCardHeight = 214f;
    public const float InvitePictureHeight = 120f;
    public const float ActionsHeight = 40f;
    public const float FieldHeight = 68f;
    public const float BioHeight = 168f;
    public const float BioEditHeight = 372f;
    public const float LinksLine = 38f;
    public const float SegmentHeight = 68f;
    public const float SegmentHintHeight = 88f;
    public const float PictureHeight = 96f;
    public const float TileHeight = 150f;
    public const float TileMinWidth = 132f;
    public const float TileGap = 10f;
    public const int TileMaxColumns = 6;

    public const float BubbleFont = DashTheme.FontBody;
    public const float BubblePadX = 12f;
    public const float BubblePadY = 8f;
    public const float BubbleMetaHeight = 14f;
    public const float BubbleShare = 0.74f;
    public const float GlyphWidth = 0.46f;
    public const float LineHeight = 1.2f;
    public const float TypingHeight = 30f;
}

internal sealed class PeopleNoteItem : ListingItem
{
    public color Tint = DashTheme.TextMuted;
    public bool Centered;
    public float Height = PeopleMetrics.NoteHeight;
    public bool Wrap;

    public PeopleNoteItem(string key, LocaleText text) : base(key)
    {
        LabelText = text;
        Kind = PeopleScreen.KindNote;
        Interactable = false;
    }
}

// A person as a list row: a friend, yourself, a search hit.
internal sealed class PeoplePersonItem : ListingItem
{
    public string UserId = string.Empty;
    public string Name = string.Empty;
    public color NameTint = DashTheme.Text;
    public string? AvatarHash;
    // the face plays when it is animated (PublicUserInfo.AnimatedPictures)
    public bool Animated;
    // null: nothing to say about presence, so no dot
    public string? Mode;
    public string Line = string.Empty;
    public int Unread;
    public string Chip = string.Empty;
    public color ChipTint = DashTheme.Accent;
    public PeopleAction Action = PeopleAction.OpenPerson;
    public Func<string, bool>? IsShown;

    public PeoplePersonItem(string key) : base(key)
    {
        Kind = PeopleScreen.KindPerson;
    }
}

// A row that opens a view: the requests, the blocked accounts.
internal sealed class PeopleLinkItem : ListingItem
{
    public string Count = string.Empty;
    public bool Lit;
    public PeopleAction Action;

    public PeopleLinkItem(string key, LocaleText label) : base(key)
    {
        LabelText = label;
        Kind = PeopleScreen.KindLink;
    }
}

internal sealed class PeopleWhereItem : ListingItem
{
    public string Title = string.Empty;
    public string Line = string.Empty;
    public string Initial = "?";
    public color Tint = DashTheme.Surface;
    public bool InWorld;
    public bool CanJoin;

    public PeopleWhereItem(string key) : base(key)
    {
        Kind = PeopleScreen.KindWhere;
    }
}

internal sealed class PeopleProseItem : ListingItem
{
    public string Text = string.Empty;
    public float Height = 40f;
    public bool Muted;
    // Text is rich text already escaped (a rendered bio); otherwise it shows as written.
    public bool Rich;

    public PeopleProseItem(string key) : base(key)
    {
        Kind = PeopleScreen.KindProse;
        Interactable = false;
    }
}

internal sealed class PeopleBadgeItem : ListingItem
{
    public string Name = string.Empty;
    public string Description = string.Empty;
    public string? IconHash;
    public color Tint = DashTheme.Accent;

    public PeopleBadgeItem(string key) : base(key)
    {
        Kind = PeopleScreen.KindBadge;
        Interactable = false;
    }
}

internal sealed class PeopleGroupItem : ListingItem
{
    public string GroupId = string.Empty;
    public string Name = string.Empty;
    public string GroupTag = string.Empty;
    public string? IconHash;
    public color Tint = DashTheme.Accent;
    public string Role = string.Empty;
    public bool Represented;

    public PeopleGroupItem(string key) : base(key)
    {
        Kind = PeopleScreen.KindGroup;
    }
}

internal sealed class PeopleFactItem : ListingItem
{
    public string Value = string.Empty;

    public PeopleFactItem(string key, LocaleText label, string value) : base(key)
    {
        LabelText = label;
        Value = value;
        Kind = PeopleScreen.KindFact;
        Interactable = false;
    }
}

// Somebody with up to two answers beside their name: a request, a block.
internal sealed class PeopleAnswerItem : ListingItem
{
    public string UserId = string.Empty;
    public string RequestId = string.Empty;
    public string Name = string.Empty;
    public string? AvatarHash;
    public bool Animated;
    public color NameTint = DashTheme.Text;
    public string Meta = string.Empty;
    public string PrimaryLabel = string.Empty;
    public PeopleAction Primary;
    public bool PrimaryLit;
    public string SecondaryLabel = string.Empty;
    public PeopleAction Secondary;
    // a tag ahead of the meta line: Bot on a request from a bot
    public string Badge = string.Empty;

    public PeopleAnswerItem(string key) : base(key)
    {
        Kind = PeopleScreen.KindAnswer;
    }
}

internal sealed class PeopleDayItem : ListingItem
{
    public PeopleDayItem(string key, string label) : base(key)
    {
        LabelText = label;
        Kind = PeopleScreen.KindDay;
        Interactable = false;
    }
}

internal sealed class PeopleMessageItem : ListingItem
{
    public ChatMessageInfo Message = new();
    public bool Mine;
    public string Text = string.Empty;
    public string Meta = string.Empty;
    public float Height = 40f;
    public float BubbleWidth = 120f;
    // ours, still on its way to the service / turned back by it (pressing the bubble sends it again)
    public bool Sending;
    public bool Failed;

    public PeopleMessageItem(string key) : base(key)
    {
        Kind = PeopleScreen.KindMessage;
    }
}

internal sealed class PeopleItemMessageItem : ListingItem
{
    public ChatMessageInfo Message = new();
    public bool Mine;
    public string Meta = string.Empty;
    public bool Expired;
    public bool IsWorld;
    // kept into the inventory from here this run
    public bool Kept;
    public bool Sending;
    public bool Failed;

    public PeopleItemMessageItem(string key) : base(key)
    {
        Kind = PeopleScreen.KindItemMessage;
    }
}

internal enum InviteCardState
{
    // nothing heard yet about whether the session runs; shown as live until told otherwise
    Live,
    Ended,
    Expired,
}

// An invite to a session: its world's picture, who hosts it, how full it is, and Join while it stands.
internal sealed class PeopleInviteMessageItem : ListingItem
{
    public ChatMessageInfo Message = new();
    public bool Mine;
    public string FromName = string.Empty;
    public string Meta = string.Empty;
    public InviteCardState State;
    // how full it is now, when the service said; otherwise as it was when the invite went
    public int Users;
    public int MaxUsers;
    // already open on this machine, so Join goes there instead
    public bool Here;

    public PeopleInviteMessageItem(string key) : base(key)
    {
        Kind = PeopleScreen.KindInviteMessage;
    }
}

// Their dots at the foot of the chat while they are typing.
internal sealed class PeopleTypingItem : ListingItem
{
    public PeopleTypingItem(string key) : base(key)
    {
        Kind = PeopleScreen.KindTyping;
        Interactable = false;
    }
}

internal sealed class PeopleActionsItem : ListingItem
{
    internal readonly struct Entry
    {
        public readonly string Label;
        public readonly PeopleAction Action;
        public readonly bool Lit;
        public readonly bool Danger;
        public readonly float Width;

        public Entry(string label, PeopleAction action, bool lit, bool danger, float width)
        {
            Label = label;
            Action = action;
            Lit = lit;
            Danger = danger;
            Width = width;
        }
    }

    public readonly List<Entry> Buttons = new();

    public PeopleActionsItem(string key) : base(key)
    {
        Kind = PeopleScreen.KindActions;
    }

    public PeopleActionsItem Add(string label, PeopleAction action, bool lit = false, bool danger = false,
        float width = PeopleMetrics.ActionWidth)
    {
        Buttons.Add(new Entry(label, action, lit, danger, width));
        return this;
    }
}

// A typed value on the edit page. The row is pooled, so it writes through the item, never to itself.
internal sealed class PeopleFieldItem : ListingItem
{
    public string Placeholder = string.Empty;
    public int MaxLength = 64;
    public bool Multiline;
    public Func<string>? Read;
    public Action<string>? Write;

    public PeopleFieldItem(string key, LocaleText label) : base(key)
    {
        LabelText = label;
        Kind = PeopleScreen.KindField;
    }
}

internal sealed class PeopleSegmentItem : ListingItem
{
    public string[] Options = Array.Empty<string>();
    public Func<int>? Read;
    public Action<int>? Write;
    public string Hint = string.Empty;

    public PeopleSegmentItem(string key, LocaleText label) : base(key)
    {
        LabelText = label;
        Kind = PeopleScreen.KindSegment;
    }
}

internal sealed class PeoplePictureItem : ListingItem
{
    public bool Banner;
    public string? Hash;
    public string Name = string.Empty;
    public color Tint = DashTheme.Accent;
    public string Status = string.Empty;
    // the preview plays an animated picture
    public bool Animate;
    // said instead of Status once the picture turns out animated, empty for nothing to say
    public string AnimatedStatus = string.Empty;

    public PeoplePictureItem(string key, LocaleText label) : base(key)
    {
        LabelText = label;
        Kind = PeopleScreen.KindPicture;
    }
}

internal sealed class PeopleLanguagesItem : ListingItem
{
    public readonly List<string> Codes = new();
    public string Hint = string.Empty;

    public PeopleLanguagesItem(string key, LocaleText label) : base(key)
    {
        LabelText = label;
        Kind = PeopleScreen.KindLanguages;
    }
}

// The addresses a bio links to, one pill each, laid out over Width (the body's, when the page was built).
internal sealed class PeopleLinksItem : ListingItem
{
    public readonly List<Uri> Links = new();
    public float Width = 480f;

    public PeopleLinksItem(string key) : base(key)
    {
        Kind = PeopleScreen.KindLinks;
    }
}

// The bio on the edit page: the markdown as typed, and how it reads.
internal sealed class PeopleBioEditItem : ListingItem
{
    public Func<string>? Read;
    public Action<string>? Write;

    public PeopleBioEditItem(string key, LocaleText label) : base(key)
    {
        LabelText = label;
        Kind = PeopleScreen.KindBioEdit;
    }
}

// Pronouns on the edit page: typed, or picked from the common sets with the pill.
internal sealed class PeoplePronounsItem : ListingItem
{
    public Func<string>? Read;
    public Action<string>? Write;

    public PeoplePronounsItem(string key, LocaleText label) : base(key)
    {
        LabelText = label;
        Kind = PeopleScreen.KindPronouns;
    }
}

internal sealed class PeopleChoiceItem : ListingItem
{
    public string Value = string.Empty;
    public string PrimaryLabel = string.Empty;
    public PeopleAction Primary;
    public string SecondaryLabel = string.Empty;
    public PeopleAction Secondary;

    public PeopleChoiceItem(string key, LocaleText label) : base(key)
    {
        LabelText = label;
        Kind = PeopleScreen.KindChoice;
    }
}
