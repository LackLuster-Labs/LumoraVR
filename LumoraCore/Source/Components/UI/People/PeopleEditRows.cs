// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// The rows of your own profile's edit page and of the inventory picker. Every edit row is a card with its
// label on top and the control under it, so the page reads as one column of questions.

internal static class PeopleFormRow
{
    public const float LabelTop = 8f;
    public const float LabelHeight = 18f;
    public const float ControlBottom = 10f;

    public static RoundedPanel Card(Slot row) => SettingsUI.Panel(row, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusCard);

    public static Text Label(Slot row, SettingsFonts fonts)
        => SettingsUI.Label(row, "Label", fonts.Medium, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(SettingsMetrics.RowInset, -(LabelTop + LabelHeight)),
            new float2(-SettingsMetrics.RowInset, -LabelTop));
}

// A typed value, one line or many. Typing stays in the field until Enter or a click away, then goes to
// the item, which writes the draft; nothing is sent until Save.
internal sealed class PeopleFieldTemplate : ListingRowTemplate
{
    private readonly SettingsFonts _fonts;

    public PeopleFieldTemplate(SettingsFonts fonts) => _fonts = fonts;

    public override float Height => PeopleMetrics.FieldHeight;
    public override float HeightFor(ListingItem item) => item is PeopleFieldItem { Multiline: true } ? PeopleMetrics.BioHeight : Height;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        PeopleFormRow.Card(row);
        var label = PeopleFormRow.Label(row, _fonts);
        var host = SettingsUI.Child(row, "Field", float2.Zero, float2.One,
            new float2(SettingsMetrics.RowInset, PeopleFormRow.ControlBottom),
            new float2(-SettingsMetrics.RowInset, -(PeopleFormRow.LabelTop + PeopleFormRow.LabelHeight + 4f)));
        var input = DashTextField.Attach(host, _fonts.Body, DashTheme.FontBody, string.Empty, 64);
        input.SubmitOnBlur.Value = true;
        var text = host.FindChild("Text")?.GetComponent<Text>();
        var listingRow = new FieldRow { Label = label, Input = input, Text = text };
        input.EditingStarted += _ => listingRow.EditingKey = listingRow.Item?.Key;
        input.EditingFinished += (_, value) => listingRow.Commit(value);
        input.FocusLost += (_, _) => listingRow.EndEdit();
        return listingRow;
    }

    private sealed class FieldRow : ListingRow
    {
        public required Text Label;
        public required TextInput Input;
        public Text? Text;
        public string? EditingKey;

        public void Commit(string value)
        {
            if (Item is PeopleFieldItem field && field.Key == EditingKey)
                field.Write?.Invoke(value);
            EditingKey = null;
            if (Item != null)
                Bind(Item);
        }

        public void EndEdit()
        {
            if (Item != null && !Input.IsFocused)
                Bind(Item);
        }

        public override void Bind(ListingItem item)
        {
            ListingStyle.SetText(Label, item.Label);
            if (item is not PeopleFieldItem field)
                return;
            if (Input.Multiline.Value != field.Multiline)
                Input.Multiline.Value = field.Multiline;
            if (Text != null)
            {
                if (Text.WordWrap.Value != field.Multiline)
                    Text.WordWrap.Value = field.Multiline;
                var align = field.Multiline ? TextVerticalAlignment.Top : TextVerticalAlignment.Middle;
                if (Text.VerticalAlignment.Value != align)
                    Text.VerticalAlignment.Value = align;
                var rect = SettingsUI.Rect(Text.Slot);
                SettingsUI.SetOffsets(rect, new float2(DashTextField.TextInset, field.Multiline ? 8f : 0f),
                    new float2(-DashTextField.TextInset, field.Multiline ? -8f : 0f));
            }
            if (!string.Equals(Input.Placeholder.Value, field.Placeholder, System.StringComparison.Ordinal))
                Input.Placeholder.Value = field.Placeholder;
            if (Input.MaxLength.Value != field.MaxLength)
                Input.MaxLength.Value = field.MaxLength;
            if (Input.IsFocused)
                return;
            string value = field.Read?.Invoke() ?? string.Empty;
            if (!string.Equals(Input.Text.Value, value, System.StringComparison.Ordinal))
                Input.Text.Value = value;
        }

        public override void Unbind()
        {
            if (Input.IsFocused)
                Input.Unfocus();
            EditingKey = null;
        }
    }
}

// The bio: markdown typed in the box, and under it the way it reads, redrawn on every key so the preview never
// waits for Enter or a click away. Only the box writes the draft, and only when typing ends, like any field.
internal sealed class PeopleBioEditTemplate : ListingRowTemplate
{
    private const float FieldTop = PeopleFormRow.LabelTop + PeopleFormRow.LabelHeight + 4f;
    private const float FieldHeight = 140f;
    private const float PreviewLabelTop = FieldTop + FieldHeight + 8f;
    private const float PreviewTop = PreviewLabelTop + PeopleFormRow.LabelHeight + 2f;
    private const float HintBand = 30f;

    private readonly SettingsFonts _fonts;

    public PeopleBioEditTemplate(SettingsFonts fonts) => _fonts = fonts;

    public override float Height => PeopleMetrics.BioEditHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        float inset = SettingsMetrics.RowInset;
        PeopleFormRow.Card(row);
        var label = PeopleFormRow.Label(row, _fonts);

        var host = SettingsUI.Child(row, "Field", new float2(0f, 1f), new float2(1f, 1f),
            new float2(inset, -(FieldTop + FieldHeight)), new float2(-inset, -FieldTop));
        var input = DashTextField.Attach(host, _fonts.Body, DashTheme.FontBody, PeopleLocale.BioHint.Resolve(),
            Lumora.Nexus.Cloud.Cdn.SocialLimits.BioMax);
        input.SubmitOnBlur.Value = true;
        input.Multiline.Value = true;
        var typed = host.FindChild("Text")?.GetComponent<Text>();
        if (typed != null)
        {
            typed.WordWrap.Value = true;
            typed.VerticalAlignment.Value = TextVerticalAlignment.Top;
            SettingsUI.SetOffsets(SettingsUI.Rect(typed.Slot), new float2(DashTextField.TextInset, 8f), new float2(-DashTextField.TextInset, -8f));
        }

        var previewLabel = SettingsUI.Label(row, "PreviewLabel", _fonts.Medium, DashTheme.FontSmall, DashTheme.TextDim,
            TextHorizontalAlignment.Left, new float2(0f, 1f), new float2(1f, 1f),
            new float2(inset, -(PreviewLabelTop + PeopleFormRow.LabelHeight)), new float2(-inset, -PreviewLabelTop));
        var box = SettingsUI.Child(row, "Preview", float2.Zero, float2.One, new float2(inset, HintBand), new float2(-inset, -PreviewTop));
        SettingsUI.Panel(box, DashTheme.Field.WithAlpha(0.55f), DashTheme.Outline, DashTheme.RadiusControl);
        var preview = SettingsUI.Label(box, "Text", _fonts.Body, DashTheme.FontBody, DashTheme.Text, TextHorizontalAlignment.Left,
            float2.Zero, float2.One, new float2(12f, 8f), new float2(-12f, -8f));
        preview.WordWrap.Value = true;
        preview.RichText.Value = true;
        preview.Ellipsis.Value = true;
        preview.ClipToRect.Value = true;
        preview.VerticalAlignment.Value = TextVerticalAlignment.Top;

        // the syntax as typed, so this one never reads its own stars as bold
        var hint = SettingsUI.Label(row, "Hint", _fonts.Body, DashTheme.FontLabel, DashTheme.TextMuted, TextHorizontalAlignment.Left,
            new float2(0f, 0f), new float2(1f, 0f), new float2(inset, 7f), new float2(-inset, 25f));
        hint.RichText.Value = false;

        var listingRow = new BioRow { Label = label, Input = input, PreviewLabel = previewLabel, Preview = preview, Hint = hint };
        input.EditingStarted += _ => listingRow.EditingKey = listingRow.Item?.Key;
        input.EditingChanged += (_, value) => listingRow.Show(value);
        input.EditingFinished += (_, value) => listingRow.Commit(value);
        input.FocusLost += (_, _) => listingRow.EndEdit();
        return listingRow;
    }

    private sealed class BioRow : ListingRow
    {
        public required Text Label;
        public required TextInput Input;
        public required Text PreviewLabel;
        public required Text Preview;
        public required Text Hint;
        public string? EditingKey;

        public void Show(string value)
        {
            var render = Lumora.Core.Social.BioMarkdown.Render(value, PeopleFormat.BioInks);
            bool empty = render.Rich.Length == 0;
            ListingStyle.SetText(Preview, empty ? PeopleLocale.PreviewEmpty.Resolve() : render.Rich);
            ListingStyle.SetTextColor(Preview, empty ? DashTheme.TextMuted : DashTheme.Text);
        }

        public void Commit(string value)
        {
            if (Item is PeopleBioEditItem bio && bio.Key == EditingKey)
                bio.Write?.Invoke(value);
            EditingKey = null;
            if (Item != null)
                Bind(Item);
        }

        public void EndEdit()
        {
            if (Item != null && !Input.IsFocused)
                Bind(Item);
        }

        public override void Bind(ListingItem item)
        {
            ListingStyle.SetText(Label, item.Label);
            ListingStyle.SetText(PreviewLabel, PeopleLocale.Preview.Resolve());
            ListingStyle.SetText(Hint, PeopleLocale.BioMarkdown.Resolve());
            if (item is not PeopleBioEditItem bio)
                return;
            if (Input.IsFocused)
            {
                Show(Input.Text.Value ?? string.Empty);
                return;
            }
            string value = bio.Read?.Invoke() ?? string.Empty;
            if (!string.Equals(Input.Text.Value, value, System.StringComparison.Ordinal))
                Input.Text.Value = value;
            Show(value);
        }

        public override void Unbind()
        {
            if (Input.IsFocused)
                Input.Unfocus();
            EditingKey = null;
        }
    }
}

// Pronouns: typed, or one of the common sets from the pill. The line under says the rule, and turns into the
// warning while what is typed would be refused, so Save is never the first to say so.
internal sealed class PeoplePronounsTemplate : ListingRowTemplate
{
    private const float PickWidth = 104f;
    private const float BandTop = PeopleFormRow.LabelTop + PeopleFormRow.LabelHeight + 6f;

    private readonly SettingsFonts _fonts;
    private readonly PeopleRelay _relay;

    public PeoplePronounsTemplate(SettingsFonts fonts, PeopleRelay relay)
    {
        _fonts = fonts;
        _relay = relay;
    }

    public override float Height => PeopleMetrics.SegmentHintHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        float inset = SettingsMetrics.RowInset;
        PeopleFormRow.Card(row);
        var label = PeopleFormRow.Label(row, _fonts);
        var host = SettingsUI.Child(row, "Field", new float2(0f, 1f), new float2(1f, 1f),
            new float2(inset, -(BandTop + PeopleMetrics.ActionHeight)), new float2(-(14f + PickWidth + PeopleMetrics.PillGap), -BandTop));
        var input = DashTextField.Attach(host, _fonts.Body, DashTheme.FontBody, PeopleLocale.PronounsNone.Resolve(),
            Lumora.Nexus.Cloud.Cdn.SocialLimits.PronounsMax + 8);
        input.SubmitOnBlur.Value = true;
        var pickHost = SettingsUI.Child(row, "Pick", new float2(1f, 1f), new float2(1f, 1f),
            new float2(-(14f + PickWidth), -(BandTop + PeopleMetrics.ActionHeight)), new float2(-14f, -BandTop));
        var pick = SettingsChip.Build(pickHost, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        var hint = SettingsUI.Label(row, "Hint", _fonts.Body, DashTheme.FontLabel, DashTheme.TextMuted, TextHorizontalAlignment.Left,
            new float2(0f, 0f), new float2(1f, 0f), new float2(inset, 6f), new float2(-inset, 24f));

        var listingRow = new PronounsRow { Label = label, Input = input, Pick = pick, Hint = hint };
        _relay.Register(pick.Button, listingRow, 0);
        input.EditingStarted += _ => listingRow.EditingKey = listingRow.Item?.Key;
        input.EditingChanged += (_, value) => listingRow.Judge(value);
        input.EditingFinished += (_, value) => listingRow.Commit(value);
        input.FocusLost += (_, _) => listingRow.EndEdit();
        return listingRow;
    }

    private sealed class PronounsRow : ListingRow
    {
        public required Text Label;
        public required TextInput Input;
        public required SettingsChip Pick;
        public required Text Hint;
        public string? EditingKey;

        public void Judge(string value)
        {
            bool bad = Lumora.Core.Social.ProfilePronouns.Normalize(value) == null;
            ListingStyle.SetText(Hint, (bad ? PeopleLocale.PronounsBad : PeopleLocale.PronounsHint).Resolve());
            ListingStyle.SetTextColor(Hint, bad ? DashTheme.Warning : DashTheme.TextMuted);
        }

        public void Commit(string value)
        {
            if (Item is PeoplePronounsItem pronouns && pronouns.Key == EditingKey)
                pronouns.Write?.Invoke(value);
            EditingKey = null;
            if (Item != null)
                Bind(Item);
        }

        public void EndEdit()
        {
            if (Item != null && !Input.IsFocused)
                Bind(Item);
        }

        public override void Bind(ListingItem item)
        {
            ListingStyle.SetText(Label, item.Label);
            GroupBand.PaintAction(Pick, PeopleLocale.PronounsPick.Resolve(), true, false, true);
            if (item is not PeoplePronounsItem pronouns)
                return;
            if (Input.IsFocused)
            {
                Judge(Input.Text.Value ?? string.Empty);
                return;
            }
            string value = pronouns.Read?.Invoke() ?? string.Empty;
            if (!string.Equals(Input.Text.Value, value, System.StringComparison.Ordinal))
                Input.Text.Value = value;
            Judge(value);
        }

        public override void Unbind()
        {
            if (Input.IsFocused)
                Input.Unfocus();
            EditingKey = null;
        }
    }
}

// One of a few: status mode, who sees where you are.
internal sealed class PeopleSegmentTemplate : ListingRowTemplate
{
    private const int MaxOptions = 4;
    private const float StripTop = PeopleFormRow.LabelTop + PeopleFormRow.LabelHeight + 6f;

    private readonly SettingsFonts _fonts;
    private readonly PeopleRelay _relay;

    public PeopleSegmentTemplate(SettingsFonts fonts, PeopleRelay relay)
    {
        _fonts = fonts;
        _relay = relay;
    }

    public override float Height => PeopleMetrics.SegmentHeight;
    public override float HeightFor(ListingItem item)
        => item is PeopleSegmentItem { Hint.Length: > 0 } ? PeopleMetrics.SegmentHintHeight : Height;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        PeopleFormRow.Card(row);
        var label = PeopleFormRow.Label(row, _fonts);
        var strip = SettingsUI.Child(row, "Strip", new float2(0f, 1f), new float2(1f, 1f),
            new float2(SettingsMetrics.RowInset, -(StripTop + PeopleMetrics.ActionHeight)), new float2(-SettingsMetrics.RowInset, -StripTop));
        var hint = SettingsUI.Label(row, "Hint", _fonts.Body, DashTheme.FontLabel, DashTheme.TextMuted, TextHorizontalAlignment.Left,
            new float2(0f, 0f), new float2(1f, 0f), new float2(SettingsMetrics.RowInset, 6f), new float2(-SettingsMetrics.RowInset, 24f));
        var listingRow = new SegmentRow { Label = label, Hint = hint };
        for (int i = 0; i < MaxOptions; i++)
        {
            var host = SettingsUI.Child(strip, "Option", float2.Zero, float2.One, float2.Zero, float2.Zero);
            var chip = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
            host.ActiveSelf.Value = false;
            _relay.Register(chip.Button, listingRow, i);
            listingRow.Chips.Add(chip);
        }
        return listingRow;
    }

    private sealed class SegmentRow : ListingRow
    {
        public required Text Label;
        public required Text Hint;
        public readonly List<SettingsChip> Chips = new();

        public override void Bind(ListingItem item)
        {
            ListingStyle.SetText(Label, item.Label);
            if (item is not PeopleSegmentItem segment)
                return;
            int count = System.Math.Min(segment.Options.Length, Chips.Count);
            int selected = segment.Read?.Invoke() ?? -1;
            for (int i = 0; i < Chips.Count; i++)
            {
                bool used = i < count;
                PeopleShow.Set(Chips[i].Slot, used);
                if (!used)
                    continue;
                var rect = SettingsUI.Rect(Chips[i].Slot);
                SettingsUI.SetAnchors(rect, new float2(i / (float)count, 0f), new float2((i + 1) / (float)count, 1f));
                SettingsUI.SetOffsets(rect, float2.Zero, new float2(i == count - 1 ? 0f : -SettingsMetrics.SegmentGap, 0f));
                GroupBand.PaintAction(Chips[i], segment.Options[i], true, false, i == selected);
            }
            ListingStyle.SetText(Hint, segment.Hint);
            PeopleShow.Set(Hint.Slot, segment.Hint.Length > 0);
        }
    }
}

// The picture or the banner: what it is now, and the two ways to change it.
internal sealed class PeoplePictureTemplate : ListingRowTemplate
{
    private const float Preview = 56f;
    private const float BannerWidth = 150f;
    private const float ChooseWidth = 190f;
    private const float RemoveWidth = 92f;
    private const float PreviewTop = PeopleFormRow.LabelTop + PeopleFormRow.LabelHeight + 4f;

    private readonly SettingsFonts _fonts;
    private readonly PeopleRelay _relay;
    private readonly PeoplePictureCache _pictures;

    public PeoplePictureTemplate(SettingsFonts fonts, PeopleRelay relay, PeoplePictureCache pictures)
    {
        _fonts = fonts;
        _relay = relay;
        _pictures = pictures;
    }

    public override float Height => PeopleMetrics.PictureHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        PeopleFormRow.Card(row);
        var label = PeopleFormRow.Label(row, _fonts);
        var faceHost = SettingsUI.Child(row, "Face", new float2(0f, 1f), new float2(0f, 1f),
            new float2(SettingsMetrics.RowInset, -(PreviewTop + Preview)), new float2(SettingsMetrics.RowInset + Preview, -PreviewTop));
        var face = PeopleAvatar.Build(faceHost, _fonts.Strong, Preview, 0f, 0f, DashTheme.FontTitle);
        var bannerHost = SettingsUI.Child(row, "Banner", new float2(0f, 1f), new float2(0f, 1f),
            new float2(SettingsMetrics.RowInset, -(PreviewTop + Preview)), new float2(SettingsMetrics.RowInset + BannerWidth, -PreviewTop));
        var banner = PeopleBanner.Build(bannerHost, DashTheme.RadiusControl);
        var status = SettingsUI.Label(row, "Status", _fonts.Body, DashTheme.FontSmall, DashTheme.TextMuted, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(SettingsMetrics.RowInset + Preview + 12f, -(PreviewTop + Preview * 0.5f + 10f)),
            new float2(-(ChooseWidth + RemoveWidth + 30f), -(PreviewTop + Preview * 0.5f - 10f)));
        var listingRow = new PictureRow
        {
            Label = label, FaceHost = faceHost, Face = face, BannerHost = bannerHost, Banner = banner, Status = status,
            StatusRect = SettingsUI.Rect(status.Slot), Pictures = _pictures,
        };
        listingRow.Remove = Pill(row, listingRow, "Remove", 14f, RemoveWidth, 1);
        listingRow.Choose = Pill(row, listingRow, "Choose", 14f + RemoveWidth + PeopleMetrics.PillGap, ChooseWidth, 0);
        return listingRow;
    }

    private SettingsChip Pill(Slot row, ListingRow listingRow, string name, float right, float width, int index)
    {
        var host = SettingsUI.Child(row, name, new float2(1f, 1f), new float2(1f, 1f),
            new float2(-(right + width), -(PreviewTop + Preview * 0.5f + PeopleMetrics.ActionHeight * 0.5f)),
            new float2(-right, -(PreviewTop + Preview * 0.5f - PeopleMetrics.ActionHeight * 0.5f)));
        var chip = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        _relay.Register(chip.Button, listingRow, index);
        return chip;
    }

    private sealed class PictureRow : ListingRow
    {
        public required Text Label;
        public required Slot FaceHost;
        public required PeopleAvatar Face;
        public required Slot BannerHost;
        public required PeopleBanner Banner;
        public required Text Status;
        public required RectTransform StatusRect;
        public required PeoplePictureCache Pictures;
        public SettingsChip Choose = null!;
        public SettingsChip Remove = null!;

        public override void Bind(ListingItem item)
        {
            ListingStyle.SetText(Label, item.Label);
            if (item is not PeoplePictureItem picture)
                return;
            PeopleShow.Set(FaceHost, !picture.Banner);
            PeopleShow.Set(BannerHost, picture.Banner);
            if (picture.Banner)
                Banner.Bind(Pictures, picture.Hash, picture.Tint, picture.Animate);
            else
                Face.Bind(Pictures, picture.Hash, picture.Name, picture.Tint, null, picture.Animate);
            float left = SettingsMetrics.RowInset + (picture.Banner ? BannerWidth : Preview) + 12f;
            var max = StatusRect.OffsetMax.Value;
            SettingsUI.SetOffsets(StatusRect, new float2(left, StatusRect.OffsetMin.Value.y), max);
            ListingStyle.SetText(Status, picture.AnimatedStatus.Length > 0 && Pictures.IsAnimated(picture.Hash)
                ? picture.AnimatedStatus : picture.Status);
            GroupBand.PaintAction(Choose, PeopleLocale.ChooseFromInventory.Resolve(), true, false, true);
            bool has = !string.IsNullOrEmpty(picture.Hash);
            GroupBand.PaintAction(Remove, PeopleLocale.Remove.Resolve(), has, false, false);
        }
    }
}

// Up to five languages as chips you press to drop, and Add.
internal sealed class PeopleLanguagesTemplate : ListingRowTemplate
{
    private const int MaxChips = 5;
    private const float ChipWidth = 64f;
    private const float StripTop = PeopleFormRow.LabelTop + PeopleFormRow.LabelHeight + 6f;

    private readonly SettingsFonts _fonts;
    private readonly PeopleRelay _relay;

    public PeopleLanguagesTemplate(SettingsFonts fonts, PeopleRelay relay)
    {
        _fonts = fonts;
        _relay = relay;
    }

    public override float Height => PeopleMetrics.SegmentHintHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        PeopleFormRow.Card(row);
        var label = PeopleFormRow.Label(row, _fonts);
        var hint = SettingsUI.Label(row, "Hint", _fonts.Body, DashTheme.FontLabel, DashTheme.TextMuted, TextHorizontalAlignment.Left,
            new float2(0f, 0f), new float2(1f, 0f), new float2(SettingsMetrics.RowInset, 6f), new float2(-SettingsMetrics.RowInset, 24f));
        var listingRow = new LanguagesRow { Label = label, Hint = hint };
        for (int i = 0; i <= MaxChips; i++)
        {
            var host = SettingsUI.Child(row, i < MaxChips ? "Language" : "Add", new float2(0f, 1f), new float2(0f, 1f),
                new float2(0f, -(StripTop + PeopleMetrics.ActionHeight)), new float2(ChipWidth, -StripTop));
            var chip = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
            host.ActiveSelf.Value = false;
            _relay.Register(chip.Button, listingRow, i);
            listingRow.Chips.Add(chip);
        }
        return listingRow;
    }

    private sealed class LanguagesRow : ListingRow
    {
        public required Text Label;
        public required Text Hint;
        public readonly List<SettingsChip> Chips = new();

        public override void Bind(ListingItem item)
        {
            ListingStyle.SetText(Label, item.Label);
            if (item is not PeopleLanguagesItem languages)
                return;
            float cursor = SettingsMetrics.RowInset;
            for (int i = 0; i < MaxChips; i++)
            {
                bool used = i < languages.Codes.Count;
                PeopleShow.Set(Chips[i].Slot, used);
                if (!used)
                    continue;
                Place(Chips[i], cursor, ChipWidth);
                cursor += ChipWidth + PeopleMetrics.PillGap;
                GroupBand.PaintAction(Chips[i], languages.Codes[i].ToUpperInvariant() + "  ×", true, false, false);
            }
            var add = Chips[MaxChips];
            bool room = languages.Codes.Count < MaxChips;
            PeopleShow.Set(add.Slot, room);
            if (room)
            {
                Place(add, cursor, 80f);
                GroupBand.PaintAction(add, "+ " + PeopleLocale.AddLanguage.Resolve(), true, false, true);
            }
            ListingStyle.SetText(Hint, languages.Hint);
            PeopleShow.Set(Hint.Slot, languages.Hint.Length > 0);
        }

        private static void Place(SettingsChip chip, float left, float width)
        {
            var rect = SettingsUI.Rect(chip.Slot);
            var min = rect.OffsetMin.Value;
            var max = rect.OffsetMax.Value;
            SettingsUI.SetOffsets(rect, new float2(left, min.y), new float2(left + width, max.y));
        }
    }
}

// A value picked from a long list (the time zone), with the pills that change it.
internal sealed class PeopleChoiceTemplate : ListingRowTemplate
{
    private const float PrimaryWidth = 104f;
    private const float SecondaryWidth = 176f;
    private const float BandTop = PeopleFormRow.LabelTop + PeopleFormRow.LabelHeight + 6f;

    private readonly SettingsFonts _fonts;
    private readonly PeopleRelay _relay;

    public PeopleChoiceTemplate(SettingsFonts fonts, PeopleRelay relay)
    {
        _fonts = fonts;
        _relay = relay;
    }

    public override float Height => PeopleMetrics.SegmentHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        PeopleFormRow.Card(row);
        var label = PeopleFormRow.Label(row, _fonts);
        float right = 14f + PrimaryWidth + SecondaryWidth + 2f * PeopleMetrics.PillGap;
        var value = SettingsUI.Label(row, "Value", _fonts.Body, DashTheme.FontBody, DashTheme.Text, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(SettingsMetrics.RowInset, -(BandTop + PeopleMetrics.ActionHeight)),
            new float2(-right, -BandTop), shrinkToFit: true);
        var listingRow = new ChoiceRow { Label = label, Value = value };
        listingRow.Primary = Pill(row, listingRow, "Primary", 14f + SecondaryWidth + PeopleMetrics.PillGap, PrimaryWidth, 0);
        listingRow.Secondary = Pill(row, listingRow, "Second", 14f, SecondaryWidth, 1);
        return listingRow;
    }

    private SettingsChip Pill(Slot row, ListingRow listingRow, string name, float right, float width, int index)
    {
        var host = SettingsUI.Child(row, name, new float2(1f, 1f), new float2(1f, 1f),
            new float2(-(right + width), -(BandTop + PeopleMetrics.ActionHeight)), new float2(-right, -BandTop));
        var chip = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        _relay.Register(chip.Button, listingRow, index);
        return chip;
    }

    private sealed class ChoiceRow : ListingRow
    {
        public required Text Label;
        public required Text Value;
        public SettingsChip Primary = null!;
        public SettingsChip Secondary = null!;

        public override void Bind(ListingItem item)
        {
            ListingStyle.SetText(Label, item.Label);
            if (item is not PeopleChoiceItem choice)
                return;
            ListingStyle.SetText(Value, choice.Value);
            PeopleShow.Set(Primary.Slot, choice.PrimaryLabel.Length > 0);
            if (choice.PrimaryLabel.Length > 0)
                GroupBand.PaintAction(Primary, choice.PrimaryLabel, true, false, true);
            PeopleShow.Set(Secondary.Slot, choice.SecondaryLabel.Length > 0);
            if (choice.SecondaryLabel.Length > 0)
                GroupBand.PaintAction(Secondary, choice.SecondaryLabel, true, false, false);
        }
    }
}
