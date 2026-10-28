// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// A chat's rows: the day, a message, a thing sent from an inventory, their dots while they type. Yours lean
// right in the accent, theirs lean left on the plain card, so a glance down the column tells who said what
// without a name on each. Holding a message, or pressing the "..." that comes up beside it under the pointer,
// asks what to do with it.

internal static class PeopleChatPaint
{
    public static readonly color Mine = Mix(DashTheme.Surface, DashTheme.Accent, 0.3f);
    public static readonly color MineHover = Mix(DashTheme.Surface, DashTheme.Accent, 0.4f);
    public static readonly color Theirs = DashTheme.Surface;
    // one of ours the service turned back: an edge, not a fill, so it reads as a note on the message
    public static readonly color FailedEdge = DashTheme.Warning.WithAlpha(0.55f);

    // their typing dots: one lit, the one it just left on its way down, the last at rest
    public static readonly color DotLit = DashTheme.TextDim;
    public static readonly color DotFading = Mix(DashTheme.TextMuted, DashTheme.TextDim, 0.45f);
    public static readonly color DotRest = DashTheme.TextMuted.WithAlpha(0.7f);

    public static color MetaTint(bool mine, bool sending, bool failed)
        => failed ? DashTheme.Warning : sending || !mine ? DashTheme.TextMuted : DashTheme.TextDim;

    private static color Mix(in color a, in color b, float t)
        => new color(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, 1f);
}

// The "..." beside one message. Drawn while the pointer is on the message or on it, but it takes a press
// wherever it sits even while it is not drawn, so a beam on its way across from the bubble never finds it gone
// half way. -xlinka
internal sealed class PeopleMessageMore
{
    public const float Size = 28f;
    private const float Gap = 4f;
    private const float Glyph = 16f;

    public readonly Button Button;
    private readonly RectTransform _rect;
    private readonly RoundedPanel _plate;
    private readonly Image _glyph;
    private bool _ownerHovered;
    private bool _hovered;
    private bool _pressed;

    private PeopleMessageMore(Button button, RectTransform rect, RoundedPanel plate, Image glyph)
    {
        Button = button;
        _rect = rect;
        _plate = plate;
        _glyph = glyph;
    }

    // owner is the message's own button, whose hover brings this up
    public static PeopleMessageMore Build(Slot row, IAssetProvider<TextureAsset>? icon, Button owner)
    {
        var slot = SettingsUI.Child(row, "More", new float2(0f, 1f), new float2(0f, 1f), new float2(0f, -Size), new float2(Size, 0f));
        var plate = SettingsUI.Panel(slot, color.Transparent, color.Transparent, Size * 0.5f);
        var button = slot.AttachComponent<Button>();
        button.HitOutset.Value = Gap;
        var glyphSlot = SettingsUI.Child(slot, "Glyph", new float2(0.5f, 0.5f), new float2(0.5f, 0.5f),
            new float2(-Glyph * 0.5f, -Glyph * 0.5f), new float2(Glyph * 0.5f, Glyph * 0.5f));
        var glyph = glyphSlot.AttachComponent<Image>();
        if (icon != null)
            glyph.Texture.Target = icon;
        glyph.PreserveAspect.Value = true;
        glyph.InteractionTarget.Value = false;
        glyph.Tint.Value = color.Transparent;

        var more = new PeopleMessageMore(button, SettingsUI.Rect(slot), plate, glyph);
        owner.HoverEntered += _ => more.Set(ref more._ownerHovered, true);
        owner.HoverExited += _ => more.Set(ref more._ownerHovered, false);
        button.HoverEntered += _ => more.Set(ref more._hovered, true);
        button.HoverExited += _ => { more._pressed = false; more.Set(ref more._hovered, false); };
        button.Pressed += _ => more.Set(ref more._pressed, true);
        button.Released += _ => more.Set(ref more._pressed, false);
        return more;
    }

    // Beside a block that leans right (ours) or left (theirs), top down from the top of the row.
    public void Place(bool mine, float inset, float blockWidth, float top, string tip)
    {
        float near = inset + blockWidth + Gap;
        if (mine)
        {
            SettingsUI.SetAnchors(_rect, new float2(1f, 1f), new float2(1f, 1f));
            SettingsUI.SetOffsets(_rect, new float2(-(near + Size), -(top + Size)), new float2(-near, -top));
        }
        else
        {
            SettingsUI.SetAnchors(_rect, new float2(0f, 1f), new float2(0f, 1f));
            SettingsUI.SetOffsets(_rect, new float2(near, -(top + Size)), new float2(near + Size, -top));
        }
        if (!string.Equals(Button.Tip.Value, tip, System.StringComparison.Ordinal))
            Button.Tip.Value = tip;
        Paint();
    }

    public void Reset()
    {
        _ownerHovered = false;
        _hovered = false;
        _pressed = false;
        Paint();
    }

    private void Set(ref bool flag, bool value)
    {
        if (flag == value)
            return;
        flag = value;
        Paint();
    }

    private void Paint()
    {
        if (_plate.IsDestroyed || _glyph.IsDestroyed)
            return;
        bool shown = _ownerHovered || _hovered;
        var fill = !shown ? color.Transparent : _pressed ? DashTheme.SurfacePressed : _hovered ? DashTheme.SurfaceHover : DashTheme.Surface;
        var edge = !shown ? color.Transparent : _hovered ? DashTheme.OutlineStrong : DashTheme.Outline;
        SettingsUI.SetPaint(_plate, fill, edge);
        var ink = !shown ? color.Transparent : _hovered ? DashTheme.Text : DashTheme.TextDim;
        if (!_glyph.Tint.Value.Equals(ink))
            _glyph.Tint.Value = ink;
    }
}

internal sealed class PeopleDayTemplate : ListingRowTemplate
{
    private readonly SettingsFonts _fonts;

    public PeopleDayTemplate(SettingsFonts fonts) => _fonts = fonts;

    public override float Height => PeopleMetrics.DayHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var left = SettingsUI.Child(row, "RuleLeft", new float2(0f, 0.5f), new float2(0.5f, 0.5f), new float2(8f, 0f), new float2(-64f, 1f));
        left.AttachComponent<Image>().Tint.Value = DashTheme.OutlineStrong;
        var right = SettingsUI.Child(row, "RuleRight", new float2(0.5f, 0.5f), new float2(1f, 0.5f), new float2(64f, 0f), new float2(-8f, 1f));
        right.AttachComponent<Image>().Tint.Value = DashTheme.OutlineStrong;
        var text = SettingsUI.Label(row, "Day", _fonts.Medium, DashTheme.FontLabel, DashTheme.TextMuted,
            TextHorizontalAlignment.Center, new float2(0.5f, 0f), new float2(0.5f, 1f), new float2(-60f, 0f), new float2(60f, 0f));
        return new DayRow { Text = text };
    }

    private sealed class DayRow : ListingRow
    {
        public required Text Text;

        public override void Bind(ListingItem item) => ListingStyle.SetText(Text, item.Label);
    }
}

// One typed message. Rich text is off: what somebody typed is shown as typed, a tag and all. One of ours the
// service turned back carries the warning colour on its edge and its status, and a short press on the bubble
// sends it again; a short press on any other bubble does nothing. A held press on any of them, or its "...",
// asks what to do with it.
internal sealed class PeopleMessageTemplate : ListingRowTemplate
{
    private const float Inset = 6f;
    // level with a one-line bubble's middle
    private const float MoreTop = 11f;

    private readonly SettingsFonts _fonts;
    private readonly PeopleRelay _relay;
    private readonly IAssetProvider<TextureAsset>? _moreIcon;

    public PeopleMessageTemplate(SettingsFonts fonts, PeopleRelay relay, IAssetProvider<TextureAsset>? moreIcon)
    {
        _fonts = fonts;
        _relay = relay;
        _moreIcon = moreIcon;
    }

    public override float Height => 40f;
    public override float HeightFor(ListingItem item) => item is PeopleMessageItem message ? message.Height : Height;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var bubble = SettingsUI.Child(row, "Bubble", float2.Zero, new float2(0f, 1f), new float2(Inset, 0f), new float2(Inset + 100f, 0f));
        var panel = SettingsUI.Panel(bubble, PeopleChatPaint.Theirs, DashTheme.Outline, DashTheme.RadiusCard);
        var text = SettingsUI.Label(bubble, "Text", _fonts.Body, PeopleMetrics.BubbleFont, DashTheme.Text,
            TextHorizontalAlignment.Left, float2.Zero, float2.One,
            new float2(PeopleMetrics.BubblePadX, PeopleMetrics.BubblePadY + PeopleMetrics.BubbleMetaHeight),
            new float2(-PeopleMetrics.BubblePadX, -PeopleMetrics.BubblePadY));
        text.WordWrap.Value = true;
        text.Ellipsis.Value = false;
        text.RichText.Value = false;
        text.VerticalAlignment.Value = TextVerticalAlignment.Top;
        var meta = SettingsUI.Label(bubble, "Meta", _fonts.Body, DashTheme.FontLabel - 1f, DashTheme.TextMuted,
            TextHorizontalAlignment.Right, float2.Zero, new float2(1f, 0f),
            new float2(PeopleMetrics.BubblePadX, PeopleMetrics.BubblePadY - 2f),
            new float2(-PeopleMetrics.BubblePadX, PeopleMetrics.BubblePadY - 2f + PeopleMetrics.BubbleMetaHeight));
        // after the panel: a button takes an Image on its own slot over as its colour, and this paint is ours
        var button = bubble.AttachComponent<Button>();
        // a hold has to be told from a press, so a press acts as it lets go
        button.ActOnRelease.Value = true;
        var more = PeopleMessageMore.Build(row, _moreIcon, button);
        var messageRow = new MessageRow { Bubble = SettingsUI.Rect(bubble), Panel = panel, Text = text, Meta = meta, Button = button, More = more };
        button.HoverEntered += _ => messageRow.SetHovered(true);
        button.HoverExited += _ => messageRow.SetHovered(false);
        _relay.Register(button, messageRow, 0);
        _relay.RegisterHold(button, messageRow);
        _relay.Register(more.Button, messageRow, 1);
        return messageRow;
    }

    private sealed class MessageRow : ListingRow
    {
        public required RectTransform Bubble;
        public required RoundedPanel Panel;
        public required Text Text;
        public required Text Meta;
        public required Button Button;
        public required PeopleMessageMore More;

        private bool _mine;
        private bool _failed;
        private bool _hovered;

        public override void Bind(ListingItem item)
        {
            if (item is not PeopleMessageItem message)
                return;
            _mine = message.Mine;
            _failed = message.Mine && message.Failed;
            if (message.Mine)
            {
                SettingsUI.SetAnchors(Bubble, new float2(1f, 0f), new float2(1f, 1f));
                SettingsUI.SetOffsets(Bubble, new float2(-(Inset + message.BubbleWidth), 0f), new float2(-Inset, 0f));
            }
            else
            {
                SettingsUI.SetAnchors(Bubble, float2.Zero, new float2(0f, 1f));
                SettingsUI.SetOffsets(Bubble, new float2(Inset, 0f), new float2(Inset + message.BubbleWidth, 0f));
            }
            More.Place(message.Mine, Inset, message.BubbleWidth, MoreTop, PeopleLocale.ChatMore.Resolve());
            Paint();
            ListingStyle.SetText(Text, message.Text);
            ListingStyle.SetText(Meta, message.Meta);
            ListingStyle.SetTextColor(Meta, PeopleChatPaint.MetaTint(message.Mine, message.Sending, _failed));
        }

        public override void Unbind()
        {
            _hovered = false;
            More.Reset();
        }

        public void SetHovered(bool value)
        {
            if (_hovered == value)
                return;
            _hovered = value;
            Paint();
        }

        // only one that a press sends again lights under the pointer; the rest bring up their "..." instead
        private void Paint()
            => SettingsUI.SetPaint(Panel,
                _mine ? (_hovered && _failed ? PeopleChatPaint.MineHover : PeopleChatPaint.Mine) : PeopleChatPaint.Theirs,
                _failed ? PeopleChatPaint.FailedEdge : _mine ? color.Transparent : DashTheme.Outline);
    }
}

// Something sent from an inventory: its picture, its name, what it is, and Spawn while it can still be had.
// One of ours that did not go through swaps Spawn for Retry. Held anywhere off its pills, or through its "...",
// it asks what to do with it like a typed one.
internal sealed class PeopleItemMessageTemplate : ListingRowTemplate
{
    private const float Inset = 6f;
    private const float Thumb = 58f;
    private const float PillWidth = 88f;
    private const float KeepWidth = 72f;
    private const float TextLeft = 10f + Thumb + 12f;
    private const float MoreTop = (PeopleMetrics.ItemCardHeight - PeopleMessageMore.Size) * 0.5f;

    private readonly SettingsFonts _fonts;
    private readonly PeopleRelay _relay;
    private readonly PeoplePictureCache _pictures;
    private readonly IAssetProvider<TextureAsset>? _moreIcon;
    private readonly Func<string, IAssetProvider<TextureAsset>?>? _icons;

    // icons: a bundled icon by name, for the kind of a thing sent without a picture
    public PeopleItemMessageTemplate(SettingsFonts fonts, PeopleRelay relay, PeoplePictureCache pictures, IAssetProvider<TextureAsset>? moreIcon,
        Func<string, IAssetProvider<TextureAsset>?>? icons = null)
    {
        _fonts = fonts;
        _relay = relay;
        _pictures = pictures;
        _moreIcon = moreIcon;
        _icons = icons;
    }

    public override float Height => PeopleMetrics.ItemCardHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var card = SettingsUI.Child(row, "Card", float2.Zero, new float2(0f, 1f), new float2(Inset, 0f),
            new float2(Inset + PeopleMetrics.ItemCardWidth, 0f));
        var panel = SettingsUI.Panel(card, PeopleChatPaint.Theirs, DashTheme.Outline, DashTheme.RadiusCard);
        // under the pills, which are built after it and so take their own presses; this one only holds
        var hold = card.AttachComponent<Button>();
        hold.ActOnRelease.Value = true;
        var thumbHost = SettingsUI.Child(card, "Thumb", new float2(0f, 1f), new float2(0f, 1f),
            new float2(10f, -(10f + Thumb)), new float2(10f + Thumb, -10f));
        var thumb = PeopleThumb.Build(thumbHost, _fonts.Strong, DashTheme.RadiusControl, DashTheme.FontTitle, glyph: true);
        var name = SettingsUI.Label(card, "Name", _fonts.Medium, DashTheme.FontBody, DashTheme.Text,
            TextHorizontalAlignment.Left, new float2(0f, 1f), new float2(1f, 1f), new float2(TextLeft, -32f), new float2(-12f, -10f));
        var kind = SettingsUI.Label(card, "Kind", _fonts.Body, DashTheme.FontSmall, DashTheme.TextDim,
            TextHorizontalAlignment.Left, new float2(0f, 1f), new float2(1f, 1f), new float2(TextLeft, -52f), new float2(-12f, -34f));
        // runs up to the pills, which Bind works out per card
        var meta = SettingsUI.Label(card, "Meta", _fonts.Body, DashTheme.FontLabel - 1f, DashTheme.TextMuted,
            TextHorizontalAlignment.Left, new float2(0f, 0f), new float2(1f, 0f), new float2(TextLeft, 8f), new float2(-(16f + PillWidth), 22f));
        var spawn = SettingsChip.Build(SettingsUI.Child(card, "Spawn", new float2(1f, 0f), new float2(1f, 0f),
                new float2(-(10f + PillWidth), 8f), new float2(-10f, 8f + 26f)),
            _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        var keep = SettingsChip.Build(SettingsUI.Child(card, "Keep", new float2(1f, 0f), new float2(1f, 0f),
                new float2(-(10f + PillWidth + 6f + KeepWidth), 8f), new float2(-(10f + PillWidth + 6f), 8f + 26f)),
            _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        var more = PeopleMessageMore.Build(row, _moreIcon, hold);
        var listingRow = new ItemRow
        {
            Card = SettingsUI.Rect(card), Panel = panel, Thumb = thumb, Name = name, KindText = kind, Meta = meta,
            MetaRect = SettingsUI.Rect(meta.Slot), Spawn = spawn, Keep = keep, Pictures = _pictures, More = more, Icons = _icons,
        };
        _relay.Register(spawn.Button, listingRow, 0);
        _relay.Register(keep.Button, listingRow, 1);
        _relay.RegisterHold(hold, listingRow);
        _relay.Register(more.Button, listingRow, 2);
        return listingRow;
    }

    private sealed class ItemRow : ListingRow
    {
        public required RectTransform Card;
        public required RoundedPanel Panel;
        public required PeopleThumb Thumb;
        public required Text Name;
        public required Text KindText;
        public required Text Meta;
        public required RectTransform MetaRect;
        public required SettingsChip Spawn;
        public required SettingsChip Keep;
        public required PeoplePictureCache Pictures;
        public required PeopleMessageMore More;
        public Func<string, IAssetProvider<TextureAsset>?>? Icons;

        public override void Unbind() => More.Reset();

        public override void Bind(ListingItem item)
        {
            if (item is not PeopleItemMessageItem message || message.Message.Item is not { } sent)
                return;
            bool failed = message.Mine && message.Failed;
            if (message.Mine)
            {
                SettingsUI.SetAnchors(Card, new float2(1f, 0f), new float2(1f, 1f));
                SettingsUI.SetOffsets(Card, new float2(-(Inset + PeopleMetrics.ItemCardWidth), 0f), new float2(-Inset, 0f));
            }
            else
            {
                SettingsUI.SetAnchors(Card, float2.Zero, new float2(0f, 1f));
                SettingsUI.SetOffsets(Card, new float2(Inset, 0f), new float2(Inset + PeopleMetrics.ItemCardWidth, 0f));
            }
            More.Place(message.Mine, Inset, PeopleMetrics.ItemCardWidth, MoreTop, PeopleLocale.ChatMore.Resolve());
            SettingsUI.SetPaint(Panel, message.Mine ? PeopleChatPaint.Mine : PeopleChatPaint.Theirs,
                failed ? PeopleChatPaint.FailedEdge : message.Mine ? color.Transparent : DashTheme.Outline);
            Thumb.Bind(Pictures, sent.ThumbnailHash, sent.Name, DashTheme.IconTeal, Icons?.Invoke(PeopleKindIcon.Of(sent.Kind)));
            ListingStyle.SetText(Name, sent.Name);
            ListingStyle.SetText(KindText, sent.Kind + "  ·  " + PeopleFormat.Bytes(sent.FileBytes));
            ListingStyle.SetText(Meta, message.Meta);
            ListingStyle.SetTextColor(Meta, PeopleChatPaint.MetaTint(message.Mine, message.Sending, failed));
            bool live = failed || !message.Expired;
            string label = failed ? PeopleLocale.ChatRetry.Resolve()
                : message.Expired ? PeopleLocale.Expired.Resolve()
                : message.IsWorld ? PeopleLocale.Open.Resolve() : PeopleLocale.Spawn.Resolve();
            GroupBand.PaintAction(Spawn, label, live, false, live);
            // yours already, or gone: nothing to keep
            bool keepable = !message.Mine && !message.Expired;
            PeopleShow.Set(Keep.Slot, keepable);
            if (keepable)
                GroupBand.PaintAction(Keep, (message.Kept ? PeopleLocale.Kept : PeopleLocale.Keep).Resolve(), !message.Kept, false, false);
            float pills = 10f + PillWidth + 6f + (keepable ? KeepWidth + 6f : 0f);
            SettingsUI.SetOffsets(MetaRect, new float2(TextLeft, 8f), new float2(-pills, 22f));
        }
    }
}

// Their dots, on their side at the foot of the chat while their pings keep coming. A light steps along the
// three off the world clock a few times a second; each step is a tint or two on a row that is its own chunk,
// so nothing else in the chat re-meshes for it, and nothing moves at all while nobody types. -xlinka
internal sealed class PeopleTypingTemplate : ListingRowTemplate
{
    private const float Inset = 6f;
    private const float Dot = 7f;
    private const float DotGap = 5f;
    private const float PadX = 14f;
    private const float BubbleWidth = PadX * 2f + Dot * 3f + DotGap * 2f;

    private readonly List<TypingRow> _rows = new();
    private long _step = -1;

    public override float Height => PeopleMetrics.TypingHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var bubble = SettingsUI.Child(row, "Bubble", float2.Zero, new float2(0f, 1f), new float2(Inset, 0f), new float2(Inset + BubbleWidth, 0f));
        SettingsUI.Panel(bubble, PeopleChatPaint.Theirs, DashTheme.Outline, DashTheme.RadiusCard);
        var dots = new RoundedPanel[3];
        for (int i = 0; i < dots.Length; i++)
        {
            float x = PadX + i * (Dot + DotGap);
            var dot = SettingsUI.Child(bubble, "Dot" + i, new float2(0f, 0.5f), new float2(0f, 0.5f),
                new float2(x, -Dot * 0.5f), new float2(x + Dot, Dot * 0.5f));
            dots[i] = SettingsUI.Panel(dot, PeopleChatPaint.DotRest, color.Transparent, Dot * 0.5f);
        }
        var typing = new TypingRow { Dots = dots };
        _rows.Add(typing);
        return typing;
    }

    // step counts up with the clock: four beats a round, one per dot and a rest with the last on its way down
    public void Animate(long step)
    {
        if (step == _step)
            return;
        _step = step;
        int beat = (int)(step % 4);
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Item != null)
                _rows[i].Light(beat);
        }
    }

    private sealed class TypingRow : ListingRow
    {
        public required RoundedPanel[] Dots;

        public override void Bind(ListingItem item) { }

        public void Light(int beat)
        {
            for (int i = 0; i < Dots.Length; i++)
                SettingsUI.SetFill(Dots[i], i == beat ? PeopleChatPaint.DotLit : i == beat - 1 ? PeopleChatPaint.DotFading : PeopleChatPaint.DotRest);
        }
    }
}
