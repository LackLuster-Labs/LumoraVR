// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Audio;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Import;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using Lumora.Core.Networking.Streams;
using GroupKind = Lumora.Core.Audio.AudioTypeGroup;
using WorldAudioOutput = Lumora.Core.Components.Audio.AudioOutput;

namespace Lumora.Core.Components.Audio;

// An input device streamed into the world as an object you can pick up and put down. The stream is the
// speaker's (only a stream's owner may create one), the item is ordinary world content, and the panel
// is the handle everybody in the room gets on it.
//
// The capture is deliberately NOT the voice path: it takes the raw device samples, because a noise gate
// and a normalizer are shaped for speech and would eat the quiet half of anything else.
//
// What the room hears (live or paused, how loud, from the panel or everywhere, how far) is synced and
// only the owner writes it. The panel itself is LOCAL and every peer builds its own: the owner's has
// working controls, everybody else's shows the same state with the controls locked, plus the one thing
// that is theirs, whether they hear it at all. A replicated panel meant a guest's press was a write to
// somebody else's object, and every peer agreeing on which buttons are greyed out is not possible when
// the answer is different for each of them. -xlinka
[ComponentCategory("Audio")]
public sealed class AudioStreamController : Component
{
    public readonly SyncRef<AudioStream> Stream;

    public readonly SyncRef<WorldAudioOutput> AudioOutput;

    // The adapter between the two: an AudioOutput points at a component, and a stream is not one.
    public readonly SyncRef<UserAudioStream> Source;

    // Metres across. The panel is authored in canvas units and scaled to this.
    public readonly Sync<float> PanelWidth;

    // Which device is going out, and what kind of thing it is. Replicated, because everybody in the room
    // should be able to see what they are listening to rather than only the person who started it.
    public readonly Sync<string> DeviceName;
    public readonly Sync<AudioInputType> DeviceKind;

    public const float DefaultReach = 25f;
    public const float MinReach = 2f;
    public const float MaxReach = 60f;

    private const float CanvasWidth = 660f;
    private const float CanvasHeight = 300f;

    // The glass sits this far behind the face, and its collider further back still. A collider level
    // with the face, or in front of it, is what the beam meets first, and the beam then belongs to the
    // grab and never to a button: that is what left every control on this item dead. -xlinka
    private const float BackingMargin = 0.012f;
    private const float BackingDepth = 0.004f;
    private const float ColliderThickness = 0.012f;

    // The gauge reads peak level over this many dB below full scale.
    private const float GaugeRangeDb = 48f;
    private const float GaugeSweep = 270f;
    private const float GaugeStart = 135f;
    // Gauge travel per second on the way down; up is instant, a peak you missed is a peak you cannot see.
    private const float GaugeRelease = 1.2f;
    private const float SignalHold = 0.5f;

    private bool _built;
    private Slot? _panel;
    private Slot? _backing;
    private RoundedQuadMesh? _backingMesh;
    private BoxCollider? _backingCollider;
    private FontProvider? _font;
    private FontProvider? _strong;
    private FontProvider? _glyphs;

    private Text? _title;
    private Text? _device;
    private Text? _rolePill;
    private RoundedPanel? _rolePlate;
    private Text? _status;
    private Text? _levelText;
    private Text? _hint;
    private Text? _volumeLabel;
    private Text? _heardLabel;
    private Text? _reachLabel;
    private Text? _volumeValue;
    private Text? _reachValue;
    private Text? _playGlyph;
    private Slot? _pauseGlyph;
    private ArcSegment? _gaugeLevel;

    private Control? _live;
    private Control? _room;
    private Control? _everywhere;
    private Control? _listen;
    private RoundedPanel? _listenDot;
    private Control? _end;
    private Slider? _volume;
    private Slider? _reach;

    private float _lastPanelWidth = -1f;
    private float _poll;
    private float _gauge;
    private float _shownGauge = -1f;
    private float _peak;
    private long _lastSamples = -1;
    private float _sinceSignal = float.PositiveInfinity;

    // Mine alone, and never sent: whether the owner hears their own stream back, and whether anybody
    // else hears it at all. Off and on by default respectively.
    private bool _hearMyself;
    private bool _mutedHere;

    private readonly List<AudioListener> _listeners = new();

    private sealed class Control
    {
        public Slot Slot = null!;
        public RoundedPanel Plate = null!;
        public Button Button = null!;
        public Text? Text;
    }

    public AudioStreamController()
    {
        Stream = new SyncRef<AudioStream>(this);
        AudioOutput = new SyncRef<WorldAudioOutput>(this);
        Source = new SyncRef<UserAudioStream>(this);
        PanelWidth = new Sync<float>(this, 0.44f);
        DeviceName = new Sync<string>(this, string.Empty);
        DeviceKind = new Sync<AudioInputType>(this, AudioInputType.Unknown);
    }

    public User? Owner => Stream.Target?.Owner;

    public bool IsOwnedByLocalUser => Owner != null && ReferenceEquals(Owner, World?.LocalUser);

    // A source that has not reached this peer yet is not paused, it is unknown, and reads as live until it
    // says otherwise.
    public bool IsLive => Source.Target is not { IsDestroyed: false } source || !source.Paused.Value;

    public bool IsEverywhere => AudioOutput.Target is { IsDestroyed: false } output && (output.Global.Value || !output.Spatialize.Value);

    public float Reach => AudioOutput.Target is { IsDestroyed: false } output ? output.MaxDistance.Value : DefaultReach;

    // The slider's travel, 0 to 1. Volume is heard, not measured: a linear fader spends most of its
    // travel in a range nobody can tell apart, so the travel is squared into the gain.
    public float VolumeTravel => AudioOutput.Target is { IsDestroyed: false } output ? MathF.Sqrt(Clamp01(output.Volume.Value)) : 1f;

    // What the owner's machine is capturing from, as far as this machine knows. The owner knows the device
    // itself; everybody else has the replicated kind.
    public AudioInputType CapturedKind
        => IsOwnedByLocalUser && Source.Target?.PreferredInput is { } input ? input.InputType : DeviceKind.Value;

    // Monitoring stays off until the owner asks for it. A loopback device can be a muted monitor
    // or a separate audio route, so its device type cannot decide whether the owner may listen. -xlinka
    public bool CanHearMyself => IsOwnedByLocalUser;

    public bool HearsHere => IsOwnedByLocalUser ? _hearMyself && CanHearMyself : !_mutedHere;

    // SETUP

    // The synced half of the item, made once by whoever spawned it.
    public void Configure()
    {
        var root = Slot;
        if (root == null || root.IsDestroyed || World == null)
            return;

        var source = Source.Target;
        if (source == null || source.IsDestroyed)
        {
            source = root.GetComponent<UserAudioStream>() ?? root.AttachComponent<UserAudioStream>();
            Source.Target = source;
        }
        if (Stream.Target != null && source.Stream.Target != Stream.Target)
            source.Stream.Target = Stream.Target;

        var output = AudioOutput.Target;
        if (output == null || output.IsDestroyed)
        {
            output = root.GetComponent<WorldAudioOutput>() ?? root.AttachComponent<WorldAudioOutput>();
            AudioOutput.Target = output;
        }

        output.Source.Target = source;
        output.AudioTypeGroup.Value = GroupKind.Multimedia;
        ApplyHeard(output, everywhere: false);
        output.MinDistance.Value = 1f;
        output.MaxDistance.Value = DefaultReach;
        // An item somebody carries across the room must not pitch-shift while they walk.
        output.DopplerLevel.Value = 0f;
    }

    private static void ApplyHeard(WorldAudioOutput output, bool everywhere)
    {
        output.Spatialize.Value = !everywhere;
        output.SpatialBlend.Value = everywhere ? 0f : 1f;
        output.Global.Value = everywhere;
    }

    // OWNER ACTIONS. Every one of them answers only on the owner's machine. Anywhere else the control is
    // locked before it can be pressed, and these refuse anyway, quietly, rather than write at an object
    // that is not ours and be refused by the world.

    public bool SetLive(bool live)
    {
        if (!IsOwnedByLocalUser || Source.Target is not { IsDestroyed: false } source)
            return false;
        if (source.Paused.Value == !live)
            return true;
        source.Paused.Value = !live;
        Repaint();
        return true;
    }

    public bool SetVolumeTravel(float travel)
    {
        if (!IsOwnedByLocalUser || AudioOutput.Target is not { IsDestroyed: false } output)
            return false;
        float t = Clamp01(travel);
        output.Volume.Value = t * t;
        Repaint();
        return true;
    }

    public bool SetEverywhere(bool everywhere)
    {
        if (!IsOwnedByLocalUser || AudioOutput.Target is not { IsDestroyed: false } output)
            return false;
        ApplyHeard(output, everywhere);
        Repaint();
        return true;
    }

    public bool SetReach(float metres)
    {
        if (!IsOwnedByLocalUser || AudioOutput.Target is not { IsDestroyed: false } output)
            return false;
        if (!float.IsFinite(metres))
            return false;
        output.MaxDistance.Value = System.Math.Clamp(metres, MinReach, MaxReach);
        Repaint();
        return true;
    }

    public bool EndStream()
    {
        if (!IsOwnedByLocalUser || Slot is not { IsDestroyed: false } slot || slot.IsRootSlot)
            return false;
        try
        {
            slot.Destroy();
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        return true;
    }

    // LOCAL ACTIONS. Nothing about these leaves the machine.

    public bool SetHearMyself(bool on)
    {
        if (!CanHearMyself)
            return false;
        _hearMyself = on;
        Repaint();
        return true;
    }

    public bool SetMutedHere(bool muted)
    {
        if (IsOwnedByLocalUser)
            return false;
        _mutedHere = muted;
        Repaint();
        return true;
    }

    private void ToggleListen()
    {
        if (IsOwnedByLocalUser)
            SetHearMyself(!_hearMyself);
        else
            SetMutedHere(!_mutedHere);
    }

    // Reach runs along the slider exponentially, so the first half of it is the room you are in and the
    // second half is the building.
    private static float ReachToTravel(float metres)
        => Clamp01(MathF.Log(System.Math.Clamp(metres, MinReach, MaxReach) / MinReach) / MathF.Log(MaxReach / MinReach));

    private static float TravelToReach(float travel)
        => MinReach * MathF.Pow(MaxReach / MinReach, Clamp01(travel));

    // LIFECYCLE

    public override void OnStart()
    {
        base.OnStart();
        BuildPanel();
    }

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();

        if (!_built)
            BuildPanel();

        UpdateSurfaces();

        // Re-asserted every frame rather than set once: an ear can appear late (the local user's head is
        // built after their root) or be replaced, and a one-shot exclusion would miss it.
        var output = AudioOutput.Target;
        if (output != null && !output.IsDestroyed)
            ExcludeLocalListeners(output, !HearsHere);

        float delta = World?.Time?.Delta ?? 0f;
        UpdateGauge(delta);

        _poll -= delta;
        if (_poll > 0f)
            return;
        _poll = 0.1f;
        Repaint();
    }

    public override void OnDestroy()
    {
        var output = AudioOutput.Target;
        if (output != null && !output.IsDestroyed)
            ExcludeLocalListeners(output, false);
        DestroyLocal(_panel);
        DestroyLocal(_backing);
        DestroyLocal(_font?.Slot);
        _panel = null;
        _backing = null;
        base.OnDestroy();
    }

    private static void DestroyLocal(Slot? slot)
    {
        if (slot != null && !slot.IsDestroyed)
            slot.Destroy();
    }

    // Local exclusion, not the replicated list: whether I hear this is mine to decide, and writing it into
    // the item would decide it for everybody and be a write to somebody else's object besides.
    private void ExcludeLocalListeners(WorldAudioOutput output, bool excluded)
    {
        var manager = World?.Audio;
        if (manager == null)
            return;

        _listeners.Clear();
        manager.CollectLocalUserListeners(_listeners);
        for (int i = 0; i < _listeners.Count; i++)
            output.SetLocallyExcluded(_listeners[i], excluded);
        _listeners.Clear();
    }

    // LEVEL

    // The stream's own last peak: the capture's on the owner's machine, the decoder's everywhere else. A
    // ring that took no samples since the last frame is silent, whatever its last peak said, because quiet
    // stretches are never written and a paused stream writes nothing at all.
    private void UpdateGauge(float delta)
    {
        var stream = Stream.Target;
        float peak = 0f;
        if (stream != null && !stream.IsDestroyed && IsLive)
        {
            long samples = stream.Samples;
            if (samples != _lastSamples)
            {
                peak = Clamp01(stream.LastPeak);
                _lastSamples = samples;
            }
        }

        if (peak > 0.0001f)
            _sinceSignal = 0f;
        else
            _sinceSignal += delta;
        if (peak > 0f || _sinceSignal > SignalHold)
            _peak = peak;

        float target = _peak > 0.0001f ? Clamp01((20f * MathF.Log10(_peak) + GaugeRangeDb) / GaugeRangeDb) : 0f;
        _gauge = target >= _gauge ? target : MathF.Max(target, _gauge - GaugeRelease * delta);

        var level = _gaugeLevel;
        if (level == null || level.IsDestroyed || MathF.Abs(_gauge - _shownGauge) < 0.004f)
            return;
        _shownGauge = _gauge;
        bool lit = _gauge > 0.004f;
        if (level.Enabled.Value != lit)
            level.Enabled.Value = lit;
        if (lit)
        {
            level.ArcLength.Value = MathF.Max(1f, GaugeSweep * _gauge);
            var tint = _gauge > 0.94f ? DashTheme.Negative : _gauge > 0.8f ? DashTheme.Warning : DashTheme.Positive;
            if (!level.Tint.Value.Equals(tint))
                level.Tint.Value = tint;
        }
    }

    // SURFACES

    private void UpdateSurfaces()
    {
        float width = PanelWidth.Value;
        if (!float.IsFinite(width) || width < 0.05f)
            width = 0.05f;
        if (width == _lastPanelWidth || _panel == null)
            return;
        _lastPanelWidth = width;

        float scale = width / CanvasWidth;
        float height = CanvasHeight * scale;
        var size = new float2(width + BackingMargin * 2f, height + BackingMargin * 2f);

        if (!_panel.IsDestroyed)
            _panel.LocalScale.Value = new float3(scale, scale, scale);
        if (_backingMesh is { IsDestroyed: false })
            _backingMesh.Size.Value = size;
        if (_backingCollider is { IsDestroyed: false })
        {
            _backingCollider.Size.Value = new float3(size.x, size.y, ColliderThickness);
            _backingCollider.Offset.Value = new float3(0f, 0f, -ColliderThickness * 0.5f);
        }
    }

    // BUILD
    //
    // Local slots under the item, built on every peer that has the item, the owner's included. Nothing
    // here is saved or sent; a late joiner builds its own the moment the item reaches it.

    private void BuildPanel()
    {
        if (_built || Slot == null || Slot.IsDestroyed || World == null)
            return;
        _built = true;

        var fonts = Slot.AddLocalSlot("Stream Fonts");
        _font = Font(fonts, ImportDialog.ResolveFontUrl(World) ?? DashTheme.FontRegular);
        _strong = Font(fonts, DashTheme.FontSemibold);
        _glyphs = Font(fonts, DashTheme.FontMono);

        _backing = Slot.AddLocalSlot("Stream Backing");
        _backing.LocalPosition.Value = new float3(0f, 0f, -BackingDepth);
        _backingMesh = _backing.AttachComponent<RoundedQuadMesh>();
        _backingMesh.CornerRadius.Value = 0.02f;
        _backingMesh.CornerSegments.Value = 4;
        _backingMesh.DualSided.Value = false;
        var glass = DashGlass.Attach(_backing);
        var renderer = _backing.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = _backingMesh;
        renderer.Material.Target = glass;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
        _backingCollider = _backing.AttachComponent<BoxCollider>();

        _panel = Slot.AddLocalSlot("Stream Panel");
        var rect = _panel.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(0.5f, 0.5f);
        rect.AnchorMax.Value = new float2(0.5f, 0.5f);
        rect.OffsetMin.Value = new float2(CanvasWidth * -0.5f, CanvasHeight * -0.5f);
        rect.OffsetMax.Value = new float2(CanvasWidth * 0.5f, CanvasHeight * 0.5f);
        var canvas = _panel.AttachComponent<Canvas>();
        canvas.MaxViewDistance.Value = 30f;
        SettingsUI.Panel(_panel, DashTheme.GlassBackdrop, DashTheme.OutlineStrong, DashTheme.RadiusPanel);

        // Its own chunk, or the content batches into the root's and draws underneath the backdrop.
        var content = SettingsUI.Fill(_panel, "Content");
        content.AttachComponent<GraphicChunkRoot>();

        BuildDeck(content);
        BuildHeader(content);
        BuildRows(content);
        BuildFooter(content);

        _lastPanelWidth = -1f;
        UpdateSurfaces();
        Repaint();
    }

    private static FontProvider Font(Slot slot, Uri url)
    {
        var font = slot.AttachComponent<FontProvider>();
        font.URL.Value = url;
        font.FallbackURLs.Add(DashTheme.FontRegular);
        return font;
    }

    // THE DECK, on the left: the on-air button inside its level ring, what state the stream is in, how
    // loud it is, and whether this machine plays it.
    private void BuildDeck(Slot content)
    {
        var deck = Box(content, "Deck", 14f, 14f, 160f, 272f);
        SettingsUI.Panel(deck, DashTheme.Field.WithAlpha(0.6f), DashTheme.Outline, DashTheme.RadiusCard);

        var dial = Box(deck, "Dial", 10f, 12f, 140f, 140f);
        var track = SettingsUI.Fill(dial, "Track").AttachComponent<ArcSegment>();
        Gauge(track, DashTheme.TextMuted.WithAlpha(0.22f));
        track.ArcLength.Value = GaugeSweep;
        // Its own chunk: it moves every frame the music does, and that should cost one ring, not the panel.
        var levelSlot = SettingsUI.Fill(dial, "Gauge");
        levelSlot.AttachComponent<GraphicChunkRoot>();
        _gaugeLevel = levelSlot.AttachComponent<ArcSegment>();
        Gauge(_gaugeLevel, DashTheme.Positive);
        _gaugeLevel.ArcLength.Value = 1f;
        _gaugeLevel.Enabled.Value = false;

        var liveSlot = SettingsUI.Child(dial, "Live", new float2(0.5f, 0.5f), new float2(0.5f, 0.5f),
            new float2(-50f, -50f), new float2(50f, 50f));
        _live = NewControl(liveSlot, 50f, () => SetLive(!IsLive));
        _playGlyph = SettingsUI.FillLabel(liveSlot, "Play", _glyphs, 34f, DashTheme.Accent);
        _playGlyph.Content.Value = "▶";
        _pauseGlyph = SettingsUI.Fill(liveSlot, "Pause");
        PauseBar(_pauseGlyph, -10f);
        PauseBar(_pauseGlyph, 4f);

        _status = Label(deck, "Status", _strong, DashTheme.FontLabel, DashTheme.Positive, TextHorizontalAlignment.Center, 0f, 156f, 160f, 20f);
        _levelText = Label(deck, "Level", _glyphs, DashTheme.FontLabel, DashTheme.TextMuted, TextHorizontalAlignment.Center, 0f, 176f, 160f, 18f);

        var listenSlot = Box(deck, "Listen", 10f, 226f, 140f, 36f);
        _listen = NewControl(listenSlot, 18f, ToggleListen);
        var dot = Box(listenSlot, "Dot", 12f, 12f, 12f, 12f);
        _listenDot = SettingsUI.Panel(dot, DashTheme.TextMuted, color.Transparent, 6f);
        _listen.Text = Label(listenSlot, "Text", _font, DashTheme.FontSmall, DashTheme.Text, TextHorizontalAlignment.Left, 30f, 0f, 104f, 36f, shrink: true);
    }

    private static void Gauge(ArcSegment arc, in color tint)
    {
        arc.AngleStart.Value = GaugeStart;
        arc.InnerRadius.Value = 60f;
        arc.OuterRadius.Value = 67f;
        arc.CornerRadius.Value = 3.5f;
        arc.OutlineThickness.Value = 0f;
        arc.Tint.Value = tint;
    }

    private static void PauseBar(Slot parent, float x)
    {
        var bar = SettingsUI.Child(parent, "Bar", new float2(0.5f, 0.5f), new float2(0.5f, 0.5f),
            new float2(x, -16f), new float2(x + 6f, 16f));
        SettingsUI.Panel(bar, DashTheme.OnAccent, color.Transparent, 3f);
    }

    private void BuildHeader(Slot content)
    {
        var badge = Box(content, "Badge", 190f, 18f, 40f, 40f);
        SettingsUI.Panel(badge, DashTheme.IconPink.WithAlpha(0.2f), color.Transparent, 12f);
        Icon(badge, "stream-audio", 24f, DashTheme.IconPink);

        _title = Label(content, "Title", _strong, DashTheme.FontTitle, DashTheme.Text, TextHorizontalAlignment.Left, 240f, 16f, 290f, 26f);
        _device = Label(content, "Device", _font, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Left, 240f, 42f, 400f, 18f);

        var pill = Box(content, "Role", 540f, 20f, 106f, 22f);
        _rolePlate = SettingsUI.Panel(pill, DashTheme.AccentSoft, color.Transparent, 11f);
        _rolePill = SettingsUI.FillLabel(pill, "Text", _strong, DashTheme.FontLabel, DashTheme.Accent, shrinkToFit: true);

        var rule = Box(content, "Rule", 190f, 72f, 456f, 1f);
        rule.AttachComponent<Image>().Tint.Value = DashTheme.Divider;
    }

    private void BuildRows(Slot content)
    {
        _volumeLabel = RowLabel(content, "VolumeLabel", "Volume", 88f);
        _volume = RowSlider(content, "Volume", 88f, VolumeTravel, (_, value) => SetVolumeTravel(value));
        _volumeValue = Label(content, "VolumeValue", _glyphs, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Right, 590f, 88f, 56f, 34f);

        _heardLabel = RowLabel(content, "HeardLabel", "Heard", 134f);
        var strip = Box(content, "Heard", 272f, 134f, 374f, 34f);
        SettingsUI.Panel(strip, DashTheme.Field.WithAlpha(0.7f), DashTheme.Outline, DashTheme.RadiusControl + 1f);
        _room = Segment(strip, "Room", "In the room", 0, () => SetEverywhere(false));
        _everywhere = Segment(strip, "Everywhere", "Everywhere", 1, () => SetEverywhere(true));

        _reachLabel = RowLabel(content, "ReachLabel", "Reach", 180f);
        _reach = RowSlider(content, "Reach", 180f, ReachToTravel(Reach), (_, value) => SetReach(TravelToReach(value)));
        _reachValue = Label(content, "ReachValue", _glyphs, DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Right, 590f, 180f, 56f, 34f);
    }

    private void BuildFooter(Slot content)
    {
        _hint = Label(content, "Hint", _font, DashTheme.FontLabel, DashTheme.TextMuted, TextHorizontalAlignment.Left, 190f, 228f, 300f, 54f);
        _hint.WordWrap.Value = true;
        _hint.Ellipsis.Value = false;

        var endSlot = Box(content, "End", 506f, 238f, 140f, 36f);
        _end = NewControl(endSlot, DashTheme.RadiusControl, () => EndStream());
        _end.Text = SettingsUI.FillLabel(endSlot, "Text", _strong, DashTheme.FontSmall, DashTheme.Negative);
        _end.Text.Content.Value = "End stream";
        // Ending removes the item for good, so it goes on release: a press that slides off is not a yes.
        _end.Button.ActOnRelease.Value = true;
    }

    private Text RowLabel(Slot content, string name, string text, float top)
    {
        var label = Label(content, name, _font, DashTheme.FontBody, DashTheme.TextDim, TextHorizontalAlignment.Left, 190f, top, 76f, 34f);
        label.Content.Value = text;
        return label;
    }

    private Slider RowSlider(Slot content, string name, float top, float initial, Action<Slider, float> changed)
    {
        var host = Box(content, name, 272f, top, 312f, 34f);
        var ui = new UIBuilder(host);
        ui.ForegroundColor(DashTheme.Accent);
        var slider = ui.Slider(Clamp01(initial), 0f, 1f, null, DashTheme.Field);
        slider.ValueChanged += changed;
        var rect = SettingsUI.Rect(slider.Slot);
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;
        rect.OffsetMin.Value = float2.Zero;
        rect.OffsetMax.Value = float2.Zero;
        return slider;
    }

    private Control Segment(Slot strip, string name, string label, int index, Action onClick)
    {
        var slot = SettingsUI.Child(strip, name, new float2(index * 0.5f, 0f), new float2(index * 0.5f + 0.5f, 1f),
            new float2(index == 0 ? 3f : 1.5f, 3f), new float2(index == 0 ? -1.5f : -3f, -3f));
        var control = NewControl(slot, DashTheme.RadiusControl - 1f, onClick);
        control.Text = SettingsUI.FillLabel(slot, "Text", _font, DashTheme.FontSmall, DashTheme.TextDim, shrinkToFit: true);
        control.Text.Content.Value = label;
        return control;
    }

    // A plate that lights under the pointer. The plate goes on before the button so the button does not
    // adopt it as an Image; its colours are then the driver's, set in Paint.
    private Control NewControl(Slot slot, float radius, Action onClick)
    {
        var plate = SettingsUI.Panel(slot, DashTheme.Surface, DashTheme.Outline, radius);
        var button = slot.AttachComponent<Button>();
        button.Clicked += (_, context) =>
        {
            if (context.Actor == null || ReferenceEquals(context.Actor, World?.LocalUser))
                onClick();
        };
        button.AddColorDriver(plate.Color, DashTheme.Surface, InteractionColorMode.Direct);
        return new Control { Slot = slot, Plate = plate, Button = button };
    }

    private static Image Icon(Slot host, string icon, float size, in color tint)
    {
        var slot = SettingsUI.Child(host, "Icon", new float2(0.5f, 0.5f), new float2(0.5f, 0.5f),
            new float2(-size * 0.5f, -size * 0.5f), new float2(size * 0.5f, size * 0.5f));
        var picture = slot.AttachComponent<ImageProvider>();
        picture.GenerateMipmaps.Value = false;
        picture.AllowCompression.Value = false;
        picture.MaxSizeOverride.Value = -1;
        picture.URL.Value = new Uri("res://Assets/Textures/Icons/" + icon + ".svg");
        var image = slot.AttachComponent<Image>();
        image.Texture.Target = picture;
        image.Tint.Value = tint;
        return image;
    }

    // PAINT

    private void Repaint()
    {
        if (_panel == null || _panel.IsDestroyed)
            return;

        var owner = Owner;
        bool mine = IsOwnedByLocalUser;
        bool live = IsLive;
        bool everywhere = IsEverywhere;
        float reach = Reach;
        string ownerName = owner?.UserName.Value ?? string.Empty;

        SetText(_title, ownerName.Length > 0 ? $"{ownerName}'s stream" : "Audio stream");
        SetText(_device, DeviceLine());

        SetText(_rolePill, mine ? "Yours" : "View only");
        SetColor(_rolePill, mine ? DashTheme.Accent : DashTheme.TextDim);
        if (_rolePlate is { IsDestroyed: false })
            SettingsUI.SetFill(_rolePlate, mine ? DashTheme.AccentSoft : DashTheme.Surface.WithAlpha(0.8f));

        bool signal = live && _sinceSignal <= SignalHold;
        SetText(_status, !live ? "PAUSED" : signal ? "ON AIR" : "NO SIGNAL");
        SetColor(_status, !live ? DashTheme.Warning : signal ? DashTheme.Positive : DashTheme.TextMuted);
        SetText(_levelText, signal && _peak > 0.0001f ? $"{20f * MathF.Log10(_peak):0} dB" : "-");

        PaintRound(_live, live, mine);
        SetActive(_playGlyph?.Slot, !live);
        SetActive(_pauseGlyph, live);
        SetColor(_playGlyph, mine ? DashTheme.Accent : DashTheme.TextMuted);

        Paint(_room, !everywhere, mine);
        Paint(_everywhere, everywhere, mine);

        PaintSlider(_volume, VolumeTravel, mine);
        PaintSlider(_reach, ReachToTravel(reach), mine && !everywhere);
        SetText(_volumeValue, $"{MathF.Round(VolumeTravel * 100f)}%");
        SetText(_reachValue, $"{MathF.Round(reach)} m");
        SetColor(_volumeLabel, mine ? DashTheme.TextDim : DashTheme.TextMuted);
        SetColor(_heardLabel, mine ? DashTheme.TextDim : DashTheme.TextMuted);
        SetColor(_reachLabel, mine && !everywhere ? DashTheme.TextDim : DashTheme.TextMuted);
        SetColor(_volumeValue, mine ? DashTheme.TextDim : DashTheme.TextMuted);
        SetColor(_reachValue, !everywhere ? DashTheme.TextDim : DashTheme.TextMuted);

        bool hears = HearsHere;
        bool canListen = mine ? CanHearMyself : true;
        Paint(_listen, mine ? hears : _mutedHere, canListen);
        SetText(_listen?.Text, mine ? "Hear it myself" : _mutedHere ? "Muted for me" : "Mute for me");
        if (_listenDot is { IsDestroyed: false })
        {
            bool lit = mine ? hears : _mutedHere;
            SettingsUI.SetFill(_listenDot, !canListen ? DashTheme.TextMuted.WithAlpha(0.5f) : lit ? (mine ? DashTheme.Positive : DashTheme.Negative) : DashTheme.TextMuted);
        }

        SetActive(_end?.Slot, mine);
        if (_end != null)
            PaintEnd(_end);
        if (_hint is { IsDestroyed: false })
        {
            var rect = SettingsUI.Rect(_hint.Slot);
            SettingsUI.SetOffsets(rect, new float2(190f, -282f), new float2(mine ? 490f : 646f, -228f));
        }
        SetText(_hint, Hint(mine, everywhere, reach, ownerName));
    }

    private string DeviceLine()
    {
        string device = DeviceName.Value ?? string.Empty;
        string kind = AudioInputTypes.Describe(DeviceKind.Value);
        string detail = string.Empty;
        if (Stream.Target is OpusStream opus)
            detail = $"{opus.BitRate.Value / 1000} kbps {(opus.Channels.Value == 2 ? "stereo" : "mono")}";
        var parts = new List<string>(3);
        if (device.Length > 0)
            parts.Add(device);
        if (DeviceKind.Value != AudioInputType.Unknown && !string.Equals(kind, device, StringComparison.OrdinalIgnoreCase))
            parts.Add(kind);
        if (detail.Length > 0)
            parts.Add(detail);
        return string.Join("  ·  ", parts);
    }

    private string Hint(bool mine, bool everywhere, float reach, string ownerName)
    {
        if (!mine)
        {
            string who = ownerName.Length > 0 ? ownerName : "Its owner";
            return _mutedHere
                ? "You muted this stream for yourself. Nobody else is affected."
                : $"{who} sets how this stream plays. Mute for me only changes what you hear.";
        }
        if (!IsLive)
            return "Paused: nothing is being sent. Press the button to go back on air.";
        if (CapturedKind == AudioInputType.Loopback)
            return "Hear it myself monitors this device here. Use a separate output to keep playback out of the capture.";
        return everywhere
            ? "Everyone in the world hears this at the same level."
            : $"Heard from this panel, fading out by {MathF.Round(reach)} m.";
    }

    private static void Paint(Control? control, bool on, bool enabled)
    {
        if (control == null || control.Button.IsDestroyed)
            return;
        color normal = on ? DashTheme.AccentSoft : DashTheme.Surface.WithAlpha(0.55f);
        color hover = on ? DashTheme.Accent.WithAlpha(0.28f) : DashTheme.SurfaceHover;
        color disabled = on ? DashTheme.Accent.WithAlpha(0.12f) : DashTheme.Field.WithAlpha(0.5f);
        Drive(control.Button, normal, hover, DashTheme.Accent.WithAlpha(0.34f), disabled);
        Interactable(control.Button, enabled);
        SettingsUI.SetPaint(control.Plate, control.Plate.Color.Value, on ? (enabled ? DashTheme.Accent : DashTheme.Accent.WithAlpha(0.45f)) : DashTheme.Outline, on ? 2f : DashTheme.OutlineWidth);
        SetColor(control.Text, !enabled ? (on ? DashTheme.TextDim : DashTheme.TextMuted) : on ? DashTheme.Text : DashTheme.TextDim);
    }

    private static void PaintRound(Control? control, bool live, bool enabled)
    {
        if (control == null || control.Button.IsDestroyed)
            return;
        if (live)
            Drive(control.Button, DashTheme.Accent, DashTheme.AccentHover, DashTheme.AccentPressed, DashTheme.Accent.WithAlpha(0.5f));
        else
            Drive(control.Button, DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.Surface.WithAlpha(0.6f));
        Interactable(control.Button, enabled);
        SettingsUI.SetPaint(control.Plate, control.Plate.Color.Value, live ? color.Transparent : DashTheme.OutlineStrong, DashTheme.OutlineWidth);
    }

    private static void PaintEnd(Control control)
    {
        if (control.Button.IsDestroyed)
            return;
        Drive(control.Button, DashTheme.Negative.WithAlpha(0.1f), DashTheme.Negative.WithAlpha(0.22f), DashTheme.Negative.WithAlpha(0.32f), DashTheme.Field);
        SettingsUI.SetPaint(control.Plate, control.Plate.Color.Value, DashTheme.Negative.WithAlpha(0.45f), DashTheme.OutlineWidth);
    }

    private static void PaintSlider(Slider? slider, float travel, bool enabled)
    {
        if (slider == null || slider.IsDestroyed)
            return;
        Interactable(slider, enabled);
        // Pushed only while nobody is dragging it, or the knob fights the hand.
        if (!slider.IsPressed.Value && MathF.Abs(slider.Value.Value - travel) > 0.005f)
        {
            slider.Value.Value = travel;
            slider.UpdateHandleDrives();
        }
        var fill = slider.Slot.FindChild("Track", recursive: false)?.FindChild("Fill", recursive: false)?.GetComponent<Image>();
        if (fill != null)
            SettingsUI.SetTint(fill, enabled ? DashTheme.Accent : DashTheme.TextMuted.WithAlpha(0.7f));
    }

    private static void Drive(Button button, in color normal, in color hover, in color pressed, in color disabled)
    {
        foreach (var driver in button.ColorDrivers)
        {
            if (driver == null || driver.IsDestroyed)
                continue;
            if (driver.NormalColor.Value.Equals(normal) && driver.HighlightColor.Value.Equals(hover)
                && driver.PressedColor.Value.Equals(pressed) && driver.DisabledColor.Value.Equals(disabled))
                continue;
            driver.NormalColor.Value = normal;
            driver.HighlightColor.Value = hover;
            driver.PressedColor.Value = pressed;
            driver.DisabledColor.Value = disabled;
            driver.Apply();
        }
    }

    private static void Interactable(InteractionElement element, bool value)
    {
        if (element.Interactable.Value != value)
            element.Interactable.Value = value;
    }

    // HELPERS

    private static Slot Box(Slot parent, string name, float x, float top, float width, float height)
        => SettingsUI.Child(parent, name, new float2(0f, 1f), new float2(0f, 1f),
            new float2(x, -(top + height)), new float2(x + width, -top));

    private static Text Label(Slot parent, string name, IAssetProvider<FontSet>? font, float size, in color tint,
        TextHorizontalAlignment align, float x, float top, float width, float height, bool shrink = false)
        => SettingsUI.Label(parent, name, font, size, tint, align, new float2(0f, 1f), new float2(0f, 1f),
            new float2(x, -(top + height)), new float2(x + width, -top), shrink);

    private static void SetActive(Slot? slot, bool active)
    {
        if (slot != null && !slot.IsDestroyed && slot.ActiveSelf.Value != active)
            slot.ActiveSelf.Value = active;
    }

    private static void SetText(Text? text, string content)
    {
        if (text == null || text.IsDestroyed)
            return;
        if (!string.Equals(text.Content.Value, content, StringComparison.Ordinal))
            text.Content.Value = content;
    }

    private static void SetColor(Text? text, in color tint)
    {
        if (text != null && !text.IsDestroyed && !text.Color.Value.Equals(tint))
            text.Color.Value = tint;
    }

    private static float Clamp01(float value)
        => float.IsNaN(value) ? 0f : (value < 0f ? 0f : (value > 1f ? 1f : value));
}
