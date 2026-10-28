// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// A group's mark: its picture cropped to cover a rounded square, or its tag on a fill of its colour while
// there is none. The square only cuts; the colour is a fill inside it, because a rounded panel drawn as its
// own stencil mask's graphic does not show. -xlinka
internal sealed class GroupMark
{
    public readonly Slot Root;
    private readonly RoundedPanel _panel;
    private readonly Text _letters;
    private readonly Slot _imageSlot;
    private readonly RawImage _image;

    private GroupMark(Slot root, RoundedPanel panel, Text letters, Slot imageSlot, RawImage image)
    {
        Root = root;
        _panel = panel;
        _letters = letters;
        _imageSlot = imageSlot;
        _image = image;
    }

    public static GroupMark Build(Slot host, IAssetProvider<FontSet>? font, float radius, float letterSize)
    {
        SettingsUI.Panel(host, color.White, color.Transparent, radius);
        var mask = host.AttachComponent<Mask>();
        mask.StencilMasking.Value = true;
        mask.ShowMaskGraphic.Value = false;
        var fill = SettingsUI.Child(host, "Fill", float2.Zero, float2.One, float2.Zero, float2.Zero);
        var panel = SettingsUI.Panel(fill, DashTheme.Field, color.Transparent, radius);
        var letters = SettingsUI.Label(host, "Letters", font, letterSize, DashTheme.Text, TextHorizontalAlignment.Center,
            float2.Zero, float2.One, new float2(3f, 0f), new float2(-3f, 0f), shrinkToFit: true);
        letters.Ellipsis.Value = false;
        var imageSlot = SettingsUI.Child(host, "Image", float2.Zero, float2.One, float2.Zero, float2.Zero);
        var image = imageSlot.AttachComponent<RawImage>();
        image.InteractionTarget.Value = false;
        imageSlot.ActiveSelf.Value = false;
        return new GroupMark(host, panel, letters, imageSlot, image);
    }

    public void Bind(PeoplePictureCache? cache, string? hash, string tag, string name, in color tint)
    {
        PeopleCrop.Show(_image, _imageSlot, Root, _letters.Slot, cache?.Get(hash));
        // Knocked well down: at full strength a group colour behind its letters reads as a warning banner.
        SettingsUI.SetFill(_panel, new color(tint.r * 0.45f, tint.g * 0.45f, tint.b * 0.45f, 1f));
        ListingStyle.SetText(_letters, tag.Length > 0 ? tag : PeopleFormat.Initials(name));
    }
}

// A fixed-width pill measured back from the row's right edge, on the shared control band, and the two ways
// a pill is painted: as something to press, and as something that only says what a thing is.
internal static class GroupBand
{
    public static Slot Right(Slot row, string name, float right, float width, float height = GroupMetrics.PillHeight)
        => SettingsUI.Child(row, name, new float2(1f, 0.5f), new float2(1f, 0.5f),
            new float2(-(right + width), -height * 0.5f), new float2(-right, height * 0.5f));

    public static Slot Left(Slot row, string name, float left, float width, float height = GroupMetrics.PillHeight)
        => SettingsUI.Child(row, name, new float2(0f, 0.5f), new float2(0f, 0.5f),
            new float2(left, -height * 0.5f), new float2(left + width, height * 0.5f));

    public static void PaintAction(SettingsChip chip, string label, bool enabled, bool danger, bool lit)
    {
        ListingStyle.SetText(chip.Text, label);
        if (danger)
            chip.SetPaint(GroupPaint.DangerFill, GroupPaint.DangerHover, DashTheme.Outline, DashTheme.Negative);
        else if (lit)
            chip.SetPaint(DashTheme.Accent, DashTheme.AccentHover, DashTheme.Accent, DashTheme.OnAccent);
        else
            chip.SetPaint(DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.Outline, DashTheme.TextDim);
        chip.SetInteractable(enabled);
    }

    // No press, no accent: the word carries it.
    public static void PaintChip(SettingsChip chip, string label, bool lit)
    {
        ListingStyle.SetText(chip.Text, label);
        chip.SetPaint(lit ? DashTheme.AccentSoft : DashTheme.Field, lit ? DashTheme.AccentSoft : DashTheme.Field,
            color.Transparent, lit ? DashTheme.Text : DashTheme.TextDim);
        chip.SetInteractable(false);
    }
}

internal static class GroupPaint
{
    public static readonly color DangerFill = DashTheme.Negative.WithAlpha(0.14f);
    public static readonly color DangerHover = DashTheme.Negative.WithAlpha(0.26f);
}

// How the Groups window turns the service's words into what it shows.
internal static class GroupsFormat
{
    // Written in sRGB the way a colour picker hands it over; the dash palette is linear, so decode once.
    public static color Tint(string? hex) => PeopleFormat.ParseColor(hex, DashTheme.Accent);

    // The group's colour as text on the window's ground: the hue stays, a colour too dark to read is lifted.
    public static color Ink(string? hex)
    {
        var c = Tint(hex);
        float luminance = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        const float Floor = 0.2f;
        if (luminance >= Floor)
            return c;
        float lift = luminance <= 0.0001f ? 1f : (Floor - luminance) / (1f - luminance);
        return new color(c.r + (1f - c.r) * lift, c.g + (1f - c.g) * lift, c.b + (1f - c.b) * lift, 1f);
    }

    public static color RoleTint(string? role) => role switch
    {
        "Owner" => DashTheme.Warning,
        "Admin" => DashTheme.Accent,
        "Moderator" => DashTheme.Positive,
        "Builder" => DashTheme.IconBlue,
        _ => DashTheme.TextDim,
    };

    // A steady colour per name for a face with no picture, so the same person keeps theirs down the list.
    public static color PersonTint(string name)
    {
        uint hash = 2166136261;
        for (int i = 0; i < name.Length; i++)
            hash = (hash ^ char.ToLowerInvariant(name[i])) * 16777619;
        return (hash % 4) switch
        {
            0 => DashTheme.IconBlue,
            1 => DashTheme.IconTeal,
            2 => DashTheme.IconPink,
            _ => DashTheme.ModeEvent,
        };
    }

    public static string Visibility(string visibility) => (visibility switch
    {
        "Public" => GroupsLocale.VisibilityOpen,
        "Private" => GroupsLocale.VisibilityRequest,
        _ => GroupsLocale.VisibilityInvite,
    }).Resolve();

    public static string Bytes(long bytes)
    {
        if (bytes <= 0)
            return "0 MB";
        double mb = bytes / (1024.0 * 1024.0);
        if (mb < 1024.0)
            return mb.ToString("0.#") + " MB";
        return (mb / 1024.0).ToString("0.##") + " GB";
    }

    public static string ShortDate(DateTime? when)
        => when.HasValue ? when.Value.ToLocalTime().ToString("yyyy-MM-dd") : "soon";

    public static string LongDate(DateTime when)
        => when.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    // A raw account id is 40 characters of nothing anybody reads. Where the service had no name to send,
    // the head is at least short enough to compare against.
    public static string ShortId(string userId)
    {
        if (string.IsNullOrEmpty(userId))
            return "?";
        return userId.Length <= 10 ? userId : userId.Substring(0, 10);
    }

    // Lines a block of text wraps to at a width, from characters per line, generous on purpose: a row a
    // line too tall is padding, a row a line too short cuts the last line off.
    public static float ProseHeight(string text, float width, float size)
    {
        int perLine = System.Math.Max(8, (int)(width / (size * PeopleMetrics.GlyphWidth)));
        int lines = 0;
        foreach (var paragraph in text.Split('\n'))
            lines += System.Math.Max(1, (paragraph.Length + perLine - 1) / perLine);
        return lines * size * PeopleMetrics.LineHeight + 4f;
    }
}
