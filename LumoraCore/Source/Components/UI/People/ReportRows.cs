// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Assets;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// The rows of the report form: cards like the profile editor's, the question on top and the answer under it.

internal static class ReportMetrics
{
    public const float StripTop = PeopleFormRow.LabelTop + PeopleFormRow.LabelHeight + 6f;
    public const float ChipMinWidth = 150f;
    public const int MaxColumns = 5;
    public const float HintHeight = 34f;
    public const float PreviewWidth = 192f;
    public const float PreviewHeight = 108f;
    public const float PillStep = PeopleMetrics.ActionHeight + 9f;
    public const float StatusHeight = 18f;
    public const float PictureHeight = StripTop + PreviewHeight + 8f + StatusHeight + 10f;
    public const float SendBand = 44f;
    public const float ErrorHeight = 40f;
    public const float SendWidth = 150f;
    public const float CancelWidth = 104f;
    public const float BarWidth = 260f;
    public const float ThanksHeight = 150f;
    public const float ThanksDisc = 44f;
    public const float DoneWidth = 160f;
}

internal sealed class ReportCategoriesItem : ListingItem
{
    public string[] Options = Array.Empty<string>();
    public int Selected = -1;
    public string Hint = string.Empty;
    public int Columns = 3;

    public ReportCategoriesItem(string key, LocaleText label) : base(key)
    {
        LabelText = label;
        Kind = PeopleScreen.KindReportCategory;
    }

    public int Lines => Options.Length == 0 ? 0 : (Options.Length + Columns - 1) / Columns;

    public float Height => ReportMetrics.StripTop + Lines * PeopleMetrics.ActionHeight + System.Math.Max(0, Lines - 1) * SettingsMetrics.SegmentGap
        + 8f + ReportMetrics.HintHeight + 8f;

    // As many to a line as fit the width, each wide enough for the longest of the words.
    public static int ColumnsFor(float width)
        => System.Math.Clamp((int)((width - 2f * SettingsMetrics.RowInset + SettingsMetrics.SegmentGap)
            / (ReportMetrics.ChipMinWidth + SettingsMetrics.SegmentGap)), 1, ReportMetrics.MaxColumns);
}

internal sealed class ReportPictureItem : ListingItem
{
    public IAssetProvider<TextureAsset>? Picture;
    public bool Has;
    public bool Busy;
    public string Status = string.Empty;
    public color StatusTint = DashTheme.TextMuted;

    public ReportPictureItem(string key, LocaleText label) : base(key)
    {
        LabelText = label;
        Kind = PeopleScreen.KindReportPicture;
    }
}

internal sealed class ReportSendItem : ListingItem
{
    public bool Sending;
    public float Progress;
    public string Error = string.Empty;

    public ReportSendItem(string key) : base(key)
    {
        Kind = PeopleScreen.KindReportSend;
    }

    public float Height => ReportMetrics.SendBand + (Error.Length > 0 ? ReportMetrics.ErrorHeight : 0f);
}

internal sealed class ReportThanksItem : ListingItem
{
    public string Body = string.Empty;

    public ReportThanksItem(string key) : base(key)
    {
        Kind = PeopleScreen.KindReportThanks;
    }
}

// What happened, as one press among the nine, and a line under them on what the pressed one covers.
internal sealed class ReportCategoriesTemplate : ListingRowTemplate
{
    private readonly SettingsFonts _fonts;
    private readonly PeopleRelay _relay;

    public ReportCategoriesTemplate(SettingsFonts fonts, PeopleRelay relay)
    {
        _fonts = fonts;
        _relay = relay;
    }

    public override float Height => PeopleMetrics.SegmentHeight;
    public override float HeightFor(ListingItem item) => item is ReportCategoriesItem categories ? categories.Height : Height;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        PeopleFormRow.Card(row);
        var label = PeopleFormRow.Label(row, _fonts);
        var grid = SettingsUI.Child(row, "Grid", float2.Zero, float2.One,
            new float2(SettingsMetrics.RowInset, 0f), new float2(-SettingsMetrics.RowInset, -ReportMetrics.StripTop));
        var hint = SettingsUI.Label(row, "Hint", _fonts.Body, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Left,
            float2.Zero, new float2(1f, 0f), new float2(SettingsMetrics.RowInset, 8f), new float2(-SettingsMetrics.RowInset, 8f + ReportMetrics.HintHeight));
        hint.WordWrap.Value = true;
        hint.Ellipsis.Value = false;
        var listingRow = new CategoriesRow { Label = label, Hint = hint };
        for (int i = 0; i < ReportCategories.All.Count; i++)
        {
            var host = SettingsUI.Child(grid, "Category" + i, new float2(0f, 1f), new float2(0f, 1f), float2.Zero, float2.Zero);
            var chip = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
            host.ActiveSelf.Value = false;
            _relay.Register(chip.Button, listingRow, i);
            listingRow.Chips.Add(chip);
        }
        return listingRow;
    }

    private sealed class CategoriesRow : ListingRow
    {
        public required Text Label;
        public required Text Hint;
        public readonly List<SettingsChip> Chips = new();

        public override void Bind(ListingItem item)
        {
            ListingStyle.SetText(Label, item.Label);
            if (item is not ReportCategoriesItem categories)
                return;
            int columns = System.Math.Max(1, categories.Columns);
            float half = SettingsMetrics.SegmentGap * 0.5f;
            for (int i = 0; i < Chips.Count; i++)
            {
                bool used = i < categories.Options.Length;
                PeopleShow.Set(Chips[i].Slot, used);
                if (!used)
                    continue;
                int column = i % columns;
                int line = i / columns;
                float top = line * (PeopleMetrics.ActionHeight + SettingsMetrics.SegmentGap);
                var rect = SettingsUI.Rect(Chips[i].Slot);
                SettingsUI.SetAnchors(rect, new float2((float)column / columns, 1f), new float2((float)(column + 1) / columns, 1f));
                SettingsUI.SetOffsets(rect, new float2(column == 0 ? 0f : half, -(top + PeopleMetrics.ActionHeight)),
                    new float2(column == columns - 1 ? 0f : -half, -top));
                GroupBand.PaintAction(Chips[i], categories.Options[i], true, false, i == categories.Selected);
            }
            ListingStyle.SetText(Hint, categories.Hint);
            ListingStyle.SetTextColor(Hint, categories.Selected >= 0 ? DashTheme.TextDim : DashTheme.TextMuted);
        }
    }
}

// The picture: what goes with the report, small, whole and uncropped, and the ways to change it.
internal sealed class ReportPictureTemplate : ListingRowTemplate
{
    private readonly SettingsFonts _fonts;
    private readonly PeopleRelay _relay;
    private readonly IAssetProvider<TextureAsset>? _cameraIcon;

    public ReportPictureTemplate(SettingsFonts fonts, PeopleRelay relay, IAssetProvider<TextureAsset>? cameraIcon)
    {
        _fonts = fonts;
        _relay = relay;
        _cameraIcon = cameraIcon;
    }

    public override float Height => ReportMetrics.PictureHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        PeopleFormRow.Card(row);
        var label = PeopleFormRow.Label(row, _fonts);
        float inset = SettingsMetrics.RowInset;
        float top = ReportMetrics.StripTop;
        var well = SettingsUI.Child(row, "Preview", new float2(0f, 1f), new float2(0f, 1f),
            new float2(inset, -(top + ReportMetrics.PreviewHeight)), new float2(inset + ReportMetrics.PreviewWidth, -top));
        SettingsUI.Panel(well, DashTheme.Field, DashTheme.Outline, DashTheme.RadiusControl);
        var glyphSlot = SettingsUI.Child(well, "Glyph", new float2(0.5f, 0.5f), new float2(0.5f, 0.5f), new float2(-14f, -14f), new float2(14f, 14f));
        var glyph = glyphSlot.AttachComponent<Image>();
        if (_cameraIcon != null)
            glyph.Texture.Target = _cameraIcon;
        glyph.PreserveAspect.Value = true;
        glyph.InteractionTarget.Value = false;
        glyph.Tint.Value = DashTheme.TextMuted;
        var imageSlot = SettingsUI.Child(well, "Image", float2.Zero, float2.One, new float2(2f, 2f), new float2(-2f, -2f));
        var image = imageSlot.AttachComponent<RawImage>();
        image.PreserveAspect.Value = true;
        image.InteractionTarget.Value = false;
        imageSlot.ActiveSelf.Value = false;

        var status = SettingsUI.Label(row, "Status", _fonts.Body, DashTheme.FontSmall, DashTheme.TextMuted, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(inset, -(top + ReportMetrics.PreviewHeight + 8f + ReportMetrics.StatusHeight)),
            new float2(-inset, -(top + ReportMetrics.PreviewHeight + 8f)));
        var listingRow = new PictureRow { Label = label, GlyphSlot = glyphSlot, ImageSlot = imageSlot, Image = image, Status = status };
        float left = inset + ReportMetrics.PreviewWidth + 14f;
        for (int i = 0; i < 3; i++)
        {
            float y = top + i * ReportMetrics.PillStep;
            var host = SettingsUI.Child(row, i == 0 ? "Take" : i == 1 ? "Choose" : "Remove", new float2(0f, 1f), new float2(1f, 1f),
                new float2(left, -(y + PeopleMetrics.ActionHeight)), new float2(-inset, -y));
            var chip = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
            _relay.Register(chip.Button, listingRow, i);
            listingRow.Pills.Add(chip);
        }
        return listingRow;
    }

    private sealed class PictureRow : ListingRow
    {
        public required Text Label;
        public required Slot GlyphSlot;
        public required Slot ImageSlot;
        public required RawImage Image;
        public required Text Status;
        public readonly List<SettingsChip> Pills = new();

        public override void Bind(ListingItem item)
        {
            ListingStyle.SetText(Label, item.Label);
            if (item is not ReportPictureItem picture)
                return;
            var provider = picture.Has ? picture.Picture : null;
            if (provider != null && !ReferenceEquals(Image.Texture.Target, provider))
                Image.Texture.Target = provider;
            bool ready = PeoplePictureCache.Ready(provider);
            PeopleShow.Set(ImageSlot, ready);
            PeopleShow.Set(GlyphSlot, !ready);
            ListingStyle.SetText(Status, picture.Status);
            ListingStyle.SetTextColor(Status, picture.StatusTint);
            GroupBand.PaintAction(Pills[0], ReportLocale.Take.Resolve(), !picture.Busy, false, false);
            GroupBand.PaintAction(Pills[1], ReportLocale.Choose.Resolve(), !picture.Busy, false, false);
            PeopleShow.Set(Pills[2].Slot, picture.Has);
            if (picture.Has)
                GroupBand.PaintAction(Pills[2], ReportLocale.Remove.Resolve(), !picture.Busy, false, false);
        }
    }
}

// Send and Cancel, or how far the sending is, with the reason it did not go above them when it did not.
internal sealed class ReportSendTemplate : ListingRowTemplate
{
    private readonly SettingsFonts _fonts;
    private readonly PeopleRelay _relay;

    public ReportSendTemplate(SettingsFonts fonts, PeopleRelay relay)
    {
        _fonts = fonts;
        _relay = relay;
    }

    public override float Height => ReportMetrics.SendBand;
    public override float HeightFor(ListingItem item) => item is ReportSendItem send ? send.Height : Height;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var error = SettingsUI.Label(row, "Error", _fonts.Medium, DashTheme.FontSmall, DashTheme.Negative, TextHorizontalAlignment.Left,
            float2.Zero, float2.One, new float2(2f, ReportMetrics.SendBand), new float2(-2f, -4f));
        error.WordWrap.Value = true;
        error.Ellipsis.Value = false;
        var listingRow = new SendRow { Error = error };
        listingRow.Send = Pill(row, listingRow, "Send", 0f, ReportMetrics.SendWidth, 0);
        listingRow.Cancel = Pill(row, listingRow, "Cancel", ReportMetrics.SendWidth + PeopleMetrics.PillGap, ReportMetrics.CancelWidth, 1);

        float middle = ReportMetrics.SendBand * 0.5f;
        var track = SettingsUI.Child(row, "Track", float2.Zero, float2.Zero, new float2(0f, middle - 4f), new float2(ReportMetrics.BarWidth, middle + 4f));
        SettingsUI.Panel(track, DashTheme.Field, color.Transparent, 4f);
        var fill = SettingsUI.Child(track, "Fill", float2.Zero, new float2(0f, 1f), float2.Zero, float2.Zero);
        SettingsUI.Panel(fill, DashTheme.Accent, color.Transparent, 4f);
        var progress = SettingsUI.Label(row, "Progress", _fonts.Medium, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Left,
            float2.Zero, new float2(1f, 0f), new float2(ReportMetrics.BarWidth + 12f, middle - 10f), new float2(0f, middle + 10f));
        listingRow.Track = track;
        listingRow.Fill = SettingsUI.Rect(fill);
        listingRow.Progress = progress;
        track.ActiveSelf.Value = false;
        progress.Slot.ActiveSelf.Value = false;
        return listingRow;
    }

    private SettingsChip Pill(Slot row, ListingRow listingRow, string name, float left, float width, int index)
    {
        float middle = ReportMetrics.SendBand * 0.5f;
        var host = SettingsUI.Child(row, name, float2.Zero, float2.Zero,
            new float2(left, middle - PeopleMetrics.ActionHeight * 0.5f), new float2(left + width, middle + PeopleMetrics.ActionHeight * 0.5f));
        var chip = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        _relay.Register(chip.Button, listingRow, index);
        return chip;
    }

    private sealed class SendRow : ListingRow
    {
        public required Text Error;
        public SettingsChip Send = null!;
        public SettingsChip Cancel = null!;
        public Slot Track = null!;
        public RectTransform Fill = null!;
        public Text Progress = null!;

        public override void Bind(ListingItem item)
        {
            if (item is not ReportSendItem send)
                return;
            ListingStyle.SetText(Error, send.Error);
            PeopleShow.Set(Error.Slot, send.Error.Length > 0);
            PeopleShow.Set(Send.Slot, !send.Sending);
            PeopleShow.Set(Cancel.Slot, !send.Sending);
            PeopleShow.Set(Track, send.Sending);
            PeopleShow.Set(Progress.Slot, send.Sending);
            if (send.Sending)
            {
                float share = System.Math.Clamp(send.Progress, 0f, 1f);
                SettingsUI.SetAnchors(Fill, float2.Zero, new float2(share, 1f));
                ListingStyle.SetText(Progress, (share >= 0.999f ? ReportLocale.Waiting : ReportLocale.Sending((int)MathF.Round(share * 100f))).Resolve());
                return;
            }
            GroupBand.PaintAction(Send, ReportLocale.Send.Resolve(), true, false, true);
            GroupBand.PaintAction(Cancel, ReportLocale.Cancel.Resolve(), true, false, false);
        }
    }
}

// The report went in: a tick, the thanks, the way back.
internal sealed class ReportThanksTemplate : ListingRowTemplate
{
    private readonly SettingsFonts _fonts;
    private readonly PeopleRelay _relay;
    private readonly IAssetProvider<TextureAsset>? _checkIcon;

    public ReportThanksTemplate(SettingsFonts fonts, PeopleRelay relay, IAssetProvider<TextureAsset>? checkIcon)
    {
        _fonts = fonts;
        _relay = relay;
        _checkIcon = checkIcon;
    }

    public override float Height => ReportMetrics.ThanksHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        PeopleFormRow.Card(row);
        float inset = 18f;
        float disc = ReportMetrics.ThanksDisc;
        var discSlot = SettingsUI.Child(row, "Disc", new float2(0f, 1f), new float2(0f, 1f), new float2(inset, -(inset + disc)), new float2(inset + disc, -inset));
        SettingsUI.Panel(discSlot, DashTheme.Positive.WithAlpha(0.18f), color.Transparent, disc * 0.5f);
        var glyphSlot = SettingsUI.Child(discSlot, "Glyph", new float2(0.5f, 0.5f), new float2(0.5f, 0.5f), new float2(-11f, -11f), new float2(11f, 11f));
        var glyph = glyphSlot.AttachComponent<Image>();
        if (_checkIcon != null)
            glyph.Texture.Target = _checkIcon;
        glyph.PreserveAspect.Value = true;
        glyph.InteractionTarget.Value = false;
        glyph.Tint.Value = DashTheme.Positive;

        float left = inset + disc + 14f;
        var title = SettingsUI.Label(row, "Title", _fonts.Strong, DashTheme.FontHeading, DashTheme.Text, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(left, -(inset + 24f)), new float2(-inset, -inset), shrinkToFit: true);
        title.Content.Value = ReportLocale.ThanksTitle.Resolve();
        var body = SettingsUI.Label(row, "Body", _fonts.Body, DashTheme.FontBody, DashTheme.TextDim, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(left, -(inset + 74f)), new float2(-inset, -(inset + 30f)));
        body.WordWrap.Value = true;
        body.Ellipsis.Value = false;
        body.VerticalAlignment.Value = TextVerticalAlignment.Top;
        var host = SettingsUI.Child(row, "Done", new float2(0f, 0f), new float2(0f, 0f),
            new float2(left, 16f), new float2(left + ReportMetrics.DoneWidth, 16f + PeopleMetrics.ActionHeight));
        var done = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        var listingRow = new ThanksRow { Title = title, Body = body, Done = done };
        _relay.Register(done.Button, listingRow, 0);
        return listingRow;
    }

    private sealed class ThanksRow : ListingRow
    {
        public required Text Title;
        public required Text Body;
        public required SettingsChip Done;

        public override void Bind(ListingItem item)
        {
            ListingStyle.SetText(Title, ReportLocale.ThanksTitle.Resolve());
            ListingStyle.SetText(Body, (item as ReportThanksItem)?.Body ?? string.Empty);
            GroupBand.PaintAction(Done, ReportLocale.Done.Resolve(), true, false, true);
        }
    }
}
