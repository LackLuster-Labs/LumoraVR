// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Helio.UI;
using Lumora.Core;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Math;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.UI;
using Lumora.Core.Localization;

namespace Lumora.Core.Components;

public static class InspectorUI
{
    // Metres per canvas unit for the inspector and every panel it opens. Was 0.0005, which put body text at
    // 7.5 mm: crisp, but past arm's length on a desktop view it was too small to read. Neighbour offsets are
    // in canvas units times this, so the layout grows with it. -xlinka
    public const float PanelScale = 0.00065f;
    public static readonly color TextColor = DashTheme.Text;
    public static readonly color MutedColor = new color(0.78f, 0.77f, 0.85f, 1f);
    // slot's own persistence flag is off
    public static readonly color NonPersistentColor = new color(0.95f, 0.58f, 0.20f, 1f);
    // non-persistent only through an ancestor
    public static readonly color NonPersistentInheritedColor = new color(0.78f, 0.56f, 0.34f, 1f);
    public static readonly color DrivenColor = new color(0.85f, 0.45f, 0.9f, 1f);
    // Linked but not driving (a hook/link holds the field without writing it) - reads cyan.
    public static readonly color LinkedColor = new color(0.38f, 0.78f, 0.85f, 1f);
    // a member the editor can no longer resolve (destroyed or never wired)
    public static readonly color BrokenColor = new color(0.50f, 0.50f, 0.52f, 1f);
    // Section headers/dividers in inspector panels.
    public static readonly color CyanColor = new color(0.35f, 0.75f, 0.90f, 1f);
    public static readonly color RowColor = DashTheme.Field;
    public static readonly color HeaderColor = DashTheme.Surface;
    // opaque near-black backing for the hierarchy and detail panes
    public static readonly color PaneColor = new color(DashTheme.Panel.r, DashTheme.Panel.g, DashTheme.Panel.b, 0.75f);
    public static readonly color AccentColor = DashTheme.Accent;
    // row backing for the selected hierarchy entry
    public static readonly color SelectionColor = new color(0.45f, 0.38f, 0.80f, 0.60f);
    public static readonly color DangerColor = new color(0.70f, 0.24f, 0.28f, 1f);
    public static readonly color AxisXColor = new color(0.90f, 0.30f, 0.32f, 1f);
    public static readonly color AxisYColor = new color(0.36f, 0.80f, 0.42f, 1f);
    public static readonly color AxisZColor = new color(0.35f, 0.55f, 0.95f, 1f);
    public const float RowHeight = 34f;
    public const float FontSize = 17f;

    // A member row left to right: chip, actions button, label, then the editor in what is left. A vector's
    // fields split the editor with ColumnSpacing between them. Rows that have to line up with member rows
    // (the axis legend, the reset buttons) take their lead from MemberLead, so they match at any width.
    public const float ChipWidth = 14f;
    public const float ActionsWidth = 20f;
    public const float LabelMinWidth = 150f;
    public const float LabelWidth = 190f;
    public const float ColumnSpacing = 4f;

    // The name a member reads as on a card, a ref readout or a list row. A collection element has
    // no name of its own (its member name is its type), so the owning worker gets first say through
    // ICustomMemberNameSource (a blend shape weight names itself after the mesh's shape), then a
    // list element falls back to "List[i]", then anything else to the member name. -xlinka
    public static string MemberDisplayName(ISyncMember member)
    {
        if (member is SyncElement element)
        {
            IWorldElement? node = element.Parent;
            while (node is SyncElement parentElement)
                node = parentElement.Parent;
            if (node is ICustomMemberNameSource source && source.GetMemberName(member) is { Length: > 0 } custom)
                return custom;

            if (element.Parent is ISyncList list && element.Parent is ISyncMember listMember)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (ReferenceEquals(list.GetElement(i), member))
                        return $"{listMember.Name}[{i}]";
                }
            }
        }
        return string.IsNullOrEmpty(member.Name) ? member.GetType().Name : member.Name!;
    }

    // THE field-state color rule, one implementation for every editor and every widget in a member
    // row: a member that is gone reads broken gray, a driven one the drive magenta, one that is
    // merely linked (something holds it but nothing writes it) cyan, anything else the caller's
    // normal color. Keep new editors on this instead of hand-rolling a tint, or the row's chip, its
    // label and its value widget end up disagreeing about what state the field is in. -xlinka
    public static color FieldStateColor(IWorldElement? member, color? normal = null)
    {
        // Broken means the MEMBER ITSELF is gone. A member kind that simply has no link machinery
        // (a delegate, a bag) is normal, not broken.
        if (member == null || member.IsDestroyed)
            return BrokenColor;
        if (member is not ILinkable linkable)
            return normal ?? TextColor;
        if (linkable.IsDriven)
            return DrivenColor;
        if (linkable.IsLinked)
            return LinkedColor;
        return normal ?? TextColor;
    }

    // Paint a widget's backing with a field-state color.
    // Helio buttons DRIVE their own Image.Tint from an interaction ColorDriver (normal/hover/pressed),
    // so writing the tint directly is silently reverted on the driver's next pass - the base color has
    // to be retuned on the DRIVER instead, which also re-derives the hover and pressed shades from it.
    // A plain image (a text input backing) has no driver and takes the write. Anything tinting an
    // inspector widget must go through here or it will look like it worked and then flicker back on
    // the first hover. -xlinka
    public static void ApplyStateTint(IField<color>? widgetTint, in color value)
    {
        if (widgetTint == null || widgetTint.IsDestroyed)
            return;

        // The color field's parent is the graphic component, whose slot carries any interaction
        // ColorDriver bound to it. Every Helio Button installs one on its own backing in OnAttach.
        var slot = ((widgetTint as SyncElement)?.Parent as Component)?.Slot;
        if (slot != null)
        {
            foreach (var driver in slot.GetComponents<ColorDriver>())
            {
                if (!ReferenceEquals(driver.Target.Target, widgetTint))
                    continue;
                driver.SetColors(value);
                return;
            }
        }
        widgetTint.Value = value;
    }

    // text without a font renders nothing - the first inspector shipped with invisible content this way
    public static Lumora.Core.Components.UI.UITheme? ApplyTheme(UIBuilder ui, Slot context)
    {
        ui.FontSize(FontSize).CornerRadius(DashTheme.RadiusControl);
        var theme = context.GetComponentInParent<Lumora.Core.Components.UI.UITheme>();
        if (theme != null)
        {
            if (theme.ThemeFont != null)
                ui.Font(theme.ThemeFont);
            ui.TextColor(theme.TextPrimary.Value);
            ui.BackgroundColor(theme.ButtonFill.Value);
            ui.RoundedSprite(null);
        }
        return theme;
    }

    public static TextInput CreateTextInput(UIBuilder ui, string name = "Input",
        TextHorizontalAlignment alignment = TextHorizontalAlignment.Left)
    {
        var slot = ui.Next(name);
        ui.NestInto(slot);
        var image = slot.AttachComponent<Image>();
        image.Tint.Value = RowColor;
        image.CornerRadius.Value = DashTheme.RadiusControl;
        var input = slot.AttachComponent<TextInput>();

        var textSlot = slot.AddSlot("Text");
        FillParent(textSlot.AttachComponent<RectTransform>());
        textSlot.GetComponent<RectTransform>()!.OffsetMin.Value = new float2(8f, 2f);
        textSlot.GetComponent<RectTransform>()!.OffsetMax.Value = new float2(-8f, -2f);
        var text = textSlot.AttachComponent<Text>();
        text.Size.Value = FontSize;
        text.Color.Value = TextColor;
        text.HorizontalAlignment.Value = alignment;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        text.WordWrap.Value = false;
        text.ClipToRect.Value = true;
        text.KeepCaretVisible.Value = true;
        var theme = slot.GetComponentInParent<Lumora.Core.Components.UI.UITheme>();
        if (theme?.ThemeFont != null)
            text.Font.Target = theme.ThemeFont;

        ui.NestOut();
        return input;
    }

    // Returns the header ROW so a caller can hang a control off the right-hand end of it - the label
    // takes the flexible width, so anything appended after lands hard right. -xlinka
    public static Slot SectionHeader(Slot parent, string label, Slot themeContext)
    {
        var row = FixedRow(parent, "Section", RowHeight, out var ui, themeContext);
        ui.PushStyle();
        ui.FlexibleWidth(1f);
        var text = ui.Text(label, FontSize - 1f, CyanColor);
        FillParent(text.RectTransform!);
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        text.VerticalAlignment.Value = TextVerticalAlignment.Bottom;
        ui.PopStyle();

        var divider = parent.AddSlot("Divider");
        divider.AttachComponent<RectTransform>();
        var dividerLE = divider.AttachComponent<Helio.UI.Layout.LayoutElement>();
        dividerLE.MinHeight.Value = 4f;
        dividerLE.PreferredHeight.Value = 4f;
        var rule = divider.AttachComponent<Image>();
        rule.Tint.Value = new color(CyanColor.r, CyanColor.g, CyanColor.b, 0.85f);
        return row;
    }

    // a bare RectTransform defaults to a 100x100 centered chunk
    public static void FillParent(RectTransform rect)
    {
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;
        rect.OffsetMin.Value = float2.Zero;
        rect.OffsetMax.Value = float2.Zero;
    }

    public static Slot FixedRow(Slot parent, string name, float height, out UIBuilder rowUi, Slot themeContext)
    {
        var row = parent.AddSlot(name);
        row.AttachComponent<RectTransform>();
        var le = row.AttachComponent<Helio.UI.Layout.LayoutElement>();
        le.MinHeight.Value = height;
        le.PreferredHeight.Value = height;
        var layout = row.AttachComponent<Helio.UI.Layout.HorizontalLayout>();
        layout.Spacing.Value = 6f;
        layout.PaddingLeft.Value = 4f;
        layout.PaddingRight.Value = 4f;
        layout.ForceExpandHeight.Value = true;
        layout.ForceExpandWidth.Value = false;
        // Own graphics chunk per row: a hover tint or text change re-tessellates ONE ROW's mesh
        // instead of the whole panel - the difference between smooth and slideshow on busy panels.
        row.AttachComponent<GraphicChunkRoot>();
        rowUi = new UIBuilder(row);
        ApplyTheme(rowUi, themeContext);
        return row;
    }

    // color coding: purple ref, blue field, green list, magenta driven. pressing it pops the
    // member's reference card out of the panel when a pull source is wired; otherwise just the marker
    public static void MemberChip(UIBuilder ui, color tint, ReferenceProxySource? pullSource)
    {
        ui.PushStyle();
        ui.MinWidth(ChipWidth);
        ui.PreferredWidth(ChipWidth);
        ui.FlexibleWidth(0f);
        if (pullSource != null)
        {
            ui.PushStyle();
            ui.BackgroundColor(tint);
            var button = ui.Button("", null!);
            button.SetAction(pullSource.OnPullPressed);
            pullSource.GrabArea.Target = button.Slot;
            ui.PopStyle();
        }
        else
        {
            var chip = ui.Image(null, tint);
            FillParent(chip.RectTransform!);
        }
        ui.PopStyle();
    }

    // The room a member row's chip, actions button and label take, with the label written in when given,
    // so whatever comes next starts where a member row's editor does.
    public static void MemberLead(UIBuilder ui, string? label = null)
    {
        Gap(ui, ChipWidth);
        Gap(ui, ActionsWidth);
        ui.PushStyle();
        ui.MinWidth(LabelMinWidth);
        ui.PreferredWidth(LabelWidth);
        ui.FlexibleWidth(0f);
        if (label == null)
        {
            ui.Empty("Label");
        }
        else
        {
            var text = ui.Text(label, FontSize, MutedColor);
            FillParent(text.RectTransform!);
            text.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
            text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        }
        ui.PopStyle();
    }

    private static void Gap(UIBuilder ui, float width)
    {
        ui.PushStyle();
        ui.MinWidth(width);
        ui.PreferredWidth(width);
        ui.FlexibleWidth(0f);
        ui.Empty("Gap");
        ui.PopStyle();
    }

    // Columns that split a row's editor room the way a vector's fields do; nest out when done.
    public static void Columns(UIBuilder ui, string name)
    {
        ui.PushStyle();
        ui.FlexibleWidth(1f);
        var columns = ui.Next(name);
        var layout = columns.AttachComponent<Helio.UI.Layout.HorizontalLayout>();
        layout.Spacing.Value = ColumnSpacing;
        layout.ForceExpandHeight.Value = true;
        ui.NestInto(columns);
        ui.PopStyle();
    }

    // synced argument routed to an IInspectorActionHandler, never closures
    public static Button RelayButton(UIBuilder ui, Component handler, string argument, string label, float width)
    {
        ui.PushStyle();
        if (width > 0f)
        {
            ui.MinWidth(width);
            ui.PreferredWidth(width);
            ui.FlexibleWidth(0f);
        }
        else
        {
            ui.FlexibleWidth(1f);
        }
        var button = ui.Button(label, null!);
        var relay = button.Slot.AttachComponent<InspectorButtonRelay>();
        relay.Argument.Value = argument;
        relay.Handler.Target = handler;
        button.SetAction(relay.OnPressed);
        var text = button.Slot.GetComponentInChildren<Text>();
        if (text != null)
            FillParent(text.RectTransform!);
        ui.PopStyle();
        return button;
    }
}
