// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Audio;
using Lumora.Core.Components.UI.Worlds;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// The middle of the dash's top bar: the world you are in (its name, its mode, how many are
// there), whether your saves are up on the cloud, the time and the frame rate. The sync label and mark
// stay visible on their own background so the status can be read at a glance. Each item is a chunk of its own, so the
// clock ticking re-meshes the clock and nothing else. Only the world item gives when the bar is short: a row
// that grew and shrank with a world's name would move everything after it on every world change. -xlinka
[ComponentCategory("Hidden")]
public sealed class DashStatusIsland : UIComponent
{
    public const float Height = 46f;
    private const float Pad = 4f;
    private const float ChipHeight = 34f;
    private const float Gap = 8f;
    private const float WorldMin = 220f;
    private const float WorldMax = 340f;
    private const float SyncWidth = 120f;
    private const float ClockWidth = 46f;
    private const float SyncDisc = 20f;
    private const float ModeWidth = 62f;
    private const float PeopleWidth = 46f;
    // Everything but the world item: the padding, the fixed items and the gaps between.
    private const float FixedWidth = Pad * 2f + SyncWidth + ClockWidth + Gap * 2f;
    private const float PollInterval = 0.5f;

    public enum SyncShown
    {
        SignedOut,
        Synced,
        Uploading,
        Error,
        Full,
    }

    private sealed class Chip
    {
        public Slot Slot = null!;
        public RoundedPanel Panel = null!;
        public RoundedButton? Button;
        public color Rest;
        public color Hover;
        public bool Hovered;
    }

    private Dashboard? _dash;
    private RectTransform? _rect;
    private Chip? _world;
    private Chip? _sync;
    private Chip? _clockChip;
    private Text? _worldName;
    private RoundedPanel? _modePanel;
    private Text? _modeText;
    private Text? _count;
    private ArcSegment? _syncTrack;
    private ArcSegment? _syncArc;
    private Image? _syncGlyph;
    private Text? _syncText;
    private Text? _clock;
    private Text? _fps;
    private readonly List<User> _users = new();
    private float _poll = PollInterval;
    private float _fpsTime;
    private int _fpsFrames;
    private float _spin;
    private volatile bool _syncChanged = true;

    public float Width { get; private set; }

    // The narrowest and widest the row lays out at: the world item takes whatever lies between.
    public static float MinWidth => FixedWidth + WorldMin;
    public static float MaxWidth => FixedWidth + WorldMax;

    // What the sync item says now, the fraction its ring shows, and whether its words are up.
    public SyncShown SyncState { get; private set; }
    public float SyncProgress { get; private set; }
    public string SyncLabel => _syncText?.Content.Value ?? string.Empty;
    public bool SyncWordsShown => _syncText != null && !_syncText.IsDestroyed && _syncText.Slot.ActiveSelf.Value;
    public string WorldLabel => _worldName?.Content.Value ?? string.Empty;
    public string PeopleLabel => _count?.Content.Value ?? string.Empty;

    internal void Bind(Dashboard dash)
    {
        _dash = dash;
        var font = dash.Font.Target;
        var semibold = dash.FontSemibold.Target ?? font;
        var bold = dash.FontBold.Target ?? semibold;

        _rect = RectTransform ?? Slot.GetComponent<RectTransform>() ?? Slot.AttachComponent<RectTransform>();
        _rect.AnchorMin.Value = new float2(0.5f, 1f);
        _rect.AnchorMax.Value = new float2(0.5f, 1f);
        Slot.AttachComponent<GraphicChunkRoot>();

        _world = AddChip("World", 0f, WorldMax, color.Transparent, DashTheme.GlassCardHover, OpenSession);
        BuildWorld(_world.Slot, semibold, bold);
        _sync = AddChip("Sync", 0f, SyncWidth, DashTheme.Surface, DashTheme.SurfaceHover, OpenInventory);
        BuildSync(_sync.Slot, font);
        _clockChip = AddChip("Clock", 0f, ClockWidth, color.Transparent, color.Transparent, null);
        _clock = SettingsUI.FillLabel(_clockChip.Slot, "Text", semibold, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Center, true);
        Place(0f, MaxWidth);

        InventorySync.Changed += OnSyncChanged;
        Refresh();
        RefreshSync();
    }

    public override void OnDestroy()
    {
        InventorySync.Changed -= OnSyncChanged;
        base.OnDestroy();
    }

    // Raised off the main thread; only a flag crosses over.
    private void OnSyncChanged() => _syncChanged = true;

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        if (_dash == null || World == null)
            return;
        float delta = World.Time.RawDelta;
        _fpsFrames++;
        _fpsTime += delta;
        _poll += delta;
        if (_poll < PollInterval)
            return;
        _poll = 0f;
        Refresh();
        if (_syncChanged || SyncState == SyncShown.Uploading)
        {
            _syncChanged = false;
            RefreshSync();
        }
    }

    // Where the row sits along the top (its centre, from the canvas centre) and how wide it is. The world
    // item takes the width; every other item keeps its own.
    public void Place(float centerX, float width)
    {
        width = MathF.Max(MinWidth, MathF.Min(MaxWidth, width));
        Width = width;
        if (_rect != null && !_rect.IsDestroyed)
        {
            SettingsUI.SetOffsets(_rect, new float2(centerX - width * 0.5f, -(Dashboard.IslandTop + Height)),
                new float2(centerX + width * 0.5f, -Dashboard.IslandTop));
        }
        float x = Pad;
        x = Lay(_world?.Slot, x, width - FixedWidth);
        x = Lay(_sync?.Slot, x, SyncWidth);
        Lay(_clockChip?.Slot, x, ClockWidth);
    }

    private static float Lay(Slot? slot, float left, float width)
    {
        if (slot != null && !slot.IsDestroyed)
        {
            var rect = SettingsUI.Rect(slot);
            var min = rect.OffsetMin.Value;
            var max = rect.OffsetMax.Value;
            SettingsUI.SetOffsets(rect, new float2(left, min.y), new float2(left + width, max.y));
        }
        return left + width + Gap;
    }

    // BUILD

    private Chip AddChip(string name, float left, float width, in color rest, in color hover, Action? press)
    {
        var slot = SettingsUI.Child(Slot, name, new float2(0f, 0.5f), new float2(0f, 0.5f),
            new float2(left, -ChipHeight * 0.5f), new float2(left + width, ChipHeight * 0.5f));
        slot.AttachComponent<GraphicChunkRoot>();
        var chip = new Chip { Slot = slot, Rest = rest, Hover = hover };
        chip.Panel = SettingsUI.Panel(slot, rest, color.Transparent, DashTheme.RadiusControl);
        if (press != null)
        {
            var button = slot.AttachComponent<RoundedButton>();
            button.CornerRadius.Value = DashTheme.RadiusControl;
            button.Clicked += (_, _) => press();
            button.HoverEntered += _ => { chip.Hovered = true; PaintChip(chip); };
            button.HoverExited += _ => { chip.Hovered = false; PaintChip(chip); };
            chip.Button = button;
        }
        return chip;
    }

    private static void PaintChip(Chip chip)
    {
        if (chip.Panel.IsDestroyed)
            return;
        SettingsUI.SetFill(chip.Panel, chip.Hovered ? chip.Hover : chip.Rest);
    }

    private Image Glyph(Slot parent, string name, string icon, in float2 anchor, in float2 offsetMin, in float2 offsetMax, in color tint)
    {
        var slot = SettingsUI.Child(parent, name, anchor, anchor, offsetMin, offsetMax);
        var image = slot.AttachComponent<Image>();
        image.Texture.Target = _dash!.IconProvider(icon);
        image.PreserveAspect.Value = true;
        image.InteractionTarget.Value = false;
        image.Tint.Value = tint;
        return image;
    }

    private void BuildWorld(Slot chip, IAssetProvider<FontSet>? semibold, IAssetProvider<FontSet>? bold)
    {
        // Keep the name and frame rate together in the centre group, with mode and people to its right.
        var title = SettingsUI.Child(chip, "Title", float2.Zero, float2.One,
            new float2(10f, 0f), new float2(-(ModeWidth + PeopleWidth + 14f), 0f));
        _worldName = SettingsUI.Label(title, "Name", bold, DashTheme.FontBody, DashTheme.Text, TextHorizontalAlignment.Center,
            new float2(0f, 0.42f), float2.One, float2.Zero, float2.Zero, shrinkToFit: true);
        _fps = SettingsUI.Label(title, "Fps", semibold, DashTheme.FontLabel, DashTheme.TextDim, TextHorizontalAlignment.Center,
            float2.Zero, new float2(1f, 0.42f), float2.Zero, float2.Zero, shrinkToFit: true);

        var mode = SettingsUI.Child(chip, "Mode", new float2(1f, 0.5f), new float2(1f, 0.5f),
            new float2(-(ModeWidth + PeopleWidth + 6f), -10f), new float2(-(PeopleWidth + 6f), 10f));
        _modePanel = SettingsUI.Panel(mode, DashTheme.ModeBuilder.WithAlpha(0.16f), color.Transparent, 6f);
        _modeText = SettingsUI.FillLabel(mode, "Text", bold, 11f, DashTheme.ModeBuilder, TextHorizontalAlignment.Center, true);

        var people = SettingsUI.Child(chip, "People", new float2(1f, 0f), new float2(1f, 1f), new float2(-PeopleWidth, 0f), new float2(-4f, 0f));
        Glyph(people, "Glyph", "avatar", new float2(0f, 0.5f), new float2(2f, -8f), new float2(18f, 8f), DashTheme.TextDim);
        _count = SettingsUI.Label(people, "Count", semibold, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Left,
            float2.Zero, float2.One, new float2(22f, 0f), float2.Zero, shrinkToFit: true);
    }

    // Right-aligned against the clock, with the status label to the left of its mark.
    private void BuildSync(Slot chip, IAssetProvider<FontSet>? font)
    {
        var holder = SettingsUI.Child(chip, "Status", new float2(1f, 0.5f), new float2(1f, 0.5f),
            new float2(-(SyncDisc + 8f), -SyncDisc * 0.5f), new float2(-8f, SyncDisc * 0.5f));
        var track = SettingsUI.Child(holder, "Track", float2.Zero, float2.One, float2.Zero, float2.Zero);
        _syncTrack = track.AttachComponent<ArcSegment>();
        _syncTrack.InnerRadius.Value = 7f;
        _syncTrack.OuterRadius.Value = 9.5f;
        _syncTrack.AngleStart.Value = -90f;
        _syncTrack.ArcLength.Value = 360f;
        _syncTrack.Tint.Value = DashTheme.Outline.WithAlpha(0.2f);
        _syncTrack.OutlineThickness.Value = 0f;
        var arc = SettingsUI.Child(holder, "Progress", float2.Zero, float2.One, float2.Zero, float2.Zero);
        _syncArc = arc.AttachComponent<ArcSegment>();
        _syncArc.InnerRadius.Value = 7f;
        _syncArc.OuterRadius.Value = 9.5f;
        _syncArc.AngleStart.Value = -90f;
        _syncArc.ArcLength.Value = 90f;
        _syncArc.Tint.Value = DashTheme.Accent;
        _syncArc.OutlineThickness.Value = 0f;
        _syncGlyph = Glyph(holder, "Glyph", "check", new float2(0.5f, 0.5f), new float2(-8f, -8f), new float2(8f, 8f), DashTheme.Text);
        _syncText = SettingsUI.Label(chip, "Text", font, DashTheme.FontSmall, DashTheme.Text, TextHorizontalAlignment.Right,
            float2.Zero, float2.One, new float2(8f, 0f), new float2(-(SyncDisc + 14f), 0f), shrinkToFit: true);
    }

    // READING

    private static World? FocusedWorld
    {
        get
        {
            var world = Engine.Current?.WorldManager?.FocusedWorld;
            return world != null && !world.IsDestroyed ? world : null;
        }
    }

    private static bool IsLocalHome(World? world)
        => world != null && !world.IsDestroyed && world.WorldName?.Value == "LocalHome";

    private void Refresh()
    {
        RefreshWorld(FocusedWorld);
        if (_clock != null)
            ListingStyle.SetText(_clock, DateTime.Now.ToString("HH:mm"));
        if (_fps != null && _fpsTime > 0f)
        {
            ListingStyle.SetText(_fps, MathF.Round(_fpsFrames / _fpsTime).ToString("0") + " fps");
            _fpsFrames = 0;
            _fpsTime = 0f;
        }
    }

    private void RefreshWorld(World? world)
    {
        if (_worldName == null)
            return;
        string name = world == null ? "No world" : IsLocalHome(world) ? Management.WorldManager.LocalHomeTitle.Resolve() : (world.Name ?? "World");
        ListingStyle.SetText(_worldName, name);

        var mode = world?.RootSlot?.GetComponent<WorldSettings>()?.Mode.Value ?? WorldMode.Builder;
        var tint = DashTheme.ModeTint(mode);
        if (_modeText != null)
        {
            ListingStyle.SetText(_modeText, DashTheme.ModeLabel(mode).ToUpperInvariant());
            ListingStyle.SetTextColor(_modeText, tint);
        }
        if (_modePanel != null)
            SettingsUI.SetFill(_modePanel, tint.WithAlpha(0.16f));

        _users.Clear();
        world?.GetAllUsers(_users);
        int people = 0;
        foreach (var user in _users)
        {
            if (user != null && !user.IsDestroyed)
                people++;
        }
        _users.Clear();
        if (_count != null)
            ListingStyle.SetText(_count, people.ToString());
    }

    private void RefreshSync()
    {
        bool signedIn = InventorySync.SignedIn;
        var status = InventorySync.Status;
        int waiting = status.Pending + status.Syncing;

        SyncShown shown;
        string text;
        float progress = 0f;
        if (!signedIn)
        {
            shown = SyncShown.SignedOut;
            text = "Not synced";
        }
        else if (status.OutOfSpace)
        {
            shown = SyncShown.Full;
            text = "Out of space";
        }
        // saves kept on this machine wait for the email, so no spinner that never ends
        else if (status.WaitingForEmail && waiting > 0)
        {
            shown = SyncShown.Full;
            text = AccountEmailLocale.ChipWaiting.Resolve();
        }
        else if (status.Variants > 0 || waiting > 0)
        {
            shown = SyncShown.Uploading;
            int count = waiting > 0 ? waiting : status.Variants;
            text = "Uploading " + count;
            progress = status.Percent >= 0 ? status.Percent / 100f : -1f;
        }
        else if (status.Failed > 0)
        {
            shown = SyncShown.Error;
            text = "Sync error";
        }
        else
        {
            shown = SyncShown.Synced;
            text = "Synced";
        }

        SyncState = shown;
        SyncProgress = progress;
        var ink = shown switch
        {
            SyncShown.Full => DashTheme.Warning,
            SyncShown.Error => DashTheme.Negative,
            _ => DashTheme.Text,
        };
        if (_syncText != null)
        {
            ListingStyle.SetText(_syncText, text);
            ListingStyle.SetTextColor(_syncText, ink);
        }
        bool ring = shown == SyncShown.Uploading;
        if (_syncTrack != null)
            ListingStyle.SetActive(_syncTrack.Slot, ring);
        if (_syncArc != null)
        {
            ListingStyle.SetActive(_syncArc.Slot, ring);
            if (ring)
            {
                // A known share fills the ring from the top; an upload that has not reported one yet turns a
                // quarter arc round instead.
                float start = -90f;
                float length = 90f;
                if (progress >= 0f)
                    length = MathF.Max(8f, progress * 360f);
                else
                {
                    _spin = (_spin + 90f) % 360f;
                    start = -90f + _spin;
                }
                if (_syncArc.AngleStart.Value != start)
                    _syncArc.AngleStart.Value = start;
                if (_syncArc.ArcLength.Value != length)
                    _syncArc.ArcLength.Value = length;
            }
        }
        if (_syncGlyph != null)
        {
            bool glyph = shown != SyncShown.Uploading;
            ListingStyle.SetActive(_syncGlyph.Slot, glyph);
            if (glyph)
            {
                var provider = _dash!.IconProvider(shown is SyncShown.Synced ? "check" : shown is SyncShown.SignedOut ? "minus" : "alert");
                if (!ReferenceEquals(_syncGlyph.Texture.Target, provider))
                    _syncGlyph.Texture.Target = provider;
                if (!_syncGlyph.Tint.Value.Equals(ink))
                    _syncGlyph.Tint.Value = ink;
            }
        }
    }

    // Forces a read now, for checks.
    public void RefreshNow()
    {
        Refresh();
        RefreshSync();
    }

    // ACTIONS

    private void OpenSession() => OpenScreen<SessionScreen>();

    private void OpenInventory() => OpenScreen<InventoryScreen>();

    private void OpenScreen<T>() where T : DashboardScreen
    {
        var dash = _dash;
        if (dash == null)
            return;
        foreach (var screen in dash.Screens)
        {
            if (screen is T)
            {
                dash.SwitchTo(screen);
                return;
            }
        }
    }
}
