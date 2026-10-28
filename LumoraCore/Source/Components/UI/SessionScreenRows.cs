// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Lumora.Core.Networking.Session;

namespace Lumora.Core.Components.UI;

// A segmented choice whose second line says what the picked option does. The line is read on every bind
// rather than fixed when the item was built, so it follows the answer when the host changes it.
internal sealed class SessionChoiceItem : ListingChoice
{
    public Func<LocaleText>? Hint;

    public SessionChoiceItem(string key, LocaleText label) : base(key, label) { }
}

// The settings screen's choice row with that live second line.
internal sealed class SessionChoiceTemplate : SettingsRowTemplate
{
    private const int InitialSegments = 5;

    public SessionChoiceTemplate(SettingsFonts fonts) : base(fonts) { }

    protected override float ControlHeight(ListingItem item)
        => SettingsSegments.BlockHeight(Fit, (item as ListingChoice)?.Options.Count ?? 0);

    // The hint is read off the answer, so the height is measured for the answer standing now.
    protected override void PrepareHint(ListingItem item) => ReadHint(item);

    // True when the hint moved: another answer has another sentence, and maybe another line count.
    private static bool ReadHint(ListingItem item)
    {
        if (item is not SessionChoiceItem { Hint: { } hint })
            return false;
        var text = hint();
        if (text == item.DetailText)
            return false;
        item.DetailText = text;
        return true;
    }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var shell = BuildShell(row, clickable: false);
        var strip = SettingsSegments.Strip(shell.Control);
        var listingRow = new ChoiceRow { Shell = shell, Segments = new SettingsSegments(strip, Fonts), Fit = Fit };
        for (int i = 0; i < InitialSegments; i++)
            listingRow.Segments.Add(index => listingRow.Pick(index));
        return listingRow;
    }

    private sealed class ChoiceRow : ListingRow
    {
        public required SettingsRowShell Shell;
        public required SettingsSegments Segments;
        public SettingsRowFit? Fit;

        public void Pick(int index)
        {
            if (Item is not ListingChoice choice || !choice.Interactable)
                return;
            if (index < 0 || index >= choice.Options.Count)
                return;
            choice.Write(index);
            Bind(choice);
        }

        public override void Bind(ListingItem item)
        {
            if (ReadHint(item))
                Fit?.MarkStale();
            Shell.Bind(item, useDetailAsHint: true);

            var choice = item as ListingChoice;
            int count = choice?.Options.Count ?? 0;
            Segments.Grow(count, index => Pick(index));
            int selected = choice?.Read() ?? -1;
            Segments.Layout(Fit, count);
            for (int i = 0; i < count; i++)
                Segments.Paint(i, choice!.Options[i], i == selected, item.Interactable);
        }
    }
}

// ONE PERSON IN THE SESSION
//
// Name over role, then two groups of controls measured back from the right edge. The far group is the
// host's moderation (mute, silence, respawn, kick, ban) and only exists on other people's rows on the
// host's machine. The near group is your own ears (how loud they are to you, or not at all), which only
// ever writes local state and so is on every row but your own. Roles are not changed here: the
// Permissions tab lays every role out as a pill, which beats a button that cycles through them blind.
//
// A host's row wants close to a thousand units across. Narrower than the controls need beside the name,
// they go on a line under it; narrower than that, each group gets its own line, and a line still too wide
// scales its controls down to the row. The name and role are never under a button. -xlinka
internal sealed class SessionUserRowTemplate : ListingRowTemplate
{
    public const float RowHeight = 56f;

    private const float NameTop = 7f;
    private const float NameHeight = 21f;
    private const float DetailTop = 30f;
    private const float DetailHeight = 19f;
    private const float StackTop = 52f;
    private const float StackBottom = 10f;
    // How far a line of controls under the name may shrink before its groups split onto a line each.
    private const float MinScale = 0.85f;

    private const float NameWidth = 250f;
    private const float ControlGap = 6f;
    private const float GroupGap = 18f;
    private const float MuteWidth = 84f;
    private const float SilenceWidth = 92f;
    private const float RespawnWidth = 86f;
    private const float KickWidth = 64f;
    private const float BanWidth = 64f;
    private const float MuteForMeWidth = 116f;
    private const float VolumeWidth = 120f;
    private const float VolumeValueWidth = 44f;
    private const float ConsentWidth = 112f;
    private const float TakeBackWidth = 132f;

    private readonly SettingsFonts _fonts;
    private readonly SessionScreen _screen;

    public SessionUserRowTemplate(SettingsFonts fonts, SessionScreen screen)
    {
        _fonts = fonts;
        _screen = screen;
    }

    public override float Height => RowHeight;
    public override bool UsesRowBackground => false;
    public override void ConfigureRow(Slot row, ListingStyle style) { }

    // Set by the list so a row can tell how wide it is; without one every row keeps one line.
    public SettingsRowFit? Fit { get; init; }

    public override float HeightFor(ListingItem item) => HeightOf(ShapeFor(Fit, _screen, item.Tag as User));

    private static float HeightOf(int shape)
        => shape == 0
            ? RowHeight
            : StackTop + shape * SettingsMetrics.ControlBand + (shape - 1) * SettingsMetrics.SegmentGap + StackBottom;

    // Widths of what the row shows on the right, gaps included: the host's moderation (Respawn alone on
    // your own row) and your own ears.
    private static void Groups(SessionScreen screen, User user, out float moderation, out float ears)
    {
        moderation = screen.CanModerate(user)
            ? BanWidth + KickWidth + RespawnWidth + SilenceWidth + MuteWidth + ControlGap * 4f
            : user.IsLocal ? RespawnWidth : 0f;
        ears = user.IsLocal ? 0f : MuteForMeWidth + VolumeValueWidth + VolumeWidth + ConsentWidth + ControlGap * 3f
            + (EditsMe(user) ? TakeBackWidth + ControlGap : 0f);
    }

    // Whether this machine's user said yes to user editing them.
    private static bool EditsMe(User user) => SessionEditConsent.Allows(user.World?.LocalUser, user);

    private static float Both(float moderation, float ears)
        => moderation + ears + (moderation > 0f && ears > 0f ? GroupGap : 0f);

    // 0: beside the name. 1: one line under it. 2: a line under it for each group.
    private static int ShapeFor(SettingsRowFit? fit, SessionScreen screen, User? user)
    {
        if (fit is not { Measured: true } || user == null || user.IsDestroyed)
            return 0;
        Groups(screen, user, out float moderation, out float ears);
        float both = Both(moderation, ears);
        float line = fit.Width - SettingsMetrics.RowInset * 2f;
        if (both <= 0f || line - NameWidth - GroupGap >= both)
            return 0;
        return line >= both * MinScale || moderation <= 0f || ears <= 0f ? 1 : 2;
    }

    private static float ScaleFor(float line, float width) => width > line && width > 0f ? line / width : 1f;

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var background = SettingsUI.Panel(row, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusControl);

        float left = SettingsMetrics.RowInset;
        var name = SettingsUI.Label(row, "Name", _fonts.Medium, DashTheme.FontBody, DashTheme.Text,
            TextHorizontalAlignment.Left, new float2(0f, 1f), new float2(0f, 1f),
            new float2(left, -(NameTop + NameHeight)), new float2(left + NameWidth, -NameTop), shrinkToFit: true);
        var detail = SettingsUI.Label(row, "Detail", _fonts.Body, DashTheme.FontSmall, DashTheme.TextDim,
            TextHorizontalAlignment.Left, new float2(0f, 1f), new float2(0f, 1f),
            new float2(left, -(DetailTop + DetailHeight)), new float2(left + NameWidth, -DetailTop), shrinkToFit: true);

        var listingRow = new UserRow
        {
            Background = background,
            Name = name,
            Detail = detail,
            Screen = _screen,
            Fit = Fit,
            NameLink = ProfileLink.On(name),
        };

        listingRow.Ban = Chip(row, "Ban", listingRow, user => _screen.RequestBan(user));
        listingRow.Kick = Chip(row, "Kick", listingRow, user => _screen.RequestKick(user));
        listingRow.Respawn = Chip(row, "Respawn", listingRow, user => _screen.Respawn(user));
        listingRow.Silence = Chip(row, "Silence", listingRow, user => _screen.ToggleSilenced(user));
        listingRow.Mute = Chip(row, "Mute", listingRow, user => _screen.ToggleMuted(user));
        listingRow.MuteForMe = Chip(row, "MuteForMe", listingRow, user => _screen.ToggleMutedForMe(user));
        listingRow.Consent = Chip(row, "Consent", listingRow, user => _screen.AskToEdit(user));
        listingRow.TakeBack = Chip(row, "TakeBack", listingRow, user => _screen.TakeBackEdits(user));

        listingRow.VolumeValue = SettingsUI.Label(row, "VolumeValue", _fonts.Body, DashTheme.FontSmall,
            DashTheme.TextDim, TextHorizontalAlignment.Right, float2.One, float2.One,
            float2.Zero, float2.Zero);

        var volumeHost = SettingsUI.Child(row, "Volume", float2.One, float2.One,
            float2.Zero, float2.Zero);
        builder.PushStyle();
        builder.ForegroundColor(DashTheme.Accent);
        builder.NestInto(volumeHost);
        var slider = builder.Slider(1f, 0f, User.MaxLocalVolume, null, DashTheme.Field);
        builder.NestOut();
        builder.PopStyle();
        // Slider() sizes itself for a layout cell; this row anchors instead, so pin it to its host.
        var sliderRect = SettingsUI.Rect(slider.Slot);
        sliderRect.AnchorMin.Value = float2.Zero;
        sliderRect.AnchorMax.Value = float2.One;
        sliderRect.OffsetMin.Value = float2.Zero;
        sliderRect.OffsetMax.Value = float2.Zero;
        var handle = slider.Slot.FindChild("HandleArea", recursive: false)
            ?.FindChild("Handle", recursive: false)?.GetComponent<ArcSegment>();
        if (handle != null)
            slider.AddColorDriver(handle.Tint, DashTheme.OnAccent);
        listingRow.Volume = slider;
        listingRow.VolumeHost = volumeHost;

        // Reads the row's CURRENT user: this instance is recycled down the whole roster.
        slider.ValueChanged += (_, value) =>
        {
            if (listingRow.Item?.Tag is not User user || user.IsDestroyed || user.IsLocal)
                return;
            user.LocalVolume = value;
            ListingStyle.SetText(listingRow.VolumeValue, SessionLocale.Percent(user.LocalVolume));
        };
        return listingRow;
    }

    private SettingsChip Chip(Slot row, string name, ListingRow listingRow, Action<User> press)
    {
        var host = SettingsUI.Child(row, name, float2.One, float2.One, float2.Zero, float2.Zero);
        var chip = SettingsChip.Build(host, _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        chip.Button.Clicked += (_, _) =>
        {
            if (listingRow.Item?.Tag is User user && !user.IsDestroyed)
                press(user);
        };
        host.ActiveSelf.Value = false;
        return chip;
    }

    private sealed class UserRow : ListingRow
    {
        public required RoundedPanel Background;
        public required Text Name;
        public required Text Detail;
        public required SessionScreen Screen;
        public required ProfileLink NameLink;
        public SettingsRowFit? Fit;

        public SettingsChip Ban = null!;
        public SettingsChip Kick = null!;
        public SettingsChip Respawn = null!;
        public SettingsChip Silence = null!;
        public SettingsChip Mute = null!;
        public SettingsChip MuteForMe = null!;
        public SettingsChip Consent = null!;
        public SettingsChip TakeBack = null!;
        public Text VolumeValue = null!;
        public Slider Volume = null!;
        public Slot VolumeHost = null!;

        public override void Bind(ListingItem item)
        {
            SettingsUI.SetPaint(Background, DashTheme.Surface, DashTheme.Outline);
            var user = item.Tag as User;
            // the live name: signing in or out mid-session renames a user without changing who is here
            ListingStyle.SetText(Name, user != null && !user.IsDestroyed ? SessionPermissionsPanel.DisplayName(user) : item.Label);

            NameLink.Bind(user);
            int shape = ShapeFor(Fit, Screen, user);
            float inset = SettingsMetrics.RowInset;
            float line = (Fit?.Width ?? 0f) - inset * 2f;
            float nameWidth = shape == 0 ? NameWidth : line;
            Span(Name.Slot, inset, NameTop, nameWidth, NameHeight);
            Span(Detail.Slot, inset, DetailTop, nameWidth, DetailHeight);

            if (user == null || user.IsDestroyed)
            {
                ListingStyle.SetText(Detail, string.Empty);
                float none = inset;
                Place(Ban.Slot, false, 0f, ref none, 0f, 1f);
                Place(Kick.Slot, false, 0f, ref none, 0f, 1f);
                Place(Respawn.Slot, false, 0f, ref none, 0f, 1f);
                Place(Silence.Slot, false, 0f, ref none, 0f, 1f);
                Place(Mute.Slot, false, 0f, ref none, 0f, 1f);
                Place(MuteForMe.Slot, false, 0f, ref none, 0f, 1f);
                Place(VolumeValue.Slot, false, 0f, ref none, 0f, 1f);
                Place(VolumeHost, false, 0f, ref none, 0f, 1f);
                Place(Consent.Slot, false, 0f, ref none, 0f, 1f);
                Place(TakeBack.Slot, false, 0f, ref none, 0f, 1f);
                return;
            }

            // A user whose controls changed since the list measured this row (this machine took or lost
            // the session) is placed for another shape: the list measures again.
            if (Fit != null)
            {
                var rect = SettingsUI.Rect(Slot);
                if (MathF.Abs(rect.OffsetMax.Value.y - rect.OffsetMin.Value.y - HeightOf(shape)) > 0.5f)
                    Fit.MarkStale();
            }

            bool hostUser = Screen.IsHostUser(user);
            bool moderate = Screen.CanModerate(user);
            ListingStyle.SetText(Detail, Screen.DescribeUser(user, hostUser));

            Groups(Screen, user, out float moderation, out float earsWidth);
            float top = shape == 0 ? (RowHeight - SettingsMetrics.ControlBand) * 0.5f : StackTop;
            float scale = shape switch
            {
                1 => ScaleFor(line, Both(moderation, earsWidth)),
                2 => ScaleFor(line, moderation),
                _ => 1f,
            };

            float right = inset;
            Place(Ban.Slot, moderate, BanWidth, ref right, top, scale);
            Place(Kick.Slot, moderate, KickWidth, ref right, top, scale);
            // Your own row keeps Respawn: getting yourself out of a wall works solo too.
            Place(Respawn.Slot, moderate || user.IsLocal, RespawnWidth, ref right, top, scale);
            Place(Silence.Slot, moderate, SilenceWidth, ref right, top, scale);
            Place(Mute.Slot, moderate, MuteWidth, ref right, top, scale);

            bool ears = !user.IsLocal;
            if (ears && shape == 2)
            {
                right = inset;
                top += SettingsMetrics.ControlBand + SettingsMetrics.SegmentGap;
                scale = ScaleFor(line, earsWidth);
            }
            else if (ears && right > inset)
            {
                right += (GroupGap - ControlGap) * scale;
            }
            Place(MuteForMe.Slot, ears, MuteForMeWidth, ref right, top, scale);
            Place(VolumeValue.Slot, ears, VolumeValueWidth, ref right, top, scale);
            Place(VolumeHost, ears, VolumeWidth, ref right, top, scale, SettingsMetrics.SliderBand);
            // Editing them, and them editing you: the ask with where it stands, and a yes you gave them to take back.
            bool editsMe = ears && EditsMe(user);
            Place(Consent.Slot, ears, ConsentWidth, ref right, top, scale);
            Place(TakeBack.Slot, editsMe, TakeBackWidth, ref right, top, scale);

            if (moderate)
            {
                PaintState(Mute, user.IsMuted.Value, SessionLocale.Muted, SessionLocale.Mute);
                PaintState(Silence, user.IsSilenced.Value, SessionLocale.Silenced, SessionLocale.Silence);
                PaintDanger(Kick, PermissionLocale.Kick);
                PaintDanger(Ban, SessionLocale.Ban);
            }
            if (moderate || user.IsLocal)
                PaintPlain(Respawn, SessionLocale.Respawn);

            if (ears)
            {
                bool mutedForMe = user.IsAudioLocallyBlocked;
                ListingStyle.SetText(MuteForMe.Text, (mutedForMe ? SessionLocale.MutedForMe : SessionLocale.MuteForMe).Resolve());
                if (mutedForMe)
                    MuteForMe.SetPaint(PermPaint.DangerFill, PermPaint.DangerHover, DashTheme.Outline, DashTheme.Negative);
                else
                    MuteForMe.SetPaint(DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.Outline, DashTheme.TextDim);
                MuteForMe.SetInteractable(true);

                ListingStyle.SetSlider(Volume, 0f, User.MaxLocalVolume, user.LocalVolume);
                ListingStyle.SetInteractable(Volume, !mutedForMe);
                ListingStyle.SetText(VolumeValue, SessionLocale.Percent(user.LocalVolume));
                ListingStyle.SetTextColor(VolumeValue, mutedForMe ? DashTheme.TextMuted : DashTheme.TextDim);

                PaintConsent(Consent, SessionEditConsent.StateOf(user.World?.LocalUser, user));
                if (editsMe)
                    PaintDanger(TakeBack, EditConsentLocale.UsersTakeBack);
            }
        }

        // Asking is live only while nothing stands between this user and asking; the rest is where the ask stands.
        private static void PaintConsent(SettingsChip chip, EditAskState state)
        {
            ListingStyle.SetText(chip.Text, EditConsentLocale.AskLabel(state).Resolve());
            switch (state)
            {
                case EditAskState.Granted:
                    chip.SetPaint(DashTheme.AccentSoft, DashTheme.AccentSoft, DashTheme.Accent, DashTheme.Accent);
                    break;
                case EditAskState.Denied:
                    chip.SetPaint(DashTheme.Surface, DashTheme.Surface, DashTheme.Outline, DashTheme.Warning);
                    break;
                case EditAskState.None:
                    chip.SetPaint(DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.Outline, DashTheme.TextDim);
                    break;
                default:
                    chip.SetPaint(DashTheme.Surface, DashTheme.Surface, DashTheme.Outline, DashTheme.TextMuted);
                    break;
            }
            chip.SetInteractable(state == EditAskState.None);
        }

        private static void PaintState(SettingsChip chip, bool on, LocaleText onLabel, LocaleText offLabel)
        {
            ListingStyle.SetText(chip.Text, (on ? onLabel : offLabel).Resolve());
            if (on)
                chip.SetPaint(DashTheme.AccentSoft, DashTheme.AccentSoft, DashTheme.Accent, DashTheme.Accent);
            else
                chip.SetPaint(DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.Outline, DashTheme.TextDim);
            chip.SetInteractable(true);
        }

        private static void PaintDanger(SettingsChip chip, LocaleText label)
        {
            ListingStyle.SetText(chip.Text, label.Resolve());
            chip.SetPaint(PermPaint.DangerFill, PermPaint.DangerHover, DashTheme.Outline, DashTheme.Negative);
            chip.SetInteractable(true);
        }

        private static void PaintPlain(SettingsChip chip, LocaleText label)
        {
            ListingStyle.SetText(chip.Text, label.Resolve());
            chip.SetPaint(DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.Outline, DashTheme.TextDim);
            chip.SetInteractable(true);
        }

        // Right to left off the row's edge, skipping whatever this row does not show, so a guest's row
        // has no hole where the host's buttons would have been. top is the control line's top edge down
        // from the row's top; scale shrinks a line that is wider than the row.
        private static void Place(Slot slot, bool show, float width, ref float right, float top, float scale,
            float height = SettingsMetrics.ControlBand)
        {
            ListingStyle.SetActive(slot, show);
            if (!show)
                return;
            width *= scale;
            float middle = top + SettingsMetrics.ControlBand * 0.5f;
            SettingsUI.SetOffsets(SettingsUI.Rect(slot), new float2(-(right + width), -(middle + height * 0.5f)),
                new float2(-right, -(middle - height * 0.5f)));
            right += width + ControlGap * scale;
        }

        // A box hung from the row's top-left corner.
        private static void Span(Slot slot, float left, float top, float width, float height)
            => SettingsUI.SetOffsets(SettingsUI.Rect(slot), new float2(left, -(top + height)),
                new float2(left + width, -top));
    }
}
