// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// The rows of the group list and of a group's pages. All of them are pooled: Bind paints what the item says
// and every write is equality-gated, so a rebind of an unchanged row costs nothing.

// ONE GROUP IN THE LIST
//
// Its mark, its name in its colour, one line of what it is to you, and on the right what it is (a chip) or,
// for an invite, the two answers. A press anywhere else opens it.
internal sealed class GroupRowTemplate : ListingRowTemplate
{
    private const float MarkLeft = 10f;
    private const float TextLeft = MarkLeft + GroupMetrics.RowMark + 12f;
    private const float ChipMax = 104f;
    private const float AnswerHeight = 26f;

    private readonly SettingsFonts _fonts;
    private readonly GroupsRelay _relay;
    private readonly PeoplePictureCache _pictures;

    public GroupRowTemplate(SettingsFonts fonts, GroupsRelay relay, PeoplePictureCache pictures)
    {
        _fonts = fonts;
        _relay = relay;
        _pictures = pictures;
    }

    public override float Height => GroupMetrics.RowHeight;
    // An invite carries its answers on a band of their own under the name, so the name keeps the row's width.
    public override float HeightFor(ListingItem item) => item is GroupRowItem { Invite: true } ? GroupMetrics.InviteRowHeight : Height;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var background = SettingsUI.Panel(row, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusControl);
        var open = row.AttachComponent<Button>();

        float half = GroupMetrics.RowMark * 0.5f;
        var markHost = SettingsUI.Child(row, "Mark", new float2(0f, 0.5f), new float2(0f, 0.5f),
            new float2(MarkLeft, -half), new float2(MarkLeft + GroupMetrics.RowMark, half));
        var mark = GroupMark.Build(markHost, _fonts.Strong, DashTheme.RadiusControl, DashTheme.FontSmall);

        var name = SettingsUI.Label(row, "Name", _fonts.Medium, DashTheme.FontBody, DashTheme.Text,
            TextHorizontalAlignment.Left, new float2(0f, 1f), new float2(1f, 1f), new float2(TextLeft, -29f), new float2(-12f, -8f));
        var line = SettingsUI.Label(row, "Line", _fonts.Body, DashTheme.FontSmall, DashTheme.TextMuted,
            TextHorizontalAlignment.Left, new float2(0f, 0f), new float2(1f, 0f), new float2(TextLeft, 8f), new float2(-12f, 27f), shrinkToFit: true);

        var chip = SettingsUI.Child(row, "Chip", new float2(1f, 0.5f), new float2(1f, 0.5f),
            new float2(-(10f + ChipMax), -11f), new float2(-10f, 11f));
        var chipPanel = SettingsUI.Panel(chip, DashTheme.Field, color.Transparent, DashTheme.RadiusChip);
        var chipText = SettingsUI.FillLabel(chip, "Text", _fonts.Medium, DashTheme.FontLabel, DashTheme.TextDim,
            TextHorizontalAlignment.Center, true);
        chip.ActiveSelf.Value = false;

        var decline = SettingsChip.Build(Answer(row, "Decline", 10f), _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        var accept = SettingsChip.Build(Answer(row, "Accept", 10f + GroupMetrics.InvitePill + 5f), _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        decline.Slot.ActiveSelf.Value = false;
        accept.Slot.ActiveSelf.Value = false;

        var listingRow = new GroupListRow
        {
            Background = background,
            Mark = mark,
            Name = name,
            NameRect = SettingsUI.Rect(name.Slot),
            Line = line,
            LineRect = SettingsUI.Rect(line.Slot),
            Chip = chip,
            ChipPanel = chipPanel,
            ChipText = chipText,
            Accept = accept,
            Decline = decline,
            Pictures = _pictures,
        };
        _relay.Register(open, listingRow, 0);
        _relay.Register(accept.Button, listingRow, 1);
        _relay.Register(decline.Button, listingRow, 2);
        return listingRow;
    }

    private static Slot Answer(Slot row, string name, float right)
        => SettingsUI.Child(row, name, new float2(1f, 0f), new float2(1f, 0f),
            new float2(-(right + GroupMetrics.InvitePill), 9f), new float2(-right, 9f + AnswerHeight));

    private sealed class GroupListRow : ListingRow
    {
        public required RoundedPanel Background;
        public required GroupMark Mark;
        public required Text Name;
        public required RectTransform NameRect;
        public required Text Line;
        public required RectTransform LineRect;
        public required Slot Chip;
        public required RoundedPanel ChipPanel;
        public required Text ChipText;
        public required SettingsChip Accept;
        public required SettingsChip Decline;
        public required PeoplePictureCache Pictures;

        public override void Bind(ListingItem item)
        {
            if (item is not GroupRowItem group)
                return;
            bool shown = group.IsShown?.Invoke(group.GroupId) ?? false;
            SettingsUI.SetPaint(Background, shown ? DashTheme.SurfaceHover : DashTheme.Surface,
                shown ? DashTheme.Accent.WithAlpha(0.55f) : group.Invite ? group.Tint.WithAlpha(0.45f) : DashTheme.Outline);

            Mark.Bind(Pictures, group.IconHash, group.GroupTag, group.Name, group.Tint);
            ListingStyle.SetText(Name, group.Name);
            ListingStyle.SetTextColor(Name, group.Ink);
            ListingStyle.SetText(Line, group.Line);

            PeopleShow.Set(Accept.Slot, group.Invite);
            PeopleShow.Set(Decline.Slot, group.Invite);
            if (group.Invite)
            {
                GroupBand.PaintAction(Accept, GroupsLocale.Accept.Resolve(), true, false, true);
                GroupBand.PaintAction(Decline, GroupsLocale.Decline.Resolve(), true, false, false);
            }

            bool chip = !group.Invite && group.Chip.Length > 0;
            PeopleShow.Set(Chip, chip);
            float chipWidth = System.Math.Clamp(PeopleTag.Width(group.Chip), 44f, ChipMax);
            if (chip)
            {
                ListingStyle.SetText(ChipText, group.Chip);
                SettingsUI.SetFill(ChipPanel, group.ChipTint.WithAlpha(0.2f));
                ListingStyle.SetTextColor(ChipText, Lift(group.ChipTint));
                SettingsUI.SetOffsets(SettingsUI.Rect(Chip), new float2(-(10f + chipWidth), -11f), new float2(-10f, 11f));
            }
            if (group.Invite)
            {
                SettingsUI.SetOffsets(NameRect, new float2(TextLeft, -31f), new float2(-12f, -10f));
                SettingsUI.SetOffsets(LineRect, new float2(TextLeft, 12f), new float2(-(10f + 2f * GroupMetrics.InvitePill + 5f + 6f), 31f));
                return;
            }
            float right = chip ? 10f + chipWidth + 8f : 12f;
            SettingsUI.SetOffsets(NameRect, new float2(TextLeft, -29f), new float2(-right, -8f));
            SettingsUI.SetOffsets(LineRect, new float2(TextLeft, 8f), new float2(-right, 27f));
        }

        private static color Lift(in color c)
            => new color(c.r + (1f - c.r) * 0.35f, c.g + (1f - c.g) * 0.35f, c.b + (1f - c.b) * 0.35f, 1f);
    }
}

// ONE MEMBER
//
// The face, the name (their profile) and since when, their role on the right. For an admin looking at
// someone below them the row grows: the escalations on the right of the name, the roles they may hand out
// under it, and the storage slice under that, as a slider that commits on release.
internal sealed class GroupMemberTemplate : ListingRowTemplate
{
    private const int MaxRoles = 4;
    private const float FaceLeft = 10f;
    private const float TextLeft = FaceLeft + GroupMetrics.MemberFace + 12f;
    private const float Right = 12f;
    private const float RoleChip = 92f;
    private const float UsedWidth = 108f;
    private const float ValueWidth = 80f;
    private const float PillHeight = 28f;
    private const float TopCentre = 27f;

    private readonly SettingsFonts _fonts;
    private readonly GroupsRelay _relay;
    private readonly GroupsScreen _screen;

    public GroupMemberTemplate(SettingsFonts fonts, GroupsRelay relay, GroupsScreen screen)
    {
        _fonts = fonts;
        _relay = relay;
        _screen = screen;
    }

    public static float HeightOf(GroupMemberItem member)
        => GroupMetrics.MemberHeight + (member.Roles.Count > 0 ? GroupMetrics.MemberBand : 0f)
            + (member.ShowStorage ? GroupMetrics.MemberBand + 4f : 0f);

    public override float Height => GroupMetrics.MemberHeight;
    public override float HeightFor(ListingItem item) => item is GroupMemberItem member ? HeightOf(member) : Height;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var background = SettingsUI.Panel(row, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusControl);
        float half = GroupMetrics.MemberFace * 0.5f;
        var faceHost = SettingsUI.Child(row, "Face", new float2(0f, 1f), new float2(0f, 1f),
            new float2(FaceLeft, -(TopCentre + half)), new float2(FaceLeft + GroupMetrics.MemberFace, -(TopCentre - half)));
        var face = PeopleAvatar.Build(faceHost, _fonts.Strong, GroupMetrics.MemberFace, 0f, 0f, DashTheme.FontBody);

        var name = SettingsUI.Label(row, "Name", _fonts.Medium, DashTheme.FontBody, DashTheme.Text, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(TextLeft, -28f), new float2(-Right, -8f));
        var line = SettingsUI.Label(row, "Line", _fonts.Body, DashTheme.FontSmall, DashTheme.TextMuted, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(TextLeft, -46f), new float2(-Right, -28f));

        var role = SettingsUI.Child(row, "Role", new float2(1f, 1f), new float2(1f, 1f),
            new float2(-(Right + RoleChip), -(TopCentre + 11f)), new float2(-Right, -(TopCentre - 11f)));
        var rolePanel = SettingsUI.Panel(role, DashTheme.Field, color.Transparent, DashTheme.RadiusChip);
        var roleText = SettingsUI.FillLabel(role, "Text", _fonts.Medium, DashTheme.FontLabel, DashTheme.TextDim,
            TextHorizontalAlignment.Center, true);

        var listingRow = new MemberRow
        {
            Background = background,
            Face = face,
            Name = name,
            NameRect = SettingsUI.Rect(name.Slot),
            Line = line,
            LineRect = SettingsUI.Rect(line.Slot),
            RoleSlot = role,
            RolePanel = rolePanel,
            RoleText = roleText,
            Link = ProfileLink.On(name),
        };
        listingRow.Link.Add(faceHost);

        listingRow.Kick = Pill(row, listingRow, "Kick", 10);
        listingRow.Ban = Pill(row, listingRow, "Ban", 11);
        listingRow.Owner = Pill(row, listingRow, "Owner", 12);

        var strip = SettingsUI.Child(row, "Roles", new float2(0f, 1f), new float2(1f, 1f),
            new float2(TextLeft, -(GroupMetrics.MemberHeight + 3f + PillHeight)), new float2(-Right, -(GroupMetrics.MemberHeight + 3f)));
        listingRow.Strip = strip;
        for (int i = 0; i < MaxRoles; i++)
        {
            var host = SettingsUI.Child(strip, "Role" + i, new float2(0f, 0f), new float2(0f, 1f),
                new float2(i * (GroupMetrics.RolePill + 6f), 0f), new float2(i * (GroupMetrics.RolePill + 6f) + GroupMetrics.RolePill, 0f));
            var chip = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
            host.ActiveSelf.Value = false;
            _relay.Register(chip.Button, listingRow, i);
            listingRow.RoleChips.Add(chip);
        }
        strip.ActiveSelf.Value = false;

        var storage = SettingsUI.Child(row, "Storage", new float2(0f, 0f), new float2(1f, 0f),
            new float2(TextLeft, 8f), new float2(-Right, 8f + PillHeight));
        listingRow.Storage = storage;
        listingRow.Used = SettingsUI.Label(storage, "Used", _fonts.Body, DashTheme.FontLabel, DashTheme.TextMuted, TextHorizontalAlignment.Left,
            new float2(0f, 0f), new float2(0f, 1f), float2.Zero, new float2(UsedWidth, 0f), shrinkToFit: true);
        var sliderHost = SettingsUI.Child(storage, "Slice", new float2(0f, 0.5f), new float2(1f, 0.5f),
            new float2(UsedWidth + 8f, -12f), new float2(-(ValueWidth + 8f), 12f));
        builder.PushStyle();
        builder.ForegroundColor(DashTheme.Accent);
        builder.NestInto(sliderHost);
        var slider = builder.Slider(0f, 0f, 1f, null, DashTheme.Field);
        builder.NestOut();
        builder.PopStyle();
        var sliderRect = SettingsUI.Rect(slider.Slot);
        SettingsUI.SetAnchors(sliderRect, float2.Zero, float2.One);
        SettingsUI.SetOffsets(sliderRect, float2.Zero, float2.Zero);
        // A slider's value event is a plain C# event, so the handler closes over the ROW and reads whichever
        // item is bound to it at fire time. The drag only moves the number; the call waits for the release,
        // so dragging across the whole track is one request rather than one per sample. -xlinka
        var screen = _screen;
        slider.ValueChanged += (_, raw) =>
        {
            if (listingRow.Item is GroupMemberItem member && member.ShowStorage)
                screen.PreviewStorage(member.UserId, raw);
        };
        slider.Released += _ =>
        {
            if (listingRow.Item is GroupMemberItem member && member.ShowStorage)
                screen.CommitStorage(member.UserId, slider.Value.Value);
        };
        listingRow.Bar = slider;
        listingRow.Value = SettingsUI.Label(storage, "SliceValue", _fonts.Medium, DashTheme.FontSmall, DashTheme.TextDim,
            TextHorizontalAlignment.Right, new float2(1f, 0f), new float2(1f, 1f), new float2(-ValueWidth, 0f), float2.Zero);
        storage.ActiveSelf.Value = false;
        return listingRow;
    }

    private SettingsChip Pill(Slot row, ListingRow listingRow, string name, int index)
    {
        var host = SettingsUI.Child(row, name, new float2(1f, 1f), new float2(1f, 1f),
            new float2(-(Right + GroupMetrics.MemberPill), -(TopCentre + PillHeight * 0.5f)), new float2(-Right, -(TopCentre - PillHeight * 0.5f)));
        var chip = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        _relay.Register(chip.Button, listingRow, index);
        host.ActiveSelf.Value = false;
        return chip;
    }

    private sealed class MemberRow : ListingRow
    {
        public required RoundedPanel Background;
        public required PeopleAvatar Face;
        public required Text Name;
        public required RectTransform NameRect;
        public required Text Line;
        public required RectTransform LineRect;
        public required Slot RoleSlot;
        public required RoundedPanel RolePanel;
        public required Text RoleText;
        public required ProfileLink Link;

        public readonly List<SettingsChip> RoleChips = new();
        public Slot Strip = null!;
        public SettingsChip Kick = null!;
        public SettingsChip Ban = null!;
        public SettingsChip Owner = null!;
        public Slot Storage = null!;
        public Text Used = null!;
        public Slider Bar = null!;
        public Text Value = null!;

        public override void Bind(ListingItem item)
        {
            SettingsUI.SetPaint(Background, DashTheme.Surface, DashTheme.Outline);
            if (item is not GroupMemberItem member)
                return;

            Face.Bind(null, null, member.Name, GroupsFormat.PersonTint(member.Name), null);
            ListingStyle.SetText(Name, member.IsSelf ? member.Name + "  (" + GroupsLocale.You.Resolve() + ")" : member.Name);
            Link.Bind(member.UserId, DashTheme.Text);
            ListingStyle.SetText(Line, member.Line);

            // the escalations pack in from the right edge, in reading order
            float right = Right;
            right = Place(Owner, member.ShowMakeOwner, GroupsLocale.MakeOwner.Resolve(), GroupMetrics.OwnerPill, right, false);
            right = Place(Ban, member.ShowBan, GroupsLocale.Ban.Resolve(), GroupMetrics.MemberPill, right, true);
            right = Place(Kick, member.ShowKick, GroupsLocale.Kick.Resolve(), GroupMetrics.MemberPill, right, true);

            bool pills = member.Roles.Count > 0;
            bool chip = !pills && member.Role.Length > 0;
            PeopleShow.Set(RoleSlot, chip);
            if (chip)
            {
                var tint = GroupsFormat.RoleTint(member.Role);
                float width = System.Math.Clamp(PeopleTag.Width(member.Role), 56f, RoleChip);
                SettingsUI.SetOffsets(SettingsUI.Rect(RoleSlot), new float2(-(right + width), -(TopCentre + 11f)), new float2(-right, -(TopCentre - 11f)));
                SettingsUI.SetFill(RolePanel, tint.WithAlpha(0.2f));
                ListingStyle.SetText(RoleText, member.Role);
                ListingStyle.SetTextColor(RoleText, new color(tint.r + (1f - tint.r) * 0.3f, tint.g + (1f - tint.g) * 0.3f, tint.b + (1f - tint.b) * 0.3f, 1f));
                right += width + 8f;
            }
            else if (right > Right)
            {
                right += 2f;
            }
            SettingsUI.SetOffsets(NameRect, new float2(TextLeft, -28f), new float2(-right, -8f));
            SettingsUI.SetOffsets(LineRect, new float2(TextLeft, -46f), new float2(-right, -28f));

            PeopleShow.Set(Strip, pills);
            for (int i = 0; i < RoleChips.Count; i++)
            {
                bool used = pills && i < member.Roles.Count;
                PeopleShow.Set(RoleChips[i].Slot, used);
                if (!used)
                    continue;
                bool lit = string.Equals(member.Roles[i], member.Role, System.StringComparison.Ordinal);
                GroupBand.PaintAction(RoleChips[i], member.Roles[i], true, false, lit);
            }

            PeopleShow.Set(Storage, member.ShowStorage);
            if (!member.ShowStorage)
                return;
            long pending = member.PendingSlice?.Invoke(member.UserId) ?? member.Allocated;
            float max = System.Math.Max(1f, member.SliderMax);
            ListingStyle.SetSlider(Bar, 0f, max, System.Math.Clamp(pending, 0L, member.SliderMax));
            ListingStyle.SetText(Value, GroupsFormat.Bytes(pending));
            ListingStyle.SetText(Used, GroupsLocale.Used(GroupsFormat.Bytes(member.UsedBytes)).Resolve());
        }

        private static float Place(SettingsChip chip, bool on, string label, float width, float right, bool danger)
        {
            PeopleShow.Set(chip.Slot, on);
            if (!on)
                return right;
            var rect = SettingsUI.Rect(chip.Slot);
            SettingsUI.SetOffsets(rect, new float2(-(right + width), -(TopCentre + PillHeight * 0.5f)), new float2(-right, -(TopCentre - PillHeight * 0.5f)));
            GroupBand.PaintAction(chip, label, true, danger, false);
            return right + width + 6f;
        }
    }
}

// A PERSON WITH ANSWERS: a join request, an invite out, a ban. The name opens their profile.
internal sealed class GroupPersonTemplate : ListingRowTemplate
{
    private const float FaceLeft = 10f;
    private const float TextLeft = FaceLeft + GroupMetrics.MemberFace + 12f;
    private const float PillWidth = 88f;
    private const float PillHeight = 28f;

    private readonly SettingsFonts _fonts;
    private readonly GroupsRelay _relay;

    public GroupPersonTemplate(SettingsFonts fonts, GroupsRelay relay)
    {
        _fonts = fonts;
        _relay = relay;
    }

    public override float Height => GroupMetrics.PersonHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var background = SettingsUI.Panel(row, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusControl);
        float half = GroupMetrics.MemberFace * 0.5f;
        var faceHost = SettingsUI.Child(row, "Face", new float2(0f, 0.5f), new float2(0f, 0.5f),
            new float2(FaceLeft, -half), new float2(FaceLeft + GroupMetrics.MemberFace, half));
        var face = PeopleAvatar.Build(faceHost, _fonts.Strong, GroupMetrics.MemberFace, 0f, 0f, DashTheme.FontBody);
        var name = SettingsUI.Label(row, "Name", _fonts.Medium, DashTheme.FontBody, DashTheme.Text, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(TextLeft, -27f), new float2(-12f, -7f));
        var note = SettingsUI.Label(row, "Note", _fonts.Body, DashTheme.FontSmall, DashTheme.TextMuted, TextHorizontalAlignment.Left,
            new float2(0f, 0f), new float2(1f, 0f), new float2(TextLeft, 7f), new float2(-12f, 25f));
        var listingRow = new PersonRow
        {
            Background = background, Face = face, Name = name, NameRect = SettingsUI.Rect(name.Slot),
            Note = note, NoteRect = SettingsUI.Rect(note.Slot), Link = ProfileLink.On(name),
        };
        listingRow.Link.Add(faceHost);
        listingRow.Secondary = Pill(row, listingRow, "Second", 12f + PillWidth + 6f, 2);
        listingRow.Primary = Pill(row, listingRow, "Primary", 12f, 1);
        return listingRow;
    }

    private SettingsChip Pill(Slot row, ListingRow listingRow, string name, float right, int index)
    {
        var chip = SettingsChip.Build(GroupBand.Right(row, name, right, PillWidth, PillHeight),
            _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        _relay.Register(chip.Button, listingRow, index);
        chip.Slot.ActiveSelf.Value = false;
        return chip;
    }

    private sealed class PersonRow : ListingRow
    {
        public required RoundedPanel Background;
        public required PeopleAvatar Face;
        public required Text Name;
        public required RectTransform NameRect;
        public required Text Note;
        public required RectTransform NoteRect;
        public required ProfileLink Link;
        public SettingsChip Primary = null!;
        public SettingsChip Secondary = null!;

        public override void Bind(ListingItem item)
        {
            SettingsUI.SetPaint(Background, DashTheme.Surface, DashTheme.Outline);
            if (item is not GroupPersonItem person)
                return;
            Face.Bind(null, null, person.Name, GroupsFormat.PersonTint(person.Name), null);
            ListingStyle.SetText(Name, person.Name);
            Link.Bind(person.UserId, DashTheme.Text);
            ListingStyle.SetText(Note, person.Note);
            PeopleShow.Set(Note.Slot, person.Note.Length > 0);

            bool primary = person.PrimaryLabel.Length > 0;
            bool secondary = person.SecondaryLabel.Length > 0;
            PeopleShow.Set(Primary.Slot, primary);
            if (primary)
                GroupBand.PaintAction(Primary, person.PrimaryLabel, true, person.PrimaryDanger, person.PrimaryLit);
            PeopleShow.Set(Secondary.Slot, secondary);
            if (secondary)
                GroupBand.PaintAction(Secondary, person.SecondaryLabel, true, person.SecondaryDanger, false);
            float right = 12f + (primary ? PillWidth + 8f : 0f) + (secondary ? PillWidth + 6f : 0f);
            SettingsUI.SetOffsets(NameRect, new float2(TextLeft, -27f), new float2(-right, -7f));
            SettingsUI.SetOffsets(NoteRect, new float2(TextLeft, 7f), new float2(-right, 25f));
        }
    }
}

// AN EVENT
//
// The day on a tile of the group's colour, what and where, and on the right what can be done about it: go
// to it while it is live, host it, take it down.
internal sealed class GroupEventTemplate : ListingRowTemplate
{
    private const float TileLeft = 10f;
    private const float TextLeft = TileLeft + GroupMetrics.DateTile + 12f;
    private const float DeleteWidth = 76f;
    private const float PillHeight = 28f;

    private readonly SettingsFonts _fonts;
    private readonly GroupsRelay _relay;

    public GroupEventTemplate(SettingsFonts fonts, GroupsRelay relay)
    {
        _fonts = fonts;
        _relay = relay;
    }

    public override float Height => GroupMetrics.EventHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var background = SettingsUI.Panel(row, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusControl);
        float half = GroupMetrics.DateTile * 0.5f;
        var tile = SettingsUI.Child(row, "Date", new float2(0f, 0.5f), new float2(0f, 0.5f),
            new float2(TileLeft, -half), new float2(TileLeft + GroupMetrics.DateTile, half));
        var tilePanel = SettingsUI.Panel(tile, DashTheme.Field, color.Transparent, DashTheme.RadiusControl);
        var month = SettingsUI.Label(tile, "Month", _fonts.Medium, DashTheme.FontLabel - 1f, DashTheme.TextDim, TextHorizontalAlignment.Center,
            new float2(0f, 1f), new float2(1f, 1f), new float2(0f, -17f), new float2(0f, -3f));
        var day = SettingsUI.Label(tile, "Day", _fonts.Strong, DashTheme.FontTitle, DashTheme.Text, TextHorizontalAlignment.Center,
            new float2(0f, 0f), new float2(1f, 1f), new float2(0f, 2f), new float2(0f, -15f));

        var title = SettingsUI.Label(row, "Title", _fonts.Medium, DashTheme.FontBody, DashTheme.Text, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(TextLeft, -31f), new float2(-12f, -10f));
        var meta = SettingsUI.Label(row, "Meta", _fonts.Body, DashTheme.FontSmall, DashTheme.TextMuted, TextHorizontalAlignment.Left,
            new float2(0f, 0f), new float2(1f, 0f), new float2(TextLeft, 10f), new float2(-12f, 29f));

        var listingRow = new EventRow
        {
            Background = background, TilePanel = tilePanel, Month = month, Day = day, Title = title,
            TitleRect = SettingsUI.Rect(title.Slot), Meta = meta, MetaRect = SettingsUI.Rect(meta.Slot),
        };
        listingRow.Delete = Pill(row, listingRow, "Delete", 1);
        listingRow.Host = Pill(row, listingRow, "Host", 2);
        listingRow.Live = Pill(row, listingRow, "Live", 3);
        return listingRow;
    }

    private SettingsChip Pill(Slot row, ListingRow listingRow, string name, int index)
    {
        var chip = SettingsChip.Build(GroupBand.Right(row, name, 12f, GroupMetrics.EventPill, PillHeight),
            _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        _relay.Register(chip.Button, listingRow, index);
        chip.Slot.ActiveSelf.Value = false;
        return chip;
    }

    private sealed class EventRow : ListingRow
    {
        public required RoundedPanel Background;
        public required RoundedPanel TilePanel;
        public required Text Month;
        public required Text Day;
        public required Text Title;
        public required RectTransform TitleRect;
        public required Text Meta;
        public required RectTransform MetaRect;
        public SettingsChip Delete = null!;
        public SettingsChip Host = null!;
        public SettingsChip Live = null!;

        public override void Bind(ListingItem item)
        {
            if (item is not GroupEventItem entry)
                return;
            SettingsUI.SetPaint(Background, DashTheme.Surface, entry.IsLive ? DashTheme.Positive.WithAlpha(0.5f) : DashTheme.Outline);
            var tint = entry.Tint;
            SettingsUI.SetFill(TilePanel, new color(tint.r * 0.4f, tint.g * 0.4f, tint.b * 0.4f, 1f));
            ListingStyle.SetText(Month, entry.Month);
            ListingStyle.SetText(Day, entry.Day);
            ListingStyle.SetText(Title, entry.Title);
            ListingStyle.SetText(Meta, entry.Meta);

            float right = 12f;
            right = Place(Delete, entry.CanDelete, GroupsLocale.Delete.Resolve(), DeleteWidth, right, true, false);
            right = Place(Host, entry.CanHost, GroupsLocale.HostEventWorld.Resolve(), GroupMetrics.EventPill, right, false, false);
            // Lit, because a session that is up right now is the one thing on this row worth pressing.
            right = Place(Live, entry.IsLive, GroupsLocale.EventLive.Resolve(), GroupMetrics.EventPill, right, false, true);
            if (right > 12f)
                right += 4f;
            SettingsUI.SetOffsets(TitleRect, new float2(TextLeft, -31f), new float2(-right, -10f));
            SettingsUI.SetOffsets(MetaRect, new float2(TextLeft, 10f), new float2(-right, 29f));
        }

        private static float Place(SettingsChip chip, bool on, string label, float width, float right, bool danger, bool lit)
        {
            PeopleShow.Set(chip.Slot, on);
            if (!on)
                return right;
            SettingsUI.SetOffsets(SettingsUI.Rect(chip.Slot), new float2(-(right + width), -PillHeight * 0.5f), new float2(-right, PillHeight * 0.5f));
            GroupBand.PaintAction(chip, label, true, danger, lit);
            return right + width + 6f;
        }
    }
}

// AN ANNOUNCEMENT: who and when, the whole text wrapped under it, and Delete for whoever may.
internal sealed class GroupPostTemplate : ListingRowTemplate
{
    private const float DeleteWidth = 76f;

    private readonly SettingsFonts _fonts;
    private readonly GroupsRelay _relay;

    public GroupPostTemplate(SettingsFonts fonts, GroupsRelay relay)
    {
        _fonts = fonts;
        _relay = relay;
    }

    public override float Height => 72f;
    public override float HeightFor(ListingItem item) => item is GroupPostItem post ? post.Height : Height;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var background = SettingsUI.Panel(row, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusCard);
        var meta = SettingsUI.Label(row, "Meta", _fonts.Medium, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(14f, -30f), new float2(-(14f + DeleteWidth + 8f), -10f));
        var text = SettingsUI.Label(row, "Text", _fonts.Body, DashTheme.FontBody, DashTheme.Text, TextHorizontalAlignment.Left,
            float2.Zero, float2.One, new float2(14f, 10f), new float2(-14f, -38f));
        text.WordWrap.Value = true;
        text.Ellipsis.Value = false;
        text.RichText.Value = false;
        text.VerticalAlignment.Value = TextVerticalAlignment.Top;
        var host = SettingsUI.Child(row, "Delete", new float2(1f, 1f), new float2(1f, 1f),
            new float2(-(12f + DeleteWidth), -34f), new float2(-12f, -8f));
        var delete = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        host.ActiveSelf.Value = false;
        var listingRow = new PostRow { Meta = meta, Text = text, Delete = delete };
        _relay.Register(delete.Button, listingRow, 1);
        SettingsUI.SetPaint(background, DashTheme.Surface, DashTheme.Outline);
        return listingRow;
    }

    private sealed class PostRow : ListingRow
    {
        public required Text Meta;
        public required Text Text;
        public required SettingsChip Delete;

        public override void Bind(ListingItem item)
        {
            if (item is not GroupPostItem post)
                return;
            ListingStyle.SetText(Meta, post.Meta);
            ListingStyle.SetText(Text, post.Text);
            PeopleShow.Set(Delete.Slot, post.CanDelete);
            if (post.CanDelete)
                GroupBand.PaintAction(Delete, GroupsLocale.Delete.Resolve(), true, true, false);
        }
    }
}
