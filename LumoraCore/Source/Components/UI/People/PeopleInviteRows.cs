// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// An invite in a chat: the world's picture across the top with its name on it and who may come, then who sent
// it and who hosts, how full it is, and Join beside Copy link. The host's name opens their profile. Once the
// session ends, or the invite runs out, the picture dims, the line under it says which, and Join stands down;
// the card stays where it was in the chat. Leans like any message, ours right in the accent. Held anywhere off
// its pills, or through its "...", it asks what to do with it like the others. -xlinka
internal sealed class PeopleInviteTemplate : ListingRowTemplate
{
    private const float Inset = 6f;
    private const float Pad = 10f;
    private const float PicturePad = 6f;
    private const float JoinWidth = 84f;
    private const float CopyWidth = 100f;
    private const float PillHeight = 28f;
    private const float MoreTop = 20f;

    private readonly SettingsFonts _fonts;
    private readonly PeopleRelay _relay;
    private readonly PeoplePictureCache _pictures;
    private readonly IAssetProvider<TextureAsset>? _moreIcon;

    public PeopleInviteTemplate(SettingsFonts fonts, PeopleRelay relay, PeoplePictureCache pictures, IAssetProvider<TextureAsset>? moreIcon)
    {
        _fonts = fonts;
        _relay = relay;
        _pictures = pictures;
        _moreIcon = moreIcon;
    }

    public override float Height => PeopleMetrics.InviteCardHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var card = SettingsUI.Child(row, "Card", float2.Zero, new float2(0f, 1f), new float2(Inset, 0f),
            new float2(Inset + PeopleMetrics.InviteCardWidth, 0f));
        var panel = SettingsUI.Panel(card, PeopleChatPaint.Theirs, DashTheme.Outline, DashTheme.RadiusCard);
        // under everything pressable on the card, which is built after it; this one only holds
        var hold = card.AttachComponent<Button>();
        hold.ActOnRelease.Value = true;

        float pictureBottom = PicturePad + PeopleMetrics.InvitePictureHeight;
        var pictureHost = SettingsUI.Child(card, "Picture", new float2(0f, 1f), new float2(1f, 1f),
            new float2(PicturePad, -pictureBottom), new float2(-PicturePad, -PicturePad));
        var picture = PeopleBanner.Build(pictureHost, DashTheme.RadiusCard - PicturePad * 0.5f);
        var shadeSlot = SettingsUI.Child(pictureHost, "Shade", float2.Zero, new float2(1f, 0.62f), float2.Zero, float2.Zero);
        var shade = shadeSlot.AttachComponent<GradientPanel>();
        var clear = new color(0f, 0f, 0f, 0f);
        var dark = new color(0f, 0f, 0f, 0.72f);
        shade.TopLeft.Value = clear;
        shade.TopRight.Value = clear;
        shade.BottomLeft.Value = dark;
        shade.BottomRight.Value = dark;
        var dimSlot = SettingsUI.Fill(pictureHost, "Dim");
        SettingsUI.Panel(dimSlot, new color(0.02f, 0.02f, 0.03f, 0.72f), color.Transparent, 0f);
        var world = SettingsUI.Label(pictureHost, "World", _fonts.Strong, DashTheme.FontHeading, color.White,
            TextHorizontalAlignment.Left, float2.Zero, new float2(1f, 0f), new float2(12f, 8f), new float2(-12f, 36f), shrinkToFit: true);
        // a dark plate, since it sits on whatever the picture is
        var accessSlot = SettingsUI.Child(pictureHost, "Access", new float2(0f, 1f), new float2(0f, 1f), new float2(8f, -28f), new float2(68f, -8f));
        SettingsUI.Panel(accessSlot, new color(0.02f, 0.02f, 0.04f, 0.66f), color.Transparent, DashTheme.RadiusChip);
        var access = SettingsUI.FillLabel(accessSlot, "Text", _fonts.Medium, DashTheme.FontLabel, color.White, TextHorizontalAlignment.Center);
        access.Ellipsis.Value = false;

        var from = SettingsUI.Label(card, "From", _fonts.Body, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(Pad, -(pictureBottom + 26f)), new float2(-Pad, -(pictureBottom + 8f)));
        var host = SettingsUI.Label(card, "Host", _fonts.Medium, DashTheme.FontBody, DashTheme.Text, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(Pad, -(pictureBottom + 48f)), new float2(-(Pad + 96f), -(pictureBottom + 28f)));
        var hostLink = ProfileLink.On(host);
        var users = SettingsUI.Label(card, "Users", _fonts.Body, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Right,
            new float2(0f, 1f), new float2(1f, 1f), new float2(Pad, -(pictureBottom + 48f)), new float2(-Pad, -(pictureBottom + 28f)));
        var meta = SettingsUI.Label(card, "Meta", _fonts.Body, DashTheme.FontLabel - 1f, DashTheme.TextMuted, TextHorizontalAlignment.Left,
            float2.Zero, new float2(1f, 0f), new float2(Pad, 8f), new float2(-(Pad + JoinWidth + CopyWidth + 12f), 22f));

        var join = SettingsChip.Build(SettingsUI.Child(card, "Join", new float2(1f, 0f), new float2(1f, 0f),
            new float2(-(Pad + JoinWidth), 8f), new float2(-Pad, 8f + PillHeight)), _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        var copy = SettingsChip.Build(SettingsUI.Child(card, "Copy", new float2(1f, 0f), new float2(1f, 0f),
            new float2(-(Pad + JoinWidth + 6f + CopyWidth), 8f), new float2(-(Pad + JoinWidth + 6f), 8f + PillHeight)),
            _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        var more = PeopleMessageMore.Build(row, _moreIcon, hold);

        var inviteRow = new InviteRow
        {
            Card = SettingsUI.Rect(card), Panel = panel, Picture = picture, Dim = dimSlot, World = world, Access = access,
            AccessRect = SettingsUI.Rect(accessSlot),
            From = from, Host = host, HostLink = hostLink, Users = users, Meta = meta, Join = join, Copy = copy,
            Pictures = _pictures, More = more,
        };
        _relay.Register(join.Button, inviteRow, 0);
        _relay.Register(copy.Button, inviteRow, 1);
        _relay.RegisterHold(hold, inviteRow);
        _relay.Register(more.Button, inviteRow, 2);
        return inviteRow;
    }

    private sealed class InviteRow : ListingRow
    {
        public required RectTransform Card;
        public required RoundedPanel Panel;
        public required PeopleBanner Picture;
        public required Slot Dim;
        public required Text World;
        public required Text Access;
        public required RectTransform AccessRect;
        public required Text From;
        public required Text Host;
        public required ProfileLink HostLink;
        public required Text Users;
        public required Text Meta;
        public required SettingsChip Join;
        public required SettingsChip Copy;
        public required PeoplePictureCache Pictures;
        public required PeopleMessageMore More;

        public override void Unbind() => More.Reset();

        public override void Bind(ListingItem item)
        {
            if (item is not PeopleInviteMessageItem card || card.Message.Invite is not { } invite)
                return;
            float width = PeopleMetrics.InviteCardWidth;
            if (card.Mine)
            {
                SettingsUI.SetAnchors(Card, new float2(1f, 0f), new float2(1f, 1f));
                SettingsUI.SetOffsets(Card, new float2(-(Inset + width), 0f), new float2(-Inset, 0f));
            }
            else
            {
                SettingsUI.SetAnchors(Card, float2.Zero, new float2(0f, 1f));
                SettingsUI.SetOffsets(Card, new float2(Inset, 0f), new float2(Inset + width, 0f));
            }
            More.Place(card.Mine, Inset, width, MoreTop, PeopleLocale.ChatMore.Resolve());
            SettingsUI.SetPaint(Panel, card.Mine ? PeopleChatPaint.Mine : PeopleChatPaint.Theirs,
                card.Mine ? color.Transparent : DashTheme.Outline);

            bool live = card.State == InviteCardState.Live;
            string name = invite.WorldName.Length > 0 ? invite.WorldName : "...";
            Picture.Bind(Pictures, invite.ThumbnailHash, PeopleScreen.WorldTint(name));
            PeopleShow.Set(Dim, !live);
            ListingStyle.SetText(World, name);
            ListingStyle.SetTextColor(World, live ? color.White : new color(1f, 1f, 1f, 0.6f));
            string level = InviteLocale.Access(invite.AccessLevel).Resolve();
            ListingStyle.SetText(Access, level);
            ListingStyle.SetTextColor(Access, live ? color.White : new color(1f, 1f, 1f, 0.6f));
            SettingsUI.SetOffsets(AccessRect, new float2(8f, -28f), new float2(8f + PeopleTag.Width(level), -8f));

            ListingStyle.SetText(From, card.Mine ? InviteLocale.CardMine.Resolve() : InviteLocale.CardFrom(card.FromName).Resolve());
            ListingStyle.SetText(Host, InviteLocale.CardHost(invite.HostName).Resolve());
            HostLink.Bind(invite.HostId, DashTheme.Text);
            string state = card.State switch
            {
                InviteCardState.Ended => InviteLocale.CardEnded.Resolve(),
                InviteCardState.Expired => InviteLocale.CardExpired.Resolve(),
                _ => card.MaxUsers > 0 ? InviteLocale.CardHere(card.Users, card.MaxUsers).Resolve() : string.Empty,
            };
            // once the session is gone the state line takes the host's place, reading from the left
            PeopleShow.Set(Host.Slot, live);
            ListingStyle.SetText(Users, state);
            ListingStyle.SetTextColor(Users, live ? DashTheme.TextDim : DashTheme.Warning);
            var align = live ? TextHorizontalAlignment.Right : TextHorizontalAlignment.Left;
            if (Users.HorizontalAlignment.Value != align)
                Users.HorizontalAlignment.Value = align;

            ListingStyle.SetText(Meta, card.Meta);
            ListingStyle.SetTextColor(Meta, PeopleChatPaint.MetaTint(card.Mine, false, false));
            string joinLabel = !live ? InviteLocale.CardEndedPill.Resolve() : card.Here ? InviteLocale.CardOpen.Resolve() : InviteLocale.CardJoin.Resolve();
            GroupBand.PaintAction(Join, joinLabel, live, false, live);
            bool canCopy = live && invite.Addresses.Count > 0;
            PeopleShow.Set(Copy.Slot, canCopy);
            if (canCopy)
                GroupBand.PaintAction(Copy, InviteLocale.CardCopy.Resolve(), true, false, false);
        }
    }
}
