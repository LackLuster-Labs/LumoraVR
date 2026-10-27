// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Helio.UI;
using Lumora.Audio;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Audio;
using Lumora.Core.Components.Media;
using Lumora.Core.Components.UI;
using Lumora.Core.Logging;
using Lumora.Core.Math;
using GroupKind = Lumora.Core.Audio.AudioTypeGroup;

namespace Lumora.Core.Components.Import;

// A sound, shown before it comes in. One page: the clip on a card at the top (its name, length, channels, rate,
// format and loudest peak, its shape as a row of bars, and a round button to hear it on this machine alone), then
// what it becomes, then how it plays, then Import.
//
// Every choice is one the audio engine already has:
// - Player or clip orb. The player is the panel with transport, loop, 3D and volume; the orb only carries the
//   clip, for dropping into sound fields, and nothing plays from it.
// - Heard from the object (positioned, fading with distance) or the same everywhere (no position, no fade).
// - Counts as an effect or as music: which volume slider turns it down and how far it carries. Guessed from the
//   length, the same rule the player uses (ten seconds or more, and not a WAV, is music), until one is picked.
// - Loop, play on arrival, and the volume it lands at.
// - Normalize: the clip edit that raises the loudest peak to -1 dB, run once as it lands and saved as a WAV.
//
// Several files share one set of choices; the arrows on the card step through them for a listen. A format the
// decoders cannot read (FLAC, AAC and the like) is named as such and left to the raw-file drop. -xlinka
[ComponentCategory("Assets/Import")]
public sealed class AudioImportDialog : ImportDialog
{
    public readonly Sync<AudioImportForm> Form;
    public readonly Sync<bool> Spatial;
    // False leaves the kind to each clip's length.
    public readonly Sync<bool> KindChosen;
    public readonly Sync<GroupKind> Kind;
    public readonly Sync<bool> Loop;
    public readonly Sync<bool> PlayOnArrival;
    public readonly Sync<bool> Normalize;
    // Where the volume slider sits, 0..1. Heard as its square, the way the player's own slider works.
    public readonly Sync<float> Volume;

    protected override string TitleText => "Audio Import";
    protected override float2 CanvasSize => new float2(540f, 720f);
    protected override float PhysicalHeight => 0.58f;

    private const float Pad = 16f;
    private const int BarCount = 64;
    private const double MusicSeconds = 10.0;
    // Past this the file is not decoded for its shape: a long recording would be hundreds of megabytes of samples
    // to draw a row of bars.
    private const long ReadLimitBytes = 24L * 1024 * 1024;
    private const float NormalizePeakDb = -1f;

    private static readonly color CardFill = DashTheme.GlassCard;
    private static readonly color BarRest = DashTheme.Accent.WithAlpha(0.32f);
    private static readonly color BarPlayed = DashTheme.Accent;

    private sealed class ClipInfo
    {
        public string Source = "";
        public string Name = "";
        public string Extension = "";
        public bool Remote;
        public bool Playable;
        public bool Read;
        public string? Problem;
        public double Duration;
        public int Channels;
        public int SampleRate;
        public float PeakDb = float.NegativeInfinity;
        public float[]? Bars;
        public long Bytes;

        public GroupKind Guess => !Extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) && Duration >= MusicSeconds
            ? GroupKind.Multimedia : GroupKind.SoundEffect;
    }

    private sealed class Choice
    {
        public Slot Slot = null!;
        public RoundedPanel Panel = null!;
        public Button Button = null!;
        public Text Text = null!;
        public Text? Detail;
        public RoundedPanel? Badge;
        public Text? Glyph;
    }

    private readonly List<ClipInfo> _clips = new();
    private bool _reading;
    private int _shown;

    private IAssetProvider<FontSet>? _font;
    private FontProvider? _glyphFont;
    private Text? _title, _meta, _waveNote, _counter, _status, _hint, _volumeValue, _playGlyph, _sectionNote;
    private RoundedPanel? _playPlate;
    private Button? _playButton, _prev, _next;
    private readonly List<RoundedPanel> _bars = new();
    private Choice? _player, _orb, _fromObject, _everywhere, _effect, _music, _loop, _arrive, _normalize, _import;
    private Text? _heardLabel, _kindLabel, _volumeLabel;
    private Slider? _volume;
    private int _barsLit = -1;

    private Slot? _previewSlot;
    private StaticAudioClip? _previewClip;
    private AudioCuePlayer? _previewCue;
    private AudioOutput? _previewOutput;
    private int _previewIndex = -1;
    private double _previewStart;
    private bool _previewing;

    public AudioImportDialog()
    {
        Form = new Sync<AudioImportForm>(this, AudioImportForm.Player);
        Spatial = new Sync<bool>(this, true);
        KindChosen = new Sync<bool>(this, false);
        Kind = new Sync<GroupKind>(this, GroupKind.SoundEffect);
        Loop = new Sync<bool>(this, false);
        PlayOnArrival = new Sync<bool>(this, false);
        Normalize = new Sync<bool>(this, false);
        Volume = new Sync<float>(this, 1f);
    }

    // BUILD

    protected override void OpenRoot(UIBuilder ui)
    {
        Collect();
        _font = ui.Style.Font;
        _glyphFont = GlyphFont();
        _bars.Clear();
        _barsLit = -1;
        var page = ui.Root;
        BuildCard(page);
        BuildForm(page);
        BuildPlayback(page);
        BuildActions(page);
        Repaint();
        StartReading();
    }

    private FontProvider? GlyphFont()
    {
        if (_glyphFont is { IsDestroyed: false })
            return _glyphFont;
        var slot = Slot.FindChild("GlyphFont", recursive: false) ?? Slot.AddSlot("GlyphFont");
        var font = slot.GetComponent<FontProvider>() ?? slot.AttachComponent<FontProvider>();
        font.URL.Value = DashTheme.FontMono;
        return font;
    }

    private void Collect()
    {
        if (_clips.Count > 0)
            return;
        foreach (var source in Paths)
        {
            bool remote = IsRemote(source);
            string extension = Extension(source);
            _clips.Add(new ClipInfo
            {
                Source = source,
                Name = NameFor(source),
                Extension = extension,
                Remote = remote,
                Playable = AudioDecoders.IsSupportedExtension(extension),
            });
        }
    }

    private void BuildCard(Slot page)
    {
        var card = Pin(page, "Clip", 6f, 162f);
        SettingsUI.Panel(card, CardFill, DashTheme.OutlineStrong, DashTheme.RadiusCard + 2f);

        var play = SettingsUI.Child(card, "Play", new float2(0f, 1f), new float2(0f, 1f), new float2(16f, -74f), new float2(74f, -16f));
        _playPlate = SettingsUI.Panel(play, DashTheme.Accent, color.Transparent, 29f);
        _playButton = play.AttachComponent<Button>();
        _playButton.Clicked += (_, _) => { if (CanInteract) TogglePreview(); };
        var playDriver = _playButton.AddColorDriver(_playPlate.Color, DashTheme.Accent, InteractionColorMode.Direct);
        playDriver.HighlightColor.Value = DashTheme.AccentHover;
        playDriver.PressedColor.Value = DashTheme.AccentPressed;
        playDriver.DisabledColor.Value = DashTheme.Field;
        _playGlyph = SettingsUI.FillLabel(play, "Glyph", _glyphFont, 22f, DashTheme.OnAccent);

        bool several = _clips.Count > 1;
        float titleRight = several ? 120f : 18f;
        _title = SettingsUI.Label(card, "Title", _font, DashTheme.FontTitle, DashTheme.Text, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(90f, -46f), new float2(-titleRight, -18f));
        _meta = SettingsUI.Label(card, "Meta", _font, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(90f, -70f), new float2(-18f, -48f));

        if (several)
        {
            _prev = StepButton(card, "Prev", "←", 88f, -1);
            _next = StepButton(card, "Next", "→", 18f, 1);
            _counter = SettingsUI.Label(card, "Counter", _font, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Center,
                new float2(1f, 1f), new float2(1f, 1f), new float2(-88f, -46f), new float2(-48f, -18f));
        }

        var wave = SettingsUI.Child(card, "Wave", float2.Zero, new float2(1f, 0f), new float2(18f, 16f), new float2(-18f, 80f));
        for (int i = 0; i < BarCount; i++)
        {
            float x0 = i / (float)BarCount, x1 = (i + 1) / (float)BarCount;
            var bar = SettingsUI.Child(wave, "Bar", new float2(x0, 0.48f), new float2(x1, 0.52f), new float2(1f, 0f), new float2(-1f, 0f));
            _bars.Add(SettingsUI.Panel(bar, BarRest, color.Transparent, 1.5f));
        }
        _waveNote = SettingsUI.FillLabel(wave, "Note", _font, DashTheme.FontSmall, DashTheme.TextMuted);
        _waveNote.WordWrap.Value = true;
    }

    private Button StepButton(Slot card, string name, string glyph, float right, int step)
    {
        var slot = SettingsUI.Child(card, name, new float2(1f, 1f), new float2(1f, 1f), new float2(-right - 30f, -46f), new float2(-right, -18f));
        var panel = SettingsUI.Panel(slot, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusControl);
        var button = slot.AttachComponent<Button>();
        button.Clicked += (_, _) => { if (CanInteract) Step(step); };
        var driver = button.AddColorDriver(panel.Color, DashTheme.Surface, InteractionColorMode.Direct);
        driver.HighlightColor.Value = DashTheme.SurfaceHover;
        driver.PressedColor.Value = DashTheme.SurfacePressed;
        driver.DisabledColor.Value = DashTheme.Field;
        SettingsUI.FillLabel(slot, "Glyph", _glyphFont, 15f, DashTheme.Text).Content.Value = glyph;
        return button;
    }

    private void BuildForm(Slot page)
    {
        SectionLabel(page, "BringLabel", "BRING IN AS", 182f);
        var left = SettingsUI.Child(page, "Player", new float2(0f, 1f), new float2(0.5f, 1f), new float2(Pad, -296f), new float2(-5f, -204f));
        var right = SettingsUI.Child(page, "Orb", new float2(0.5f, 1f), new float2(1f, 1f), new float2(5f, -296f), new float2(-Pad, -204f));
        _player = Tile(left, "▶", "Player", "A panel with play, seek, loop, 3D and volume.", () => SetForm(AudioImportForm.Player));
        _orb = Tile(right, "●", "Clip orb", "A small bead that carries the clip. Hold it over a sound field to drop the clip in.", () => SetForm(AudioImportForm.Orb));
    }

    private Choice Tile(Slot slot, string glyph, string name, string detail, Action onClick)
    {
        var choice = NewChoice(slot, onClick, DashTheme.RadiusCard);
        var badgeSlot = SettingsUI.Child(slot, "Badge", new float2(0f, 1f), new float2(0f, 1f), new float2(14f, -52f), new float2(50f, -16f));
        choice.Badge = SettingsUI.Panel(badgeSlot, DashTheme.AccentSoft, color.Transparent, 10f);
        choice.Glyph = SettingsUI.FillLabel(badgeSlot, "Glyph", _glyphFont, 16f, DashTheme.Accent);
        choice.Glyph.Content.Value = glyph;
        choice.Text = SettingsUI.Label(slot, "Name", _font, DashTheme.FontHeading, DashTheme.Text, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(62f, -40f), new float2(-12f, -14f));
        choice.Text.Content.Value = name;
        choice.Detail = SettingsUI.Label(slot, "Detail", _font, DashTheme.FontLabel, DashTheme.TextDim, TextHorizontalAlignment.Left,
            new float2(0f, 0f), new float2(1f, 1f), new float2(62f, 8f), new float2(-12f, -42f));
        choice.Detail.WordWrap.Value = true;
        choice.Detail.Ellipsis.Value = false;
        choice.Detail.VerticalAlignment.Value = TextVerticalAlignment.Top;
        choice.Detail.Content.Value = detail;
        return choice;
    }

    private void BuildPlayback(Slot page)
    {
        SectionLabel(page, "HowLabel", "HOW IT PLAYS", 310f);
        _sectionNote = SettingsUI.Label(page, "HowNote", _font, DashTheme.FontLabel, DashTheme.TextMuted, TextHorizontalAlignment.Right,
            new float2(0f, 1f), new float2(1f, 1f), new float2(Pad, -328f), new float2(-Pad - 4f, -310f));

        _heardLabel = RowLabel(page, "HeardLabel", "Heard", 334f);
        var heard = Strip(page, "Heard", 334f);
        _fromObject = Segment(heard, "FromObject", "From the object", 0, () => SetSpatial(true));
        _everywhere = Segment(heard, "Everywhere", "The same everywhere", 1, () => SetSpatial(false));

        _kindLabel = RowLabel(page, "KindLabel", "Counts as", 378f);
        var kind = Strip(page, "Kind", 378f);
        _effect = Segment(kind, "Effect", "Sound effect", 0, () => SetKind(GroupKind.SoundEffect));
        _music = Segment(kind, "Music", "Music", 1, () => SetKind(GroupKind.Multimedia));

        float x = Pad;
        _loop = Chip(page, "Loop", "Loop", ref x, 92f, () => Toggle(Loop));
        _arrive = Chip(page, "Arrive", "Play on arrival", ref x, 150f, () => Toggle(PlayOnArrival));
        _normalize = Chip(page, "Normalize", "Normalize", ref x, 124f, () => Toggle(Normalize));
        _hint = SettingsUI.Label(page, "Hint", _font, DashTheme.FontLabel, DashTheme.TextMuted, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(Pad + 4f, -480f), new float2(-Pad, -460f));

        _volumeLabel = RowLabel(page, "VolumeLabel", "Volume", 486f);
        var host = SettingsUI.Child(page, "VolumeBar", new float2(0f, 1f), new float2(1f, 1f), new float2(150f, -522f), new float2(-76f, -486f));
        host.AttachComponent<GraphicChunkRoot>();
        var builder = new UIBuilder(host);
        builder.ForegroundColor(DashTheme.Accent);
        _volume = builder.Slider(Clamp01(Volume.Value), 0f, 1f, OnVolume, DashTheme.Field);
        var rect = SettingsUI.Rect(_volume.Slot);
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;
        rect.OffsetMin.Value = float2.Zero;
        rect.OffsetMax.Value = float2.Zero;
        _volumeValue = SettingsUI.Label(page, "VolumeValue", _font, DashTheme.FontBody, DashTheme.TextDim, TextHorizontalAlignment.Right,
            new float2(1f, 1f), new float2(1f, 1f), new float2(-Pad - 56f, -522f), new float2(-Pad, -486f));
    }

    private void BuildActions(Slot page)
    {
        _status = SettingsUI.Label(page, "Status", _font, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(Pad + 4f, -580f), new float2(-Pad, -532f));
        _status.WordWrap.Value = true;
        _status.Ellipsis.Value = false;

        var raw = SettingsUI.Child(page, "Raw", float2.Zero, float2.Zero, new float2(Pad, 12f), new float2(Pad + 150f, 62f));
        var rawChoice = NewChoice(raw, AsRawFile, DashTheme.RadiusControl);
        rawChoice.Text = SettingsUI.FillLabel(raw, "Text", _font, DashTheme.FontBody, DashTheme.TextDim);
        rawChoice.Text.Content.Value = "As Raw File";
        Paint(rawChoice, false, true);

        var go = SettingsUI.Child(page, "Import", new float2(1f, 0f), new float2(1f, 0f), new float2(-Pad - 220f, 12f), new float2(-Pad, 62f));
        _import = NewChoice(go, RunImport, DashTheme.RadiusControl);
        _import.Text = SettingsUI.FillLabel(go, "Text", _font, DashTheme.FontHeading, DashTheme.OnAccent);
    }

    // CONTROLS

    private static Slot Pin(Slot parent, string name, float top, float height)
        => SettingsUI.Child(parent, name, new float2(0f, 1f), new float2(1f, 1f), new float2(Pad, -(top + height)), new float2(-Pad, -top));

    private void SectionLabel(Slot page, string name, string text, float top)
    {
        var label = SettingsUI.Label(page, name, _font, DashTheme.FontLabel, DashTheme.TextDim, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(1f, 1f), new float2(Pad + 4f, -(top + 18f)), new float2(-Pad, -top));
        label.Content.Value = text;
    }

    private Text RowLabel(Slot page, string name, string text, float top)
    {
        var label = SettingsUI.Label(page, name, _font, DashTheme.FontBody, DashTheme.TextDim, TextHorizontalAlignment.Left,
            new float2(0f, 1f), new float2(0f, 1f), new float2(Pad + 4f, -(top + 36f)), new float2(146f, -top));
        label.Content.Value = text;
        return label;
    }

    private static Slot Strip(Slot page, string name, float top)
    {
        var strip = SettingsUI.Child(page, name, new float2(0f, 1f), new float2(1f, 1f), new float2(150f, -(top + 36f)), new float2(-Pad, -top));
        SettingsUI.Panel(strip, DashTheme.Field.WithAlpha(0.7f), DashTheme.Outline, DashTheme.RadiusControl + 1f);
        return strip;
    }

    private Choice Segment(Slot strip, string name, string label, int index, Action onClick)
    {
        var slot = SettingsUI.Child(strip, name, new float2(index * 0.5f, 0f), new float2(index * 0.5f + 0.5f, 1f),
            new float2(index == 0 ? 3f : 1.5f, 3f), new float2(index == 0 ? -1.5f : -3f, -3f));
        var choice = NewChoice(slot, onClick, DashTheme.RadiusControl - 1f);
        choice.Text = SettingsUI.FillLabel(slot, "Text", _font, DashTheme.FontSmall, DashTheme.TextDim, shrinkToFit: true);
        choice.Text.Content.Value = label;
        return choice;
    }

    private Choice Chip(Slot page, string name, string label, ref float x, float width, Action onClick)
    {
        var slot = SettingsUI.Child(page, name, new float2(0f, 1f), new float2(0f, 1f), new float2(x, -454f), new float2(x + width, -420f));
        x += width + 8f;
        var choice = NewChoice(slot, onClick, 17f);
        choice.Text = SettingsUI.FillLabel(slot, "Text", _font, DashTheme.FontSmall, DashTheme.TextDim, shrinkToFit: true);
        choice.Text.Content.Value = label;
        return choice;
    }

    // A plate that lights under the pointer. Its colours are the driver's to set; Paint picks them.
    private Choice NewChoice(Slot slot, Action onClick, float radius)
    {
        var panel = SettingsUI.Panel(slot, DashTheme.Surface, DashTheme.Outline, radius);
        var button = slot.AttachComponent<Button>();
        button.Clicked += (_, _) => { if (CanInteract) onClick(); };
        button.AddColorDriver(panel.Color, DashTheme.Surface, InteractionColorMode.Direct);
        return new Choice { Slot = slot, Panel = panel, Button = button };
    }

    private static void Paint(Choice? choice, bool on, bool enabled, bool primary = false)
    {
        if (choice == null || choice.Button.IsDestroyed)
            return;
        color normal = primary ? DashTheme.Accent : on ? DashTheme.AccentSoft : DashTheme.Surface.WithAlpha(0.55f);
        color hover = primary ? DashTheme.AccentHover : on ? DashTheme.Accent.WithAlpha(0.28f) : DashTheme.SurfaceHover;
        color pressed = primary ? DashTheme.AccentPressed : DashTheme.Accent.WithAlpha(0.34f);
        foreach (var driver in choice.Button.ColorDrivers)
        {
            if (driver == null || driver.IsDestroyed)
                continue;
            driver.NormalColor.Value = normal;
            driver.HighlightColor.Value = hover;
            driver.PressedColor.Value = pressed;
            driver.DisabledColor.Value = DashTheme.Field.WithAlpha(0.5f);
            driver.Apply();
        }
        if (choice.Button.Interactable.Value != enabled)
            choice.Button.Interactable.Value = enabled;
        var outline = on && !primary ? DashTheme.Accent : DashTheme.Outline;
        float ring = on && !primary ? 2f : DashTheme.OutlineWidth;
        if (!choice.Panel.OutlineColor.Value.Equals(outline))
            choice.Panel.OutlineColor.Value = outline;
        if (choice.Panel.OutlineThickness.Value != ring)
            choice.Panel.OutlineThickness.Value = ring;
        var text = !enabled ? DashTheme.TextMuted : primary ? DashTheme.OnAccent : on ? DashTheme.Text : DashTheme.TextDim;
        SetColor(choice.Text, text);
        SetColor(choice.Detail, enabled ? DashTheme.TextDim : DashTheme.TextMuted);
        SetColor(choice.Glyph, enabled ? DashTheme.Accent : DashTheme.TextMuted);
        if (choice.Badge != null)
        {
            var badge = enabled ? (on ? DashTheme.Accent.WithAlpha(0.3f) : DashTheme.AccentSoft) : DashTheme.Field;
            if (!choice.Badge.Color.Value.Equals(badge))
                choice.Badge.Color.Value = badge;
        }
    }

    // CHOICES

    private void SetForm(AudioImportForm form)
    {
        Form.Value = form;
        Repaint();
    }

    private void SetSpatial(bool on)
    {
        Spatial.Value = on;
        Repaint();
    }

    private void SetKind(GroupKind kind)
    {
        KindChosen.Value = true;
        Kind.Value = kind;
        Repaint();
    }

    private void Toggle(Sync<bool> field)
    {
        field.Value = !field.Value;
        Repaint();
    }

    [SyncMethod]
    public void OnVolume(Slider slider, float value)
    {
        if (!CanInteract)
            return;
        Volume.Value = Clamp01(value);
        RepaintVolume();
    }

    private void Step(int step)
    {
        if (_clips.Count < 2)
            return;
        StopPreview();
        _shown = ((_shown + step) % _clips.Count + _clips.Count) % _clips.Count;
        _barsLit = -1;
        Repaint();
    }

    // PAINT

    private void Repaint()
    {
        if (_title == null || _title.IsDestroyed)
            return;
        var shown = Shown;
        bool player = Form.Value == AudioImportForm.Player;

        SetText(_title, shown?.Name ?? "No sound");
        SetText(_meta, Meta(shown));
        if (_counter != null)
            SetText(_counter, $"{_shown + 1}/{_clips.Count}");
        bool canPreview = shown is { Playable: true, Remote: false } && World?.LocalUser != null;
        if (_playButton is { IsDestroyed: false } && _playButton.Interactable.Value != canPreview)
            _playButton.Interactable.Value = canPreview;
        SetText(_playGlyph, _previewing ? "■" : "▶");
        SetColor(_playGlyph, canPreview ? DashTheme.OnAccent : DashTheme.TextMuted);
        RepaintBars(shown);

        Paint(_player, player, true);
        Paint(_orb, !player, true);
        SetText(_sectionNote, player ? "" : "The orb only carries the clip; these shape the player.");
        Paint(_fromObject, Spatial.Value, player);
        Paint(_everywhere, !Spatial.Value, player);
        var kind = KindShown();
        Paint(_effect, kind == GroupKind.SoundEffect, player);
        Paint(_music, kind == GroupKind.Multimedia, player);
        SetText(_kindLabel, KindChosen.Value ? "Counts as" : "Counts as (guess)");
        SetColor(_heardLabel, player ? DashTheme.TextDim : DashTheme.TextMuted);
        SetColor(_kindLabel, player ? DashTheme.TextDim : DashTheme.TextMuted);
        SetColor(_volumeLabel, player ? DashTheme.TextDim : DashTheme.TextMuted);
        Paint(_loop, Loop.Value, player);
        Paint(_arrive, PlayOnArrival.Value, player);
        Paint(_normalize, Normalize.Value, true);
        SetText(_hint, Normalize.Value
            ? "Normalize raises the loudest peak to -1 dB as it lands, saved as a new WAV."
            : player ? (Spatial.Value ? "Fades with distance from the panel." : "Heard at one level by everyone in the world.") : "");
        if (_volume is { IsDestroyed: false } && _volume.Interactable.Value != player)
            _volume.Interactable.Value = player;
        RepaintVolume();

        int playable = 0;
        foreach (var clip in _clips)
            if (clip.Playable)
                playable++;
        Paint(_import, false, playable > 0, primary: playable > 0);
        string what = player ? (playable == 1 ? "player" : "players") : (playable == 1 ? "orb" : "orbs");
        SetText(_import?.Text, playable == 0 ? "Nothing to import" : playable == 1 ? $"Import {what}" : $"Import {playable} {what}");
        SetText(_status, Status(playable));
        SetColor(_status, playable < _clips.Count ? DashTheme.Warning : DashTheme.TextDim);
    }

    private void RepaintVolume()
    {
        float t = Clamp01(Volume.Value);
        SetText(_volumeValue, $"{MathF.Round(t * 100f)}%");
        SetColor(_volumeValue, Form.Value == AudioImportForm.Player ? DashTheme.TextDim : DashTheme.TextMuted);
        if (_previewOutput is { IsDestroyed: false })
            _previewOutput.Volume.Value = t * t;
    }

    private GroupKind? KindShown()
    {
        if (KindChosen.Value)
            return Kind.Value;
        GroupKind? guess = null;
        foreach (var clip in _clips)
        {
            if (!clip.Playable || !clip.Read)
                continue;
            if (guess != null && guess != clip.Guess)
                return null;
            guess = clip.Guess;
        }
        return guess;
    }

    private void RepaintBars(ClipInfo? shown)
    {
        var bars = shown?.Bars;
        string note = shown == null ? ""
            : shown.Remote ? "A link: it plays from the web, so there is nothing here to draw."
            : !shown.Playable ? $"{Format(shown)} files can't be played here yet."
            : shown.Problem != null ? shown.Problem
            : !shown.Read ? "Reading the clip..."
            : bars == null ? "Too long to draw; it imports all the same."
            : "";
        SetText(_waveNote, note);

        int lit = bars == null ? -2 : PlayedBars(shown!);
        if (lit == _barsLit)
            return;
        bool shape = _barsLit == -1 || lit == -2;
        _barsLit = lit;
        for (int i = 0; i < _bars.Count; i++)
        {
            var bar = _bars[i];
            if (bar == null || bar.IsDestroyed)
                continue;
            if (shape && bar.Slot.GetComponent<RectTransform>() is { } rect)
            {
                float h = bars != null && i < bars.Length ? bars[i] : 0f;
                SettingsUI.SetAnchors(rect, new float2(i / (float)BarCount, 0.5f - h * 0.5f), new float2((i + 1) / (float)BarCount, 0.5f + h * 0.5f));
            }
            var tint = bars == null ? color.Transparent : i < lit ? BarPlayed : BarRest;
            if (!bar.Color.Value.Equals(tint))
                bar.Color.Value = tint;
        }
    }

    private int PlayedBars(ClipInfo shown)
    {
        if (!_previewing || _previewIndex != _shown || shown.Duration <= 0.0 || World == null)
            return 0;
        double elapsed = World.Time.TotalTime - _previewStart;
        double at = Loop.Value ? elapsed % shown.Duration : System.Math.Min(elapsed, shown.Duration);
        return (int)System.Math.Round(at / shown.Duration * BarCount);
    }

    private ClipInfo? Shown => _clips.Count == 0 ? null : _clips[System.Math.Clamp(_shown, 0, _clips.Count - 1)];

    private static string Meta(ClipInfo? clip)
    {
        if (clip == null)
            return "";
        if (clip.Remote)
            return "Link " + Format(clip);
        var parts = new List<string>();
        if (clip.Read && clip.Duration > 0.0)
            parts.Add(Time(clip.Duration));
        if (clip.Read && clip.Channels > 0)
            parts.Add(clip.Channels == 1 ? "mono" : clip.Channels == 2 ? "stereo" : $"{clip.Channels} channels");
        if (clip.Read && clip.SampleRate > 0)
            parts.Add($"{clip.SampleRate / 1000f:0.#} kHz");
        parts.Add(Format(clip));
        if (clip.Read && float.IsFinite(clip.PeakDb))
            parts.Add($"peak {clip.PeakDb:0.0} dB");
        else if (clip.Bytes > 0)
            parts.Add($"{clip.Bytes / (1024f * 1024f):0.0} MB");
        return string.Join("  ·  ", parts);
    }

    private string Status(int playable)
    {
        int count = _clips.Count;
        if (count == 0)
            return "";
        if (playable == 0)
            return count == 1
                ? $"{Format(_clips[0])} files can't be played here yet. Bring it in as a raw file instead."
                : "None of these can be played here yet. Bring them in as raw files instead.";
        if (playable < count)
            return $"{playable} of {count} can be played; the rest are left out (bring them in as raw files).";
        return count > 1 ? $"{count} sounds, one set of choices for all of them." : "";
    }

    private static string Format(ClipInfo clip)
        => string.IsNullOrEmpty(clip.Extension) ? "Audio" : clip.Extension.TrimStart('.').ToUpperInvariant();

    private static string Time(double seconds)
    {
        int whole = (int)seconds;
        return whole >= 60 ? $"{whole / 60}:{whole % 60:00}" : $"0:{seconds:00.0}";
    }

    // READING

    private void StartReading()
    {
        if (_reading || _clips.Count == 0 || !CanInteract)
            return;
        _reading = true;
        var clips = _clips.ToArray();
        StartTask(async () =>
        {
            await WorldContext.ToBackground();
            foreach (var clip in clips)
                Read(clip);
            await WorldContext.ToWorld();
            if (IsDestroyed)
                return;
            _barsLit = -1;
            Repaint();
            LoadPreview();
        });
    }

    // Off the world thread: the clip's numbers and shape, the samples dropped once they are counted.
    private static void Read(ClipInfo clip)
    {
        if (clip.Remote || !clip.Playable)
        {
            clip.Read = true;
            return;
        }
        try
        {
            var info = new FileInfo(clip.Source);
            clip.Bytes = info.Exists ? info.Length : 0;
            if (!info.Exists)
            {
                clip.Problem = "The file is gone.";
                clip.Playable = false;
                return;
            }
            if (clip.Bytes > ReadLimitBytes)
                return;
            var bytes = File.ReadAllBytes(clip.Source);
            if (!AudioDecoders.TryDecode(new ReadOnlyMemory<byte>(bytes), clip.Extension, out var data, out var error) || data == null)
            {
                clip.Problem = "It would not decode: " + (error ?? "unknown format.");
                clip.Playable = false;
                return;
            }
            clip.Duration = data.Duration;
            clip.Channels = data.Channels;
            clip.SampleRate = data.SampleRate;
            clip.Bars = Shape(data, out float peak);
            clip.PeakDb = peak > 0f ? 20f * MathF.Log10(peak) : float.NegativeInfinity;
        }
        catch (Exception ex)
        {
            clip.Problem = "It could not be read: " + ex.Message;
        }
        finally
        {
            clip.Read = true;
        }
    }

    // The loudest sample in each of BarCount stretches, as a height from a 48 dB floor to full scale, so quiet
    // passages still show.
    private static float[] Shape(AudioClipData data, out float peak)
    {
        var bars = new float[BarCount];
        var samples = data.Samples;
        int channels = data.Channels;
        int frames = data.FrameCount;
        peak = 0f;
        for (int b = 0; b < BarCount; b++)
        {
            int from = (int)((long)frames * b / BarCount) * channels;
            int to = (int)((long)frames * (b + 1) / BarCount) * channels;
            float loudest = 0f;
            for (int i = from; i < to && i < samples.Length; i++)
            {
                float v = MathF.Abs(samples[i]);
                if (v > loudest)
                    loudest = v;
            }
            if (loudest > peak)
                peak = loudest;
            float db = loudest > 0f ? 20f * MathF.Log10(loudest) : -96f;
            bars[b] = System.Math.Clamp((db + 48f) / 48f, 0.04f, 1f);
        }
        return bars;
    }

    // PREVIEW
    // On this machine only, from the file itself: a local slot under the dialog with a clip, a cue player and a
    // flat output. Nobody else hears it and nothing about it is saved.

    private void EnsurePreview()
    {
        if (_previewCue is { IsDestroyed: false })
            return;
        _previewSlot = Slot.AddLocalSlot("Preview");
        _previewSlot.Persistent.Value = false;
        _previewClip = _previewSlot.AttachComponent<StaticAudioClip>();
        _previewCue = _previewSlot.AttachComponent<AudioCuePlayer>();
        _previewCue.PlayOnEnable.Value = false;
        _previewCue.Clip.Target = _previewClip;
        _previewOutput = _previewSlot.AttachComponent<AudioOutput>();
        _previewOutput.Source.Target = _previewCue;
        _previewOutput.Global.Value = true;
        _previewOutput.SpatialBlend.Value = 0f;
        _previewOutput.Spatialize.Value = false;
        _previewOutput.SetupAsUI();
        RepaintVolume();
    }

    // The shown clip loads as soon as it is shown, so the button plays it at once.
    private void LoadPreview()
    {
        var shown = Shown;
        if (shown is not { Playable: true, Remote: false } || !CanInteract)
            return;
        EnsurePreview();
        if (_previewIndex == _shown)
            return;
        _previewIndex = _shown;
        try
        {
            _previewClip!.URL.Value = new Uri(Path.GetFullPath(shown.Source));
        }
        catch (Exception ex)
        {
            Logger.Debug($"AudioImportDialog: no preview for {shown.Source}: {ex.Message}");
        }
    }

    private void TogglePreview()
    {
        if (_previewing)
        {
            StopPreview();
            Repaint();
            return;
        }
        LoadPreview();
        if (_previewCue == null || _previewIndex != _shown || World == null)
            return;
        _previewCue.CueLoop = Loop.Value;
        _previewCue.Cue();
        _previewStart = World.Time.TotalTime;
        _previewing = true;
        _barsLit = -1;
        Repaint();
    }

    private void StopPreview()
    {
        _previewCue?.StopCue();
        _previewing = false;
        _barsLit = -1;
    }

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        if (!_previewing)
            return;
        if (_previewCue is not { IsDestroyed: false } cue || !cue.IsCuePlaying)
        {
            _previewing = false;
            _barsLit = -1;
            Repaint();
            return;
        }
        RepaintBars(Shown);
    }

    // IMPORT

    public void RunImport()
    {
        if (!CanInteract || RefuseSpawn())
            return;
        Collect();
        var sources = new List<ClipInfo>();
        foreach (var clip in _clips)
            if (clip.Playable || clip.Remote && AudioDecoders.IsSupportedExtension(clip.Extension))
                sources.Add(clip);
        if (sources.Count == 0)
        {
            ShowUnsupported("None of these can be played here yet.\nUse \"As Raw File\" to drop the files into the world.");
            return;
        }

        StopPreview();
        bool orb = Form.Value == AudioImportForm.Orb;
        var options = new Options(Form.Value, Spatial.Value, KindChosen.Value ? Kind.Value : null, Loop.Value,
            PlayOnArrival.Value, Normalize.Value, Clamp01(Volume.Value) * Clamp01(Volume.Value));
        Logger.Debug($"AudioImportDialog: importing {sources.Count} sound(s) as {options.Form}");

        var target = ResolveTargetWorld();
        var basePos = Slot.GlobalPosition;
        var baseRot = Slot.GlobalRotation;
        int rowSize = (int)MathF.Max(1f, MathF.Ceiling(MathF.Sqrt(sources.Count)));
        float across = orb ? 0.14f : 0.75f, down = orb ? 0.16f : 0.3f;
        int index = 0;
        foreach (var clip in sources)
        {
            var grid = UniversalImporter.GridOffset(ref index, rowSize);
            var slot = target.AddSpawnSlot(target.RootSlot, clip.Name);
            slot.GlobalPosition = basePos + baseRot * new float3(grid.x * across, -grid.y * down, 0f);
            slot.GlobalRotation = baseRot;
            slot.GlobalScale = float3.One;
            var source = clip.Source;
            var label = Label(clip);
            slot.StartTask(() => BringIn(slot, source, label, options));
        }
        Slot.Destroy();
    }

    private sealed record Options(AudioImportForm Form, bool Spatial, GroupKind? Kind, bool Loop, bool PlayOnArrival, bool Normalize, float Gain);

    private static string Label(ClipInfo clip)
    {
        var name = Path.GetFileNameWithoutExtension(clip.Name);
        if (string.IsNullOrEmpty(name))
            name = clip.Name;
        return clip.Read && clip.Duration > 0.0 ? $"{name}\n{Time(clip.Duration)}" : name;
    }

    // The file goes into the local store first, so its ADDRESS travels and every machine fetches the bytes;
    // a link plays from where it is.
    private static async Task BringIn(Slot slot, string source, string label, Options options)
    {
        Uri uri;
        if (IsRemote(source))
        {
            uri = new Uri(source);
        }
        else
        {
            await WorldContext.ToBackground();
            string? stored = null;
            var db = Engine.Current?.LocalDB;
            if (db != null && File.Exists(source))
                stored = await db.ImportLocalAssetAsync(source, LocalDB.ImportLocation.Copy);
            uri = new Uri(string.IsNullOrEmpty(stored) ? Path.GetFullPath(source) : stored);
            await WorldContext.ToWorld();
        }
        if (slot.IsDestroyed)
            return;

        StaticAudioClip? clip;
        AudioPlayer? player = null;
        if (options.Form == AudioImportForm.Orb)
        {
            AudioClipOrb.Build(slot, uri, label);
            clip = slot.GetComponent<StaticAudioClip>();
        }
        else
        {
            // The user's favourite audio player when they have one, set to play this.
            player = await FavouriteSpawn.FillAsync<AudioPlayer>(slot, FavouriteKind.AudioPlayer);
            if (slot.IsDestroyed)
                return;
            player ??= slot.AttachComponent<AudioPlayer>();
            player.SetSource(uri);
            clip = player.Provider;
            if (player.Transport is { } transport)
                transport.Loop.Value = options.Loop;
            if (player.AudioOutput.Target is { } output)
            {
                output.Spatialize.Value = options.Spatial;
                output.SpatialBlend.Value = options.Spatial ? 1f : 0f;
                output.Volume.Value = options.Gain;
            }
            if (options.Kind is { } kind)
                player.SetKind(kind);
        }

        if (options.Normalize && clip != null && clip.Edit(AudioClipEdit.NormalizePeak(NormalizePeakDb)))
        {
            for (int frame = 0; frame < 3600 && !clip.IsDestroyed && clip.IsEditing; frame++)
                await WorldContext.NextUpdate();
        }

        if (player != null && options.PlayOnArrival)
        {
            // A shared playhead started before the clip has loaded would already be partway in when it arrives.
            for (int frame = 0; frame < 1200 && !player.IsDestroyed && player.Transport is { IsClipReady: false }; frame++)
                await WorldContext.NextUpdate();
            if (!player.IsDestroyed)
                player.Play();
        }
    }

    // HELPERS

    private static bool IsRemote(string source)
        => Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile
           && !string.Equals(uri.Scheme, "local", StringComparison.OrdinalIgnoreCase);

    private static string Extension(string source)
    {
        try
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile)
                return Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            return Path.GetExtension(source).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    private static string NameFor(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            var last = Path.GetFileName(uri.AbsolutePath);
            return string.IsNullOrEmpty(last) ? uri.Host : last;
        }
        var name = Path.GetFileName(source);
        return string.IsNullOrEmpty(name) ? source : name;
    }

    private static void SetText(Text? text, string content)
    {
        if (text != null && !text.IsDestroyed && !string.Equals(text.Content.Value, content, StringComparison.Ordinal))
            text.Content.Value = content;
    }

    private static void SetColor(Text? text, in color tint)
    {
        if (text != null && !text.IsDestroyed && !text.Color.Value.Equals(tint))
            text.Color.Value = tint;
    }

    private static float Clamp01(float value) => float.IsFinite(value) ? System.Math.Clamp(value, 0f, 1f) : 1f;
}
