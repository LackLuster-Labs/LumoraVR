// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// The rows of the people list and of a profile. All of them are pooled: Bind paints what the item says and
// every write is equality-gated, so a rebind of an unchanged row costs nothing.

internal sealed class PeopleNoteTemplate : ListingRowTemplate
{
    private readonly SettingsFonts _fonts;

    public PeopleNoteTemplate(SettingsFonts fonts) => _fonts = fonts;

    public override float Height => PeopleMetrics.NoteHeight;
    public override float HeightFor(ListingItem item) => item is PeopleNoteItem note ? note.Height : Height;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var text = SettingsUI.Label(row, "Note", _fonts.Body, DashTheme.FontSmall, DashTheme.TextMuted,
            TextHorizontalAlignment.Left, float2.Zero, float2.One,
            new float2(SettingsMetrics.RowInset, 0f), new float2(-SettingsMetrics.RowInset, 0f));
        return new NoteRow { Text = text };
    }

    private sealed class NoteRow : ListingRow
    {
        public required Text Text;

        public override void Bind(ListingItem item)
        {
            var note = item as PeopleNoteItem;
            ListingStyle.SetText(Text, item.Label);
            ListingStyle.SetTextColor(Text, note?.Tint ?? DashTheme.TextMuted);
            bool wrap = note?.Wrap ?? false;
            if (Text.WordWrap.Value != wrap)
                Text.WordWrap.Value = wrap;
            var align = note is { Centered: true } ? TextHorizontalAlignment.Center : TextHorizontalAlignment.Left;
            if (Text.HorizontalAlignment.Value != align)
                Text.HorizontalAlignment.Value = align;
        }
    }
}

// ONE PERSON IN THE LIST
//
// The face with its status dot, the name in the account's own colour, one line of where they are, and on
// the right whatever is waiting: unread messages, or for a search hit where you stand with them.
internal sealed class PeoplePersonTemplate : ListingRowTemplate
{
    private const float FaceLeft = 10f;
    private const float TextLeft = FaceLeft + PeopleMetrics.RowFace + 12f;
    private const float ChipWidth = 92f;
    private const float UnreadWidth = 30f;

    private readonly SettingsFonts _fonts;
    private readonly PeopleRelay _relay;
    private readonly PeoplePictureCache _pictures;

    public PeoplePersonTemplate(SettingsFonts fonts, PeopleRelay relay, PeoplePictureCache pictures)
    {
        _fonts = fonts;
        _relay = relay;
        _pictures = pictures;
    }

    public override float Height => PeopleMetrics.PersonRowHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var background = SettingsUI.Panel(row, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusControl);
        var open = row.AttachComponent<Button>();

        float half = PeopleMetrics.RowFace * 0.5f;
        var faceHost = SettingsUI.Child(row, "Face", new float2(0f, 0.5f), new float2(0f, 0.5f),
            new float2(FaceLeft, -half), new float2(FaceLeft + PeopleMetrics.RowFace, half));
        var face = PeopleAvatar.Build(faceHost, _fonts.Strong, PeopleMetrics.RowFace, 0f, PeopleMetrics.RowDot, DashTheme.FontBody);

        var name = SettingsUI.Label(row, "Name", _fonts.Medium, DashTheme.FontBody, DashTheme.Text,
            TextHorizontalAlignment.Left, new float2(0f, 1f), new float2(1f, 1f),
            new float2(TextLeft, -27f), new float2(-12f, -7f));
        var line = SettingsUI.Label(row, "Line", _fonts.Body, DashTheme.FontSmall, DashTheme.TextMuted,
            TextHorizontalAlignment.Left, new float2(0f, 0f), new float2(1f, 0f),
            new float2(TextLeft, 7f), new float2(-12f, 25f));

        var unread = SettingsUI.Child(row, "Unread", new float2(1f, 0.5f), new float2(1f, 0.5f),
            new float2(-(10f + UnreadWidth), -11f), new float2(-10f, 11f));
        SettingsUI.Panel(unread, DashTheme.Accent, color.Transparent, 11f);
        var unreadText = SettingsUI.FillLabel(unread, "Count", _fonts.Strong, DashTheme.FontLabel, DashTheme.OnAccent);
        unread.ActiveSelf.Value = false;

        var chip = SettingsUI.Child(row, "Chip", new float2(1f, 0.5f), new float2(1f, 0.5f),
            new float2(-(10f + ChipWidth), -11f), new float2(-10f, 11f));
        var chipPanel = SettingsUI.Panel(chip, DashTheme.Field, color.Transparent, DashTheme.RadiusChip);
        var chipText = SettingsUI.FillLabel(chip, "Text", _fonts.Medium, DashTheme.FontLabel, DashTheme.TextDim,
            TextHorizontalAlignment.Center, true);
        chip.ActiveSelf.Value = false;

        var listingRow = new PersonRow
        {
            Background = background,
            Open = open,
            Face = face,
            Name = name,
            NameRect = SettingsUI.Rect(name.Slot),
            Line = line,
            LineRect = SettingsUI.Rect(line.Slot),
            Unread = unread,
            UnreadText = unreadText,
            Chip = chip,
            ChipPanel = chipPanel,
            ChipText = chipText,
            Pictures = _pictures,
        };
        _relay.Register(open, listingRow, 0);
        open.HoverEntered += _ => listingRow.Hover(true);
        open.HoverExited += _ => listingRow.Hover(false);
        return listingRow;
    }

    private sealed class PersonRow : ListingRow
    {
        public required RoundedPanel Background;
        public required Button Open;
        public required PeopleAvatar Face;
        public required Text Name;
        public required RectTransform NameRect;
        public required Text Line;
        public required RectTransform LineRect;
        public required Slot Unread;
        public required Text UnreadText;
        public required Slot Chip;
        public required RoundedPanel ChipPanel;
        public required Text ChipText;
        public required PeoplePictureCache Pictures;

        private bool _shown;
        private bool _hovered;

        public void Hover(bool hovered)
        {
            _hovered = hovered;
            Paint();
        }

        private void Paint()
            => SettingsUI.SetPaint(Background, _shown || _hovered && Open.CanInteract ? DashTheme.SurfaceHover : DashTheme.Surface,
                _shown ? DashTheme.Accent.WithAlpha(0.55f) : DashTheme.Outline);

        public override void Bind(ListingItem item)
        {
            if (item is not PeoplePersonItem person)
                return;
            _shown = person.IsShown?.Invoke(person.UserId) ?? false;
            Paint();
            string tip = ProfileLink.ViewProfile.Resolve();
            if (Open.Tip.Value != tip)
                Open.Tip.Value = tip;

            Face.Bind(Pictures, person.AvatarHash, person.Name, person.NameTint, person.Mode, person.Animated);
            ListingStyle.SetText(Name, person.Name);
            ListingStyle.SetTextColor(Name, person.NameTint);
            ListingStyle.SetText(Line, person.Line);

            bool unread = person.Unread > 0;
            bool chip = !unread && person.Chip.Length > 0;
            PeopleShow.Set(Unread, unread);
            if (unread)
                ListingStyle.SetText(UnreadText, person.Unread > 99 ? "99+" : person.Unread.ToString());
            PeopleShow.Set(Chip, chip);
            float chipWidth = System.Math.Clamp(PeopleTag.Width(person.Chip), 44f, ChipWidth);
            if (chip)
            {
                ListingStyle.SetText(ChipText, person.Chip);
                SettingsUI.SetFill(ChipPanel, person.ChipTint.WithAlpha(0.2f));
                SettingsUI.SetOffsets(SettingsUI.Rect(Chip), new float2(-(10f + chipWidth), -11f), new float2(-10f, 11f));
            }
            float right = unread ? 10f + UnreadWidth + 8f : chip ? 10f + chipWidth + 8f : 12f;
            SettingsUI.SetOffsets(NameRect, new float2(TextLeft, -27f), new float2(-right, -7f));
            SettingsUI.SetOffsets(LineRect, new float2(TextLeft, 7f), new float2(-right, 25f));
        }
    }
}

// A row that opens a view in the pane: the requests, the blocked list.
internal sealed class PeopleLinkTemplate : ListingRowTemplate
{
    private readonly SettingsFonts _fonts;
    private readonly PeopleRelay _relay;

    public PeopleLinkTemplate(SettingsFonts fonts, PeopleRelay relay)
    {
        _fonts = fonts;
        _relay = relay;
    }

    public override float Height => PeopleMetrics.LinkRowHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var background = SettingsUI.Panel(row, DashTheme.Field.WithAlpha(0.7f), DashTheme.Outline, DashTheme.RadiusControl);
        var open = row.AttachComponent<Button>();
        var label = SettingsUI.Label(row, "Label", _fonts.Medium, DashTheme.FontSmall, DashTheme.TextDim,
            TextHorizontalAlignment.Left, float2.Zero, float2.One, new float2(SettingsMetrics.RowInset, 0f), new float2(-120f, 0f));
        var count = SettingsUI.Child(row, "Count", new float2(1f, 0.5f), new float2(1f, 0.5f),
            new float2(-110f, -11f), new float2(-28f, 11f));
        var countPanel = SettingsUI.Panel(count, DashTheme.Accent, color.Transparent, 11f);
        var countText = SettingsUI.FillLabel(count, "Text", _fonts.Strong, DashTheme.FontLabel, DashTheme.OnAccent,
            TextHorizontalAlignment.Center, true);
        SettingsUI.Label(row, "Arrow", _fonts.Medium, DashTheme.FontHeading, DashTheme.TextMuted,
            TextHorizontalAlignment.Center, new float2(1f, 0f), new float2(1f, 1f), new float2(-24f, 0f), new float2(-8f, 0f))
            .Content.Value = "›";
        var listingRow = new LinkRow { Background = background, Label = label, Count = count, CountPanel = countPanel, CountText = countText };
        _relay.Register(open, listingRow, 0);
        return listingRow;
    }

    private sealed class LinkRow : ListingRow
    {
        public required RoundedPanel Background;
        public required Text Label;
        public required Slot Count;
        public required RoundedPanel CountPanel;
        public required Text CountText;

        public override void Bind(ListingItem item)
        {
            var link = item as PeopleLinkItem;
            ListingStyle.SetText(Label, item.Label);
            bool hasCount = link != null && link.Count.Length > 0;
            PeopleShow.Set(Count, hasCount);
            if (!hasCount)
                return;
            ListingStyle.SetText(CountText, link!.Count);
            SettingsUI.SetFill(CountPanel, link.Lit ? DashTheme.Accent : DashTheme.Surface);
            ListingStyle.SetTextColor(CountText, link.Lit ? DashTheme.OnAccent : DashTheme.TextDim);
        }
    }
}

// WHERE THEY ARE
//
// A world card when they are somewhere a friend may see, with Join when that session would let you in; a
// plain line when the world is private, hidden, or they are not on.
internal sealed class PeopleWhereTemplate : ListingRowTemplate
{
    private const float Tile = 52f;

    private readonly SettingsFonts _fonts;
    private readonly PeopleRelay _relay;

    public PeopleWhereTemplate(SettingsFonts fonts, PeopleRelay relay)
    {
        _fonts = fonts;
        _relay = relay;
    }

    public override float Height => PeopleMetrics.WhereHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var background = SettingsUI.Panel(row, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusCard);
        var tileHost = SettingsUI.Child(row, "Tile", new float2(0f, 0.5f), new float2(0f, 0.5f),
            new float2(12f, -Tile * 0.5f), new float2(12f + Tile, Tile * 0.5f));
        var tile = tileHost.AttachComponent<GradientPanel>();
        var initial = SettingsUI.FillLabel(tileHost, "Initial", _fonts.Strong, DashTheme.FontTitle, DashTheme.Text);
        float textLeft = 12f + Tile + 12f;
        var title = SettingsUI.Label(row, "Title", _fonts.Medium, DashTheme.FontHeading, DashTheme.Text,
            TextHorizontalAlignment.Left, new float2(0f, 0.5f), new float2(1f, 0.5f),
            new float2(textLeft, 0f), new float2(-(PeopleMetrics.ActionWidth + 22f), 24f), shrinkToFit: true);
        var line = SettingsUI.Label(row, "Line", _fonts.Body, DashTheme.FontSmall, DashTheme.TextMuted,
            TextHorizontalAlignment.Left, new float2(0f, 0.5f), new float2(1f, 0.5f),
            new float2(textLeft, -22f), new float2(-(PeopleMetrics.ActionWidth + 22f), -2f));
        var join = SettingsChip.Build(GroupBand.Right(row, "Join", 14f, PeopleMetrics.ActionWidth, PeopleMetrics.ActionHeight),
            _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        var listingRow = new WhereRow
        {
            Background = background, Tile = tile, TileSlot = tileHost, Initial = initial, Title = title, TitleRect = SettingsUI.Rect(title.Slot),
            Line = line, LineRect = SettingsUI.Rect(line.Slot), Join = join,
        };
        _relay.Register(join.Button, listingRow, 0);
        return listingRow;
    }

    private sealed class WhereRow : ListingRow
    {
        public required RoundedPanel Background;
        public required GradientPanel Tile;
        public required Slot TileSlot;
        public required Text Initial;
        public required Text Title;
        public required RectTransform TitleRect;
        public required Text Line;
        public required RectTransform LineRect;
        public required SettingsChip Join;

        public override void Bind(ListingItem item)
        {
            if (item is not PeopleWhereItem where)
                return;
            SettingsUI.SetPaint(Background, where.InWorld ? DashTheme.Surface : DashTheme.Field.WithAlpha(0.7f), DashTheme.Outline);
            PeopleShow.Set(TileSlot, where.InWorld);
            if (where.InWorld)
            {
                var top = new color(where.Tint.r * 0.6f, where.Tint.g * 0.6f, where.Tint.b * 0.6f, 1f);
                SetColor(Tile.TopLeft, top);
                SetColor(Tile.TopRight, top);
                SetColor(Tile.BottomLeft, DashTheme.Field);
                SetColor(Tile.BottomRight, DashTheme.Field);
                ListingStyle.SetText(Initial, where.Initial);
            }
            float left = where.InWorld ? 76f : SettingsMetrics.RowInset;
            float right = where.CanJoin ? PeopleMetrics.ActionWidth + 22f : SettingsMetrics.RowInset;
            SettingsUI.SetOffsets(TitleRect, new float2(left, 0f), new float2(-right, 24f));
            SettingsUI.SetOffsets(LineRect, new float2(left, -22f), new float2(-right, -2f));
            ListingStyle.SetText(Title, where.Title);
            ListingStyle.SetText(Line, where.Line);
            PeopleShow.Set(Line.Slot, where.Line.Length > 0);
            PeopleShow.Set(Join.Slot, where.CanJoin);
            if (where.CanJoin)
                GroupBand.PaintAction(Join, PeopleLocale.Join.Resolve(), true, false, true);
        }

        private static void SetColor(Sync<color> field, in color value)
        {
            if (field.Value != value)
                field.Value = value;
        }
    }
}

// A block of wrapped text: the bio. Its height is worked out when the item is made, from the pane's width.
internal sealed class PeopleProseTemplate : ListingRowTemplate
{
    private readonly SettingsFonts _fonts;

    public PeopleProseTemplate(SettingsFonts fonts) => _fonts = fonts;

    public override float Height => 40f;
    public override float HeightFor(ListingItem item) => item is PeopleProseItem prose ? prose.Height : Height;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var background = SettingsUI.Panel(row, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusCard);
        var text = SettingsUI.Label(row, "Text", _fonts.Body, DashTheme.FontBody, DashTheme.Text,
            TextHorizontalAlignment.Left, float2.Zero, float2.One, new float2(14f, 10f), new float2(-14f, -10f));
        text.WordWrap.Value = true;
        text.Ellipsis.Value = false;
        text.RichText.Value = false;
        text.VerticalAlignment.Value = TextVerticalAlignment.Top;
        return new ProseRow { Background = background, Text = text };
    }

    private sealed class ProseRow : ListingRow
    {
        public required RoundedPanel Background;
        public required Text Text;

        public override void Bind(ListingItem item)
        {
            if (item is not PeopleProseItem prose)
                return;
            if (Text.RichText.Value != prose.Rich)
                Text.RichText.Value = prose.Rich;
            ListingStyle.SetText(Text, prose.Text);
            ListingStyle.SetTextColor(Text, prose.Muted ? DashTheme.TextMuted : DashTheme.Text);
        }
    }
}

// The addresses a bio links to, a pill each, wrapped onto as many lines as the body's width needs. A pill
// opens nothing itself: the screen asks first, with the whole address in front of the person.
internal sealed class PeopleLinksTemplate : ListingRowTemplate
{
    private const int LabelMax = 34;
    private const float Inset = 2f;

    private readonly SettingsFonts _fonts;
    private readonly PeopleRelay _relay;

    public PeopleLinksTemplate(SettingsFonts fonts, PeopleRelay relay)
    {
        _fonts = fonts;
        _relay = relay;
    }

    public override float Height => PeopleMetrics.LinksLine;
    public override float HeightFor(ListingItem item)
        => item is PeopleLinksItem links ? Lines(links) * PeopleMetrics.LinksLine : Height;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var listingRow = new LinksRow();
        for (int i = 0; i < Lumora.Core.Social.BioMarkdown.LinksMax; i++)
        {
            var host = SettingsUI.Child(row, "Link" + i, new float2(0f, 1f), new float2(0f, 1f), float2.Zero, float2.Zero);
            var chip = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
            host.ActiveSelf.Value = false;
            _relay.Register(chip.Button, listingRow, i);
            listingRow.Chips.Add(chip);
        }
        return listingRow;
    }

    // The address as a pill reads it: no scheme, cut short. The prompt shows it whole.
    public static string Label(System.Uri uri)
    {
        string shown = Lumora.Core.Social.BioMarkdown.Display(uri);
        int scheme = shown.IndexOf("://", System.StringComparison.Ordinal);
        if (scheme >= 0)
            shown = shown.Substring(scheme + 3);
        return shown.Length <= LabelMax ? shown : shown.Substring(0, LabelMax - 1) + "…";
    }

    private static float Width(string label) => System.MathF.Max(84f, 26f + label.Length * 7.4f);

    private static int Lines(PeopleLinksItem links)
    {
        int lines = 1;
        float x = Inset;
        for (int i = 0; i < links.Links.Count && i < Lumora.Core.Social.BioMarkdown.LinksMax; i++)
        {
            float width = System.MathF.Min(Width(Label(links.Links[i])), links.Width - Inset * 2f);
            if (x > Inset && x + width > links.Width - Inset)
            {
                lines++;
                x = Inset;
            }
            x += width + PeopleMetrics.PillGap;
        }
        return lines;
    }

    private sealed class LinksRow : ListingRow
    {
        public readonly System.Collections.Generic.List<SettingsChip> Chips = new();

        public override void Bind(ListingItem item)
        {
            var links = item as PeopleLinksItem;
            int line = 0;
            float x = Inset;
            float pill = PeopleMetrics.ActionHeight;
            float pad = (PeopleMetrics.LinksLine - pill) * 0.5f;
            for (int i = 0; i < Chips.Count; i++)
            {
                bool used = links != null && i < links.Links.Count;
                PeopleShow.Set(Chips[i].Slot, used);
                if (!used)
                    continue;
                string label = Label(links!.Links[i]);
                float width = System.MathF.Min(Width(label), links.Width - Inset * 2f);
                if (x > Inset && x + width > links.Width - Inset)
                {
                    line++;
                    x = Inset;
                }
                float top = line * PeopleMetrics.LinksLine + pad;
                SettingsUI.SetOffsets(SettingsUI.Rect(Chips[i].Slot), new float2(x, -(top + pill)), new float2(x + width, -top));
                GroupBand.PaintAction(Chips[i], label, true, false, false);
                x += width + PeopleMetrics.PillGap;
            }
        }
    }
}

// A badge the platform gave them: its picture or its colour, its name, what it is for.
internal sealed class PeopleBadgeTemplate : ListingRowTemplate
{
    private const float Icon = 34f;

    private readonly SettingsFonts _fonts;
    private readonly PeoplePictureCache _pictures;

    public PeopleBadgeTemplate(SettingsFonts fonts, PeoplePictureCache pictures)
    {
        _fonts = fonts;
        _pictures = pictures;
    }

    public override float Height => PeopleMetrics.BadgeHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var background = SettingsUI.Panel(row, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusControl);
        var iconHost = SettingsUI.Child(row, "Icon", new float2(0f, 0.5f), new float2(0f, 0.5f),
            new float2(10f, -Icon * 0.5f), new float2(10f + Icon, Icon * 0.5f));
        var icon = PeopleThumb.Build(iconHost, _fonts.Strong, Icon * 0.5f, DashTheme.FontBody);
        float left = 10f + Icon + 12f;
        var name = SettingsUI.Label(row, "Name", _fonts.Medium, DashTheme.FontBody, DashTheme.Text,
            TextHorizontalAlignment.Left, new float2(0f, 1f), new float2(1f, 1f), new float2(left, -26f), new float2(-12f, -6f));
        var detail = SettingsUI.Label(row, "Detail", _fonts.Body, DashTheme.FontSmall, DashTheme.TextMuted,
            TextHorizontalAlignment.Left, new float2(0f, 0f), new float2(1f, 0f), new float2(left, 6f), new float2(-12f, 24f));
        return new BadgeRow { Background = background, Icon = icon, Name = name, Detail = detail, Pictures = _pictures };
    }

    private sealed class BadgeRow : ListingRow
    {
        public required RoundedPanel Background;
        public required PeopleThumb Icon;
        public required Text Name;
        public required Text Detail;
        public required PeoplePictureCache Pictures;

        public override void Bind(ListingItem item)
        {
            if (item is not PeopleBadgeItem badge)
                return;
            Icon.Bind(Pictures, badge.IconHash, badge.Name, badge.Tint);
            ListingStyle.SetText(Name, badge.Name);
            ListingStyle.SetTextColor(Name, Lift(badge.Tint));
            ListingStyle.SetText(Detail, badge.Description);
            PeopleShow.Set(Detail.Slot, badge.Description.Length > 0);
        }

        private static color Lift(in color c) => new color(c.r + (1f - c.r) * 0.3f, c.g + (1f - c.g) * 0.3f, c.b + (1f - c.b) * 0.3f, 1f);
    }
}

// A group they are in. The one they represent comes first and says so.
internal sealed class PeopleGroupTemplate : ListingRowTemplate
{
    private const float Icon = 34f;
    private const float ChipWidth = 104f;

    private readonly SettingsFonts _fonts;
    private readonly PeopleRelay _relay;
    private readonly PeoplePictureCache _pictures;

    public PeopleGroupTemplate(SettingsFonts fonts, PeopleRelay relay, PeoplePictureCache pictures)
    {
        _fonts = fonts;
        _relay = relay;
        _pictures = pictures;
    }

    public override float Height => PeopleMetrics.GroupHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var background = SettingsUI.Panel(row, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusControl);
        var open = row.AttachComponent<Button>();
        var iconHost = SettingsUI.Child(row, "Icon", new float2(0f, 0.5f), new float2(0f, 0.5f),
            new float2(10f, -Icon * 0.5f), new float2(10f + Icon, Icon * 0.5f));
        var icon = PeopleThumb.Build(iconHost, _fonts.Strong, DashTheme.RadiusControl, DashTheme.FontSmall);
        float left = 10f + Icon + 12f;
        var name = SettingsUI.Label(row, "Name", _fonts.Medium, DashTheme.FontBody, DashTheme.Text,
            TextHorizontalAlignment.Left, new float2(0f, 1f), new float2(1f, 1f), new float2(left, -26f), new float2(-(ChipWidth + 22f), -6f));
        var role = SettingsUI.Label(row, "Role", _fonts.Body, DashTheme.FontSmall, DashTheme.TextMuted,
            TextHorizontalAlignment.Left, new float2(0f, 0f), new float2(1f, 0f), new float2(left, 6f), new float2(-(ChipWidth + 22f), 24f));
        var chip = SettingsChip.Build(GroupBand.Right(row, "Chip", 12f, ChipWidth, 22f), _fonts.Medium, DashTheme.FontLabel, DashTheme.RadiusChip);
        var listingRow = new GroupRow { Background = background, Icon = icon, Name = name, Role = role, Chip = chip, Pictures = _pictures };
        _relay.Register(open, listingRow, 0);
        return listingRow;
    }

    private sealed class GroupRow : ListingRow
    {
        public required RoundedPanel Background;
        public required PeopleThumb Icon;
        public required Text Name;
        public required Text Role;
        public required SettingsChip Chip;
        public required PeoplePictureCache Pictures;

        public override void Bind(ListingItem item)
        {
            if (item is not PeopleGroupItem group)
                return;
            SettingsUI.SetPaint(Background, DashTheme.Surface, group.Represented ? group.Tint.WithAlpha(0.6f) : DashTheme.Outline);
            Icon.Bind(Pictures, group.IconHash, group.GroupTag.Length > 0 ? group.GroupTag : group.Name, group.Tint);
            ListingStyle.SetText(Name, group.GroupTag.Length > 0 ? group.Name + "  [" + group.GroupTag + "]" : group.Name);
            ListingStyle.SetText(Role, group.Role);
            PeopleShow.Set(Chip.Slot, group.Represented);
            if (group.Represented)
                GroupBand.PaintChip(Chip, PeopleLocale.Represented.Resolve(), true);
        }
    }
}

internal sealed class PeopleFactTemplate : ListingRowTemplate
{
    private readonly SettingsFonts _fonts;

    public PeopleFactTemplate(SettingsFonts fonts) => _fonts = fonts;

    public override float Height => PeopleMetrics.FactHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var label = SettingsUI.Label(row, "Label", _fonts.Body, DashTheme.FontSmall, DashTheme.TextMuted,
            TextHorizontalAlignment.Left, float2.Zero, new float2(0.36f, 1f), new float2(SettingsMetrics.RowInset, 0f), new float2(-8f, 0f));
        var value = SettingsUI.Label(row, "Value", _fonts.Body, DashTheme.FontBody, DashTheme.Text,
            TextHorizontalAlignment.Left, new float2(0.36f, 0f), float2.One, float2.Zero, new float2(-SettingsMetrics.RowInset, 0f));
        SettingsUI.Divider(row, "Rule");
        return new FactRow { Label = label, Value = value };
    }

    private sealed class FactRow : ListingRow
    {
        public required Text Label;
        public required Text Value;

        public override void Bind(ListingItem item)
        {
            ListingStyle.SetText(Label, item.Label);
            ListingStyle.SetText(Value, (item as PeopleFactItem)?.Value ?? string.Empty);
        }
    }
}

// SOMEBODY WITH ANSWERS: a request either way, a block. The name opens their profile.
internal sealed class PeopleAnswerTemplate : ListingRowTemplate
{
    private const float FaceLeft = 10f;
    private const float TextLeft = FaceLeft + PeopleMetrics.RowFace + 12f;
    private const float TagHeight = 16f;

    private readonly SettingsFonts _fonts;
    private readonly PeopleRelay _relay;
    private readonly PeoplePictureCache _pictures;

    public PeopleAnswerTemplate(SettingsFonts fonts, PeopleRelay relay, PeoplePictureCache pictures)
    {
        _fonts = fonts;
        _relay = relay;
        _pictures = pictures;
    }

    public override float Height => PeopleMetrics.AnswerHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var background = SettingsUI.Panel(row, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusControl);
        var open = row.AttachComponent<Button>();
        float half = PeopleMetrics.RowFace * 0.5f;
        var faceHost = SettingsUI.Child(row, "Face", new float2(0f, 0.5f), new float2(0f, 0.5f),
            new float2(FaceLeft, -half), new float2(FaceLeft + PeopleMetrics.RowFace, half));
        var face = PeopleAvatar.Build(faceHost, _fonts.Strong, PeopleMetrics.RowFace, 0f, 0f, DashTheme.FontBody);
        float right = 14f + 2f * PeopleMetrics.ActionWidth + 2f * PeopleMetrics.PillGap;
        var name = SettingsUI.Label(row, "Name", _fonts.Medium, DashTheme.FontBody, DashTheme.Text,
            TextHorizontalAlignment.Left, new float2(0f, 1f), new float2(1f, 1f), new float2(TextLeft, -27f), new float2(-right, -7f));
        var meta = SettingsUI.Label(row, "Meta", _fonts.Body, DashTheme.FontSmall, DashTheme.TextMuted,
            TextHorizontalAlignment.Left, new float2(0f, 0f), new float2(1f, 0f), new float2(TextLeft, 7f), new float2(-right, 25f));
        var tag = PeopleTag.Build(row, "Tag", _fonts.Medium, TagHeight);
        var listingRow = new AnswerRow
        {
            Background = background, Open = open, Face = face, Pictures = _pictures, Name = name, Meta = meta,
            MetaRect = SettingsUI.Rect(meta.Slot), Tag = tag, Right = right,
        };
        listingRow.Secondary = Pill(row, listingRow, "Second", 14f + PeopleMetrics.ActionWidth + PeopleMetrics.PillGap, 2);
        listingRow.Primary = Pill(row, listingRow, "Primary", 14f, 1);
        _relay.Register(open, listingRow, 0);
        open.HoverEntered += _ => listingRow.Hover(true);
        open.HoverExited += _ => listingRow.Hover(false);
        return listingRow;
    }

    private SettingsChip Pill(Slot row, ListingRow listingRow, string name, float right, int index)
    {
        var chip = SettingsChip.Build(GroupBand.Right(row, name, right, PeopleMetrics.ActionWidth, PeopleMetrics.ActionHeight),
            _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        _relay.Register(chip.Button, listingRow, index);
        chip.Slot.ActiveSelf.Value = false;
        return chip;
    }

    private sealed class AnswerRow : ListingRow
    {
        public required RoundedPanel Background;
        public required Button Open;
        public required PeopleAvatar Face;
        public required PeoplePictureCache Pictures;
        public required Text Name;
        public required Text Meta;
        public required RectTransform MetaRect;
        public required PeopleTag Tag;
        public required float Right;
        public SettingsChip Primary = null!;
        public SettingsChip Secondary = null!;

        private bool _hovered;

        public void Hover(bool hovered)
        {
            _hovered = hovered;
            SettingsUI.SetPaint(Background, _hovered && Open.CanInteract ? DashTheme.SurfaceHover : DashTheme.Surface, DashTheme.Outline);
        }

        public override void Bind(ListingItem item)
        {
            if (item is not PeopleAnswerItem answer)
                return;
            Hover(_hovered);
            string tip = ProfileLink.ViewProfile.Resolve();
            if (Open.Tip.Value != tip)
                Open.Tip.Value = tip;
            Face.Bind(Pictures, answer.AvatarHash, answer.Name, answer.NameTint, null, answer.Animated);
            ListingStyle.SetText(Name, answer.Name);
            ListingStyle.SetTextColor(Name, answer.NameTint);
            ListingStyle.SetText(Meta, answer.Meta);
            // the tag rides the start of the meta line and pushes the words along
            float metaTop = PeopleMetrics.AnswerHeight - 25f;
            float tagWidth = Tag.Place(answer.Badge, PeopleFormat.BotTint, TextLeft, metaTop + 1f, TagHeight);
            float metaLeft = TextLeft + (tagWidth > 0f ? tagWidth + 6f : 0f);
            SettingsUI.SetOffsets(MetaRect, new float2(metaLeft, 7f), new float2(-Right, 25f));
            PeopleShow.Set(Primary.Slot, answer.PrimaryLabel.Length > 0);
            if (answer.PrimaryLabel.Length > 0)
                GroupBand.PaintAction(Primary, answer.PrimaryLabel, true, false, answer.PrimaryLit);
            PeopleShow.Set(Secondary.Slot, answer.SecondaryLabel.Length > 0);
            if (answer.SecondaryLabel.Length > 0)
                GroupBand.PaintAction(Secondary, answer.SecondaryLabel, true, false, false);
        }
    }
}

// A ROW OF PILLS
internal sealed class PeopleActionsTemplate : ListingRowTemplate
{
    private const int MaxButtons = 4;

    private readonly SettingsFonts _fonts;
    private readonly PeopleRelay _relay;

    public PeopleActionsTemplate(SettingsFonts fonts, PeopleRelay relay)
    {
        _fonts = fonts;
        _relay = relay;
    }

    public override float Height => PeopleMetrics.ActionsHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var listingRow = new ActionsRow();
        for (int i = 0; i < MaxButtons; i++)
        {
            var host = GroupBand.Left(row, "Action", 0f, PeopleMetrics.ActionWidth, PeopleMetrics.ActionHeight);
            var chip = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
            host.ActiveSelf.Value = false;
            _relay.Register(chip.Button, listingRow, i);
            listingRow.Chips.Add(chip);
        }
        return listingRow;
    }

    private sealed class ActionsRow : ListingRow
    {
        public readonly System.Collections.Generic.List<SettingsChip> Chips = new();

        public override void Bind(ListingItem item)
        {
            var actions = item as PeopleActionsItem;
            int count = actions?.Buttons.Count ?? 0;
            float cursor = 0f;
            for (int i = 0; i < Chips.Count; i++)
            {
                bool used = i < count;
                PeopleShow.Set(Chips[i].Slot, used);
                if (!used)
                    continue;
                var entry = actions!.Buttons[i];
                SettingsUI.SetOffsets(SettingsUI.Rect(Chips[i].Slot), new float2(cursor, -PeopleMetrics.ActionHeight * 0.5f),
                    new float2(cursor + entry.Width, PeopleMetrics.ActionHeight * 0.5f));
                cursor += entry.Width + PeopleMetrics.PillGap;
                GroupBand.PaintAction(Chips[i], entry.Label, true, entry.Danger, entry.Lit);
            }
        }
    }
}
