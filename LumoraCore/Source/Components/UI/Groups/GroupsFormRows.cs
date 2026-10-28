// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// The rows a group's pages are edited with: typed values, a choice of a few, a row of pills, and the
// storage pool and the owner's share of it.

// TYPED VALUES
//
// One card, a label over each field, one or two lines of them, and the pill that sends them on the last line.
// Typing goes straight to the screen's buffer, so a pill pressed mid-word sends what is in the field; Enter
// in any field is the same as the pill.
internal sealed class GroupFormTemplate : ListingRowTemplate
{
    private const int MaxCells = 4;
    private const float Inset = 14f;
    private const float Gap = 8f;
    private const float LabelTop = 8f;
    private const float LabelHeight = 18f;
    private const float FieldTop = LabelTop + LabelHeight + 2f;

    private readonly SettingsFonts _fonts;
    private readonly GroupsRelay _relay;

    public GroupFormTemplate(SettingsFonts fonts, GroupsRelay relay)
    {
        _fonts = fonts;
        _relay = relay;
    }

    private const float CompactTop = 10f;

    public static float HeightOf(GroupFormItem form)
        => form.Multiline ? FieldTop + GroupMetrics.FormBio + 10f
            : form.Compact ? CompactTop + GroupMetrics.FormField + 10f
            : form.Lines * GroupMetrics.FormLine + 10f;

    public override float Height => GroupMetrics.FormLine + 10f;
    public override float HeightFor(ListingItem item) => item is GroupFormItem form ? HeightOf(form) : Height;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        SettingsUI.Panel(row, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusCard);
        var listingRow = new FormRow();
        for (int i = 0; i < MaxCells; i++)
        {
            int index = i;
            var label = SettingsUI.Label(row, "Label" + i, _fonts.Medium, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Left,
                new float2(0f, 1f), new float2(0f, 1f), float2.Zero, float2.Zero);
            var host = SettingsUI.Child(row, "Field" + i, new float2(0f, 1f), new float2(0f, 1f), float2.Zero, float2.Zero);
            var input = DashTextField.Attach(host, _fonts.Body, DashTheme.FontBody, string.Empty, 64);
            input.EditingChanged += (_, text) => listingRow.Write(index, text);
            input.EditingFinished += (_, text) => listingRow.Enter(index, text);
            input.FocusLost += (_, _) => listingRow.EndEdit();
            label.Slot.ActiveSelf.Value = false;
            host.ActiveSelf.Value = false;
            listingRow.Cells.Add(new FormRow.CellParts
            {
                Label = label,
                LabelRect = SettingsUI.Rect(label.Slot),
                Host = host,
                HostRect = SettingsUI.Rect(host),
                Input = input,
                Text = host.FindChild("Text")?.GetComponent<Text>(),
            });
        }
        var pillHost = SettingsUI.Child(row, "Pill", new float2(1f, 1f), new float2(1f, 1f), float2.Zero, float2.Zero);
        listingRow.Pill = SettingsChip.Build(pillHost, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        _relay.Register(listingRow.Pill.Button, listingRow, 0);
        pillHost.ActiveSelf.Value = false;
        return listingRow;
    }

    private sealed class FormRow : ListingRow
    {
        internal sealed class CellParts
        {
            public Text Label = null!;
            public RectTransform LabelRect = null!;
            public Slot Host = null!;
            public RectTransform HostRect = null!;
            public TextInput Input = null!;
            public Text? Text;
        }

        public readonly List<CellParts> Cells = new();
        public SettingsChip Pill = null!;

        public void Write(int index, string text)
        {
            if (Item is GroupFormItem form && index < form.Cells.Count)
                form.Cells[index].Write?.Invoke(text ?? string.Empty);
        }

        public void Enter(int index, string text)
        {
            Write(index, text);
            if (Item is GroupFormItem form)
                form.Submit?.Invoke();
        }

        public void EndEdit()
        {
            if (Item != null)
                Bind(Item);
        }

        public override void Bind(ListingItem item)
        {
            if (item is not GroupFormItem form)
                return;
            int count = System.Math.Min(form.Cells.Count, Cells.Count);
            int lines = form.Lines;
            bool pill = form.PillLabel.Length > 0;
            float fieldTop = form.Compact ? CompactTop : FieldTop;
            for (int line = 0; line < lines; line++)
            {
                bool last = line == lines - 1;
                float reserved = 2f * Inset + (pill && last ? GroupMetrics.FormPill + Gap : 0f);
                float total = 0f;
                int onLine = 0;
                for (int i = 0; i < count; i++)
                {
                    if (form.Cells[i].Line != line)
                        continue;
                    total += System.Math.Max(0.01f, form.Cells[i].Weight);
                    onLine++;
                }
                if (onLine == 0)
                    continue;
                reserved += Gap * (onLine - 1);
                float top = line * GroupMetrics.FormLine;
                float at = 0f;
                int slot = 0;
                for (int i = 0; i < count; i++)
                {
                    var cell = form.Cells[i];
                    if (cell.Line != line)
                        continue;
                    float from = at / total;
                    at += System.Math.Max(0.01f, cell.Weight);
                    float to = at / total;
                    float left = Inset + slot * Gap;
                    slot++;
                    PlaceCell(Cells[i], cell, form.Multiline, from, to, left, reserved, top, fieldTop);
                }
            }
            for (int i = count; i < Cells.Count; i++)
            {
                PeopleShow.Set(Cells[i].Label.Slot, false);
                PeopleShow.Set(Cells[i].Host, false);
            }

            PeopleShow.Set(Pill.Slot, pill);
            if (pill)
            {
                float top = (lines - 1) * GroupMetrics.FormLine;
                SettingsUI.SetOffsets(SettingsUI.Rect(Pill.Slot), new float2(-(Inset + GroupMetrics.FormPill), -(top + fieldTop + GroupMetrics.FormField)),
                    new float2(-Inset, -(top + fieldTop)));
                GroupBand.PaintAction(Pill, form.PillLabel, true, false, true);
            }
        }

        private static void PlaceCell(CellParts parts, GroupFormItem.Cell cell, bool multiline, float from, float to, float left,
            float reserved, float top, float fieldTop)
        {
            PeopleShow.Set(parts.Label.Slot, cell.Label.Length > 0);
            PeopleShow.Set(parts.Host, true);
            float x0 = left - from * reserved;
            float x1 = left - to * reserved;
            SettingsUI.SetAnchors(parts.LabelRect, new float2(from, 1f), new float2(to, 1f));
            SettingsUI.SetOffsets(parts.LabelRect, new float2(x0, -(top + LabelTop + LabelHeight)), new float2(x1, -(top + LabelTop)));
            if (multiline)
            {
                SettingsUI.SetAnchors(parts.HostRect, new float2(from, 0f), new float2(to, 1f));
                SettingsUI.SetOffsets(parts.HostRect, new float2(x0, 10f), new float2(x1, -(top + fieldTop)));
            }
            else
            {
                SettingsUI.SetAnchors(parts.HostRect, new float2(from, 1f), new float2(to, 1f));
                SettingsUI.SetOffsets(parts.HostRect, new float2(x0, -(top + fieldTop + GroupMetrics.FormField)), new float2(x1, -(top + fieldTop)));
            }
            ListingStyle.SetText(parts.Label, cell.Label);

            var input = parts.Input;
            if (input.Multiline.Value != multiline)
                input.Multiline.Value = multiline;
            if (parts.Text != null)
            {
                if (parts.Text.WordWrap.Value != multiline)
                    parts.Text.WordWrap.Value = multiline;
                var align = multiline ? TextVerticalAlignment.Top : TextVerticalAlignment.Middle;
                if (parts.Text.VerticalAlignment.Value != align)
                    parts.Text.VerticalAlignment.Value = align;
                SettingsUI.SetOffsets(SettingsUI.Rect(parts.Text.Slot), new float2(DashTextField.TextInset, multiline ? 8f : 0f),
                    new float2(-DashTextField.TextInset, multiline ? -8f : 0f));
            }
            if (!string.Equals(input.Placeholder.Value, cell.Placeholder, System.StringComparison.Ordinal))
                input.Placeholder.Value = cell.Placeholder;
            if (input.MaxLength.Value != cell.MaxLength)
                input.MaxLength.Value = cell.MaxLength;
            if (input.IsFocused)
                return;
            string value = cell.Read?.Invoke() ?? string.Empty;
            if (!string.Equals(input.Text.Value, value, System.StringComparison.Ordinal))
                input.Text.Value = value;
        }

        public override void Unbind()
        {
            for (int i = 0; i < Cells.Count; i++)
            {
                if (Cells[i].Input.IsFocused)
                    Cells[i].Input.Unfocus();
            }
        }
    }
}

// THE ICON OR THE BANNER
//
// The label with where it stands on one line, then the picture as it is now beside the two ways to change it:
// a square for the icon, a strip for the banner, so each previews in the shape it is drawn in.
internal sealed class GroupPictureTemplate : ListingRowTemplate
{
    private const float ChooseWidth = 190f;
    private const float RemoveWidth = 92f;
    private const float PreviewTop = 8f + 18f + 6f;

    private readonly SettingsFonts _fonts;
    private readonly GroupsRelay _relay;
    private readonly PeoplePictureCache _pictures;

    public GroupPictureTemplate(SettingsFonts fonts, GroupsRelay relay, PeoplePictureCache pictures)
    {
        _fonts = fonts;
        _relay = relay;
        _pictures = pictures;
    }

    public override float Height => GroupMetrics.PictureHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        SettingsUI.Panel(row, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusCard);
        var label = SettingsUI.Label(row, "Label", _fonts.Medium, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(0.5f, 1f), new float2(14f, -26f), new float2(0f, -8f));
        var status = SettingsUI.Label(row, "Status", _fonts.Body, DashTheme.FontSmall, DashTheme.TextMuted, TextHorizontalAlignment.Right,
            new float2(0.5f, 1f), new float2(1f, 1f), new float2(8f, -26f), new float2(-14f, -8f), shrinkToFit: true);

        float size = GroupMetrics.PicturePreview;
        var markHost = SettingsUI.Child(row, "Icon", new float2(0f, 1f), new float2(0f, 1f),
            new float2(14f, -(PreviewTop + size)), new float2(14f + size, -PreviewTop));
        var mark = GroupMark.Build(markHost, _fonts.Strong, DashTheme.RadiusControl, DashTheme.FontHeading);
        var bannerHost = SettingsUI.Child(row, "Banner", new float2(0f, 1f), new float2(0f, 1f),
            new float2(14f, -(PreviewTop + size)), new float2(14f + GroupMetrics.BannerPreview, -PreviewTop));
        var banner = PeopleBanner.Build(bannerHost, DashTheme.RadiusControl);

        var listingRow = new PictureRow
        {
            Label = label, Status = status, MarkHost = markHost, Mark = mark, BannerHost = bannerHost, Banner = banner,
            Pictures = _pictures,
        };
        listingRow.Remove = Pill(row, listingRow, "Remove", 14f, RemoveWidth, 1);
        listingRow.Choose = Pill(row, listingRow, "Choose", 14f + RemoveWidth + GroupMetrics.PillGap, ChooseWidth, 0);
        return listingRow;
    }

    private SettingsChip Pill(Slot row, ListingRow listingRow, string name, float right, float width, int index)
    {
        float centre = PreviewTop + GroupMetrics.PicturePreview * 0.5f;
        var host = SettingsUI.Child(row, name, new float2(1f, 1f), new float2(1f, 1f),
            new float2(-(right + width), -(centre + GroupMetrics.ActionHeight * 0.5f)), new float2(-right, -(centre - GroupMetrics.ActionHeight * 0.5f)));
        var chip = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        _relay.Register(chip.Button, listingRow, index);
        return chip;
    }

    private sealed class PictureRow : ListingRow
    {
        public required Text Label;
        public required Text Status;
        public required Slot MarkHost;
        public required GroupMark Mark;
        public required Slot BannerHost;
        public required PeopleBanner Banner;
        public required PeoplePictureCache Pictures;
        public SettingsChip Choose = null!;
        public SettingsChip Remove = null!;

        public override void Bind(ListingItem item)
        {
            ListingStyle.SetText(Label, item.Label);
            if (item is not GroupPictureItem picture)
                return;
            PeopleShow.Set(MarkHost, !picture.Banner);
            PeopleShow.Set(BannerHost, picture.Banner);
            if (picture.Banner)
                Banner.Bind(Pictures, picture.Hash, picture.Tint);
            else
                Mark.Bind(Pictures, picture.Hash, picture.GroupTag, picture.Name, picture.Tint);
            ListingStyle.SetText(Status, picture.Status);
            GroupBand.PaintAction(Choose, PeopleLocale.ChooseFromInventory.Resolve(), true, false, true);
            GroupBand.PaintAction(Remove, PeopleLocale.Remove.Resolve(), !string.IsNullOrEmpty(picture.Hash), false, false);
        }
    }
}

// ONE OF A FEW: who may join.
internal sealed class GroupSegmentTemplate : ListingRowTemplate
{
    private const int MaxOptions = 4;
    private const float StripTop = 8f + 18f + 6f;

    private readonly SettingsFonts _fonts;
    private readonly GroupsRelay _relay;

    public GroupSegmentTemplate(SettingsFonts fonts, GroupsRelay relay)
    {
        _fonts = fonts;
        _relay = relay;
    }

    public override float Height => GroupMetrics.SegmentHeight + 4f;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        SettingsUI.Panel(row, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusCard);
        var label = SettingsUI.Label(row, "Label", _fonts.Medium, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(14f, -26f), new float2(-14f, -8f));
        var strip = SettingsUI.Child(row, "Strip", new float2(0f, 1f), new float2(1f, 1f),
            new float2(14f, -(StripTop + GroupMetrics.ActionHeight)), new float2(-14f, -StripTop));
        var listingRow = new SegmentRow { Label = label };
        for (int i = 0; i < MaxOptions; i++)
        {
            var host = SettingsUI.Child(strip, "Option" + i, float2.Zero, float2.One, float2.Zero, float2.Zero);
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
        public readonly List<SettingsChip> Chips = new();

        public override void Bind(ListingItem item)
        {
            ListingStyle.SetText(Label, item.Label);
            if (item is not GroupSegmentItem segment)
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
        }
    }
}

// A ROW OF PILLS
internal sealed class GroupActionsTemplate : ListingRowTemplate
{
    private const int MaxButtons = 4;

    private readonly SettingsFonts _fonts;
    private readonly GroupsRelay _relay;

    public GroupActionsTemplate(SettingsFonts fonts, GroupsRelay relay)
    {
        _fonts = fonts;
        _relay = relay;
    }

    public override float Height => GroupMetrics.ActionsHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var listingRow = new ActionsRow();
        for (int i = 0; i < MaxButtons; i++)
        {
            var host = GroupBand.Left(row, "Action" + i, 0f, GroupMetrics.ActionWidth, GroupMetrics.ActionHeight);
            var chip = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
            host.ActiveSelf.Value = false;
            _relay.Register(chip.Button, listingRow, i);
            listingRow.Chips.Add(chip);
        }
        return listingRow;
    }

    private sealed class ActionsRow : ListingRow
    {
        public readonly List<SettingsChip> Chips = new();

        public override void Bind(ListingItem item)
        {
            var actions = item as GroupActionsItem;
            int count = actions?.Buttons.Count ?? 0;
            float cursor = 0f;
            for (int i = 0; i < Chips.Count; i++)
            {
                bool used = i < count;
                PeopleShow.Set(Chips[i].Slot, used);
                if (!used)
                    continue;
                var entry = actions!.Buttons[i];
                SettingsUI.SetOffsets(SettingsUI.Rect(Chips[i].Slot), new float2(cursor, -GroupMetrics.ActionHeight * 0.5f),
                    new float2(cursor + entry.Width, GroupMetrics.ActionHeight * 0.5f));
                cursor += entry.Width + GroupMetrics.PillGap;
                GroupBand.PaintAction(Chips[i], entry.Label, true, entry.Danger, entry.Lit);
            }
        }
    }
}

// THE POOL
//
// One bar, two fills. The dark one is what the group has actually stored and the lighter one behind it is
// what its members have been promised, because those are two different facts and a single bar can only
// ever tell you one of them. -xlinka
internal sealed class GroupStorageTemplate : ListingRowTemplate
{
    private readonly SettingsFonts _fonts;

    public GroupStorageTemplate(SettingsFonts fonts) => _fonts = fonts;

    public override float Height => GroupMetrics.StorageHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        SettingsUI.Panel(row, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusCard);
        var caption = SettingsUI.Label(row, "Caption", _fonts.Body, DashTheme.FontSmall, DashTheme.TextDim,
            TextHorizontalAlignment.Left, new float2(0f, 1f), new float2(1f, 1f),
            new float2(14f, -28f), new float2(-14f, -8f), shrinkToFit: true);

        var track = SettingsUI.Child(row, "Track", new float2(0f, 0f), new float2(1f, 0f), new float2(14f, 13f), new float2(-14f, 23f));
        SettingsUI.Panel(track, DashTheme.Field, color.Transparent, 5f);
        var allocatedSlot = SettingsUI.Child(track, "Allocated", float2.Zero, new float2(0f, 1f), float2.Zero, float2.Zero);
        var allocated = SettingsUI.Panel(allocatedSlot, DashTheme.AccentSoft, color.Transparent, 5f);
        var usedSlot = SettingsUI.Child(track, "Used", float2.Zero, new float2(0f, 1f), float2.Zero, float2.Zero);
        var used = SettingsUI.Panel(usedSlot, DashTheme.Accent, color.Transparent, 5f);

        return new StorageRow
        {
            Caption = caption,
            AllocatedRect = SettingsUI.Rect(allocatedSlot),
            Allocated = allocated,
            UsedRect = SettingsUI.Rect(usedSlot),
            Used = used,
        };
    }

    private sealed class StorageRow : ListingRow
    {
        public required Text Caption;
        public required RectTransform AllocatedRect;
        public required RoundedPanel Allocated;
        public required RectTransform UsedRect;
        public required RoundedPanel Used;

        public override void Bind(ListingItem item)
        {
            ListingStyle.SetText(Caption, item.Label);
            if (item is not GroupStorageItem storage)
                return;
            float quota = storage.Quota > 0 ? storage.Quota : 0f;
            float usedFraction = quota > 0f ? System.Math.Clamp(storage.Used / quota, 0f, 1f) : 0f;
            float allocFraction = quota > 0f ? System.Math.Clamp(storage.Allocated / quota, 0f, 1f) : 0f;
            SettingsUI.SetAnchors(UsedRect, float2.Zero, new float2(usedFraction, 1f));
            SettingsUI.SetAnchors(AllocatedRect, float2.Zero, new float2(allocFraction, 1f));
            SettingsUI.SetPaint(Used, quota > 0f ? DashTheme.Accent : DashTheme.Field, color.Transparent);
            SettingsUI.SetPaint(Allocated, DashTheme.AccentSoft, color.Transparent);
        }
    }
}

// THE OWNER'S SHARE
//
// What this group gets out of the owner's one pool: the share and its value on top, the slider across, and
// what its members already hold under it, since that is the floor. Same rhythm as a member's slice: the drag
// moves the number, the release sends one request. -xlinka
internal sealed class GroupShareTemplate : ListingRowTemplate
{
    private const float ValueWidth = 96f;

    private readonly SettingsFonts _fonts;
    private readonly GroupsScreen _screen;

    public GroupShareTemplate(SettingsFonts fonts, GroupsScreen screen)
    {
        _fonts = fonts;
        _screen = screen;
    }

    public override float Height => GroupMetrics.ShareHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        SettingsUI.Panel(row, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusCard);
        var caption = SettingsUI.Label(row, "Caption", _fonts.Medium, DashTheme.FontBody, DashTheme.Text,
            TextHorizontalAlignment.Left, new float2(0f, 1f), new float2(1f, 1f),
            new float2(14f, -30f), new float2(-(14f + ValueWidth + 8f), -8f), shrinkToFit: true);
        var value = SettingsUI.Label(row, "ShareValue", _fonts.Medium, DashTheme.FontBody, DashTheme.Accent,
            TextHorizontalAlignment.Right, new float2(1f, 1f), new float2(1f, 1f),
            new float2(-(14f + ValueWidth), -30f), new float2(-14f, -8f));

        var sliderHost = SettingsUI.Child(row, "Share", new float2(0f, 1f), new float2(1f, 1f), new float2(14f, -60f), new float2(-14f, -36f));
        builder.PushStyle();
        builder.ForegroundColor(DashTheme.Accent);
        builder.NestInto(sliderHost);
        var slider = builder.Slider(0f, 0f, 1f, null, DashTheme.Field);
        builder.NestOut();
        builder.PopStyle();
        var sliderRect = SettingsUI.Rect(slider.Slot);
        SettingsUI.SetAnchors(sliderRect, float2.Zero, float2.One);
        SettingsUI.SetOffsets(sliderRect, float2.Zero, float2.Zero);

        var handed = SettingsUI.Label(row, "Handed", _fonts.Body, DashTheme.FontLabel, DashTheme.TextMuted,
            TextHorizontalAlignment.Left, new float2(0f, 0f), new float2(1f, 0f), new float2(14f, 8f), new float2(-14f, 25f));

        var listingRow = new ShareRow { Caption = caption, Value = value, Bar = slider, Handed = handed };
        var screen = _screen;
        slider.ValueChanged += (_, raw) =>
        {
            if (listingRow.Item is GroupShareItem)
                screen.PreviewShare(raw);
        };
        slider.Released += _ =>
        {
            if (listingRow.Item is GroupShareItem)
                screen.CommitShare(slider.Value.Value);
        };
        return listingRow;
    }

    private sealed class ShareRow : ListingRow
    {
        public required Text Caption;
        public required Text Value;
        public required Slider Bar;
        public required Text Handed;

        public override void Bind(ListingItem item)
        {
            ListingStyle.SetText(Caption, item.Label);
            if (item is not GroupShareItem share)
                return;
            long max = System.Math.Max(share.Min, share.Max);
            long pending = System.Math.Clamp(share.Pending?.Invoke() ?? share.Min, share.Min, max);
            ListingStyle.SetSlider(Bar, share.Min, System.Math.Max(share.Min + 1f, max), pending);
            ListingStyle.SetText(Value, GroupsFormat.Bytes(pending));
            ListingStyle.SetText(Handed, GroupsLocale.HandedOut(GroupsFormat.Bytes(share.Sliced)).Resolve());
        }
    }
}
