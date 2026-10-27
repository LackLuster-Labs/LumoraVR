// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Import;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using Lumora.Core.Media;
using Lumora.Core.Persistence;
using ClipPlayer = Lumora.Core.Components.Audio.AudioClipPlayer;
using WorldAudioOutput = Lumora.Core.Components.Audio.AudioOutput;

namespace Lumora.Core.Components.Media;

// The whole in-world sound item: the clip, the thing that plays it, the emitter, and a small panel to
// drive it with. Attaching this to a bare slot builds all of it, so spawning a player is one
// AttachComponent and nothing has to know the part list.
//
// It owns no playback state. The player's SyncPlayback is the clock every peer already agrees on, so
// this is a face on top of it: every label is computed locally out of replicated state and written
// silently, because four peers broadcasting the same string at each other for a number all four can
// work out is traffic that buys nothing. -xlinka
[ComponentCategory("Audio")]
public sealed class AudioPlayer : Component, IPlayable
{
    public readonly SyncRef<StaticAudioClip> Clip;

    public readonly SyncRef<ClipPlayer> Player;

    // The emitter, aliased because the field carries the same name as its own type.
    public readonly SyncRef<WorldAudioOutput> AudioOutput;

    // Metres across. Everything else is authored in canvas pixels and scaled to match.
    public readonly Sync<float> PanelWidth;

    // What the clip is, which is what decides the mixer group it is counted against and how far it
    // carries. Classified from the clip once it loads and then left alone, so a person who sets it by
    // hand keeps their answer.
    public readonly Sync<Lumora.Core.Audio.AudioTypeGroup> TypeGroup;

    // False until the clip has been seen once. Replicated so a late joiner does not re-classify a
    // group somebody already corrected.
    public readonly Sync<bool> TypeClassified;

    // Authored in pixels and then scaled so the panel's width matches PanelWidth, the way the import
    // dialogs do it: layout numbers stay readable and the item can be resized by one field.
    private const float CanvasWidth = 900f;
    private const float CanvasHeight = 160f;

    // How far the backing plate sticks out past the panel, and how far behind it sits.
    private const float PlateMargin = 0.012f;
    private const float PlateDepth = 0.005f;

    private const float SeekStep = 5f;

    private Slot? _plateSlot;
    private Slot? _waveformSlot;
    private Meshes.AudioWaveformMesh? _waveformMesh;
    private Slot? _controlsSlot;

    private RoundedQuadMesh? _plateMesh;
    private BoxCollider? _plateCollider;
    private FontProvider? _font;

    private Text? _titleLabel;
    private Text? _statusLabel;
    private Text? _timeLabel;
    private Text? _playLabel;
    private Button? _playButton;
    private Button? _loopButton;
    private Button? _spatialButton;
    private Slider? _timeline;
    private Slider? _volumeSlider;

    private bool _built;
    private bool _wired;
    private float _labelPoll;
    private float _lastPanelWidth = -1f;
    private bool _toggleStateKnown;
    private bool _lastLoop;
    private bool _lastSpatialize;

    public AudioPlayer()
    {
        Clip = new SyncRef<StaticAudioClip>(this);
        Player = new SyncRef<ClipPlayer>(this);
        AudioOutput = new SyncRef<WorldAudioOutput>(this);
        PanelWidth = new Sync<float>(this, 0.6f);
        TypeGroup = new Sync<Lumora.Core.Audio.AudioTypeGroup>(this, Lumora.Core.Audio.AudioTypeGroup.SoundEffect);
        TypeClassified = new Sync<bool>(this, false);
    }

    public StaticAudioClip? Provider => Clip.Target;

    public ClipPlayer? Transport => Player.Target;

    // SOURCE

    public void SetSource(Uri url)
    {
        var provider = EnsureProvider();
        if (provider == null || url == null)
            return;
        provider.URL.Value = url;
    }

    public void SetSource(string source)
    {
        var url = VideoPlayer.ParseSource(source);
        if (url != null)
            SetSource(url);
    }

    // IPlayable
    // Forwarded rather than mirrored: two copies of a playhead is two playheads, and the player's is
    // the one the mixer and the other peers are reading.

    public float Position
    {
        get => Transport?.Position ?? 0f;
        set { var player = Transport; if (player != null) player.Position = value; }
    }

    public float Speed
    {
        get => Transport?.Speed.Value ?? 1f;
        set { var player = Transport; if (player != null) player.Speed.Value = value; }
    }

    public double ClipLength => Transport?.ClipLength ?? -1.0;

    public bool IsStreaming => false;

    public bool IsPlaying => Transport?.IsPlaying ?? false;

    public bool IsFinished => Transport?.IsFinished ?? false;

    public float NormalizedPosition
    {
        get => Transport?.NormalizedPosition ?? -1f;
        set { var player = Transport; if (player != null) player.NormalizedPosition = value; }
    }

    public bool Loop
    {
        get => Transport?.Loop.Value ?? false;
        set { var player = Transport; if (player != null) player.Loop.Value = value; }
    }

    [SyncMethod]
    public void Play() => Transport?.Play();

    [SyncMethod]
    public void Stop() => Transport?.Stop();

    [SyncMethod]
    public void Pause() => Transport?.Pause();

    [SyncMethod]
    public void Resume() => Transport?.Resume();

    [SyncMethod]
    public void TogglePlayback() => Transport?.TogglePlayback();

    // LIFECYCLE

    public override void OnAttach()
    {
        base.OnAttach();
        Build();
    }

    public override void OnStart()
    {
        base.OnStart();
        // A peer that received this item, or loaded it out of a save, already has every part: find
        // them rather than building a second set on top.
        Wire();
    }

    public override void Load(DataTreeNode node, LoadControl control)
    {
        base.Load(node, control);
        control.OnLoaded(Wire);
    }

    // BUILD

    private StaticAudioClip? EnsureProvider()
    {
        var provider = Clip.Target;
        if (provider != null && !provider.IsDestroyed)
            return provider;
        if (Slot == null || Slot.IsDestroyed)
            return null;
        provider = Slot.GetComponent<StaticAudioClip>() ?? Slot.AttachComponent<StaticAudioClip>();
        Clip.Target = provider;
        return provider;
    }

    private void Build()
    {
        if (_built || Slot == null || Slot.IsDestroyed)
            return;
        _built = true;

        var root = Slot.GetOrAttachComponent<ObjectRoot>();
        if (string.IsNullOrEmpty(root.ObjectName.Value))
            root.ObjectName.Value = "Audio Player";

        var grab = Slot.GetOrAttachComponent<Grabbable>();
        grab.AllowGrab.Value = true;
        grab.Scalable.Value = true;

        var provider = EnsureProvider();
        if (provider == null)
            return;

        var player = Player.Target;
        if (player == null || player.IsDestroyed)
        {
            player = Slot.GetComponent<ClipPlayer>() ?? Slot.AttachComponent<ClipPlayer>();
            Player.Target = player;
        }
        player.Clip.Target = provider;

        var audio = AudioOutput.Target;
        if (audio == null || audio.IsDestroyed)
        {
            audio = Slot.GetComponent<WorldAudioOutput>() ?? Slot.AttachComponent<WorldAudioOutput>();
            AudioOutput.Target = audio;
        }
        audio.Source.Target = player;
        audio.AudioTypeGroup.Value = TypeGroup.Value;
        audio.Spatialize.Value = true;
        audio.SpatialBlend.Value = 1f;
        audio.MinDistance.Value = 1f;
        audio.MaxDistance.Value = DistanceFor(TypeGroup.Value);
        audio.RolloffMode.Value = Lumora.Audio.AudioRolloffCurve.LogarithmicFadeOff;
        // A panel somebody carries across the room must not pitch-shift while they walk.
        audio.DopplerLevel.Value = 0f;

        BuildFont();
        BuildPlate();
        BuildControls(player, audio);
        BuildWaveform(audio);

        _wired = true;
        UpdateSurfaces();
    }

    private void BuildFont()
    {
        var fontSlot = Slot.FindChildOrAdd("Font");
        _font = fontSlot.GetOrAttachComponent<FontProvider>();
        // Text with no font draws nothing at all, so a face is picked here rather than left to
        // whatever the world happens to have registered.
        var url = ImportDialog.ResolveFontUrl(World) ?? DashTheme.FontRegular;
        _font.URL.Value = url;
        if (_font.FallbackURLs.Count == 0)
            _font.FallbackURLs.Add(DashTheme.FontRegular);
    }

    // The panel is a canvas and a canvas has no body to grab. The plate behind it is the item's
    // physical self: it is what a hand or a laser takes hold of, and it is what stops the controls
    // floating in mid air with nothing around them.
    private void BuildPlate()
    {
        _plateSlot = Slot.FindChildOrAdd("Plate");
        _plateSlot.LocalPosition.Value = new float3(0f, 0f, -PlateDepth);
        _plateMesh = _plateSlot.GetOrAttachComponent<RoundedQuadMesh>();
        _plateMesh.CornerRadius.Value = 0.02f;
        _plateMesh.CornerSegments.Value = 4;
        _plateMesh.DualSided.Value = false;

        // Glass, like every other panel body. An opaque slab reads as a placeholder next to them.
        var material = DashGlass.Attach(_plateSlot);

        var renderer = _plateSlot.GetOrAttachComponent<MeshRenderer>();
        renderer.Mesh.Target = _plateMesh;
        renderer.Material.Target = material;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;

        _plateCollider = _plateSlot.GetOrAttachComponent<BoxCollider>();
    }

    // THE PART THAT MOVES.
    //
    // A player that is playing looked exactly like a player that is not: same panel, same buttons,
    // a number counting up in small type. Sound is the one thing in a room with no shape of its own,
    // so it is given one - a live trace across the face of the panel, over the plate and under the
    // controls. It reads the OUTPUT rather than the clip, so it shows what is actually coming out:
    // paused is a flat line, muted is a flat line, and a loud passage is a loud picture. -xlinka
    private void BuildWaveform(WorldAudioOutput audio)
    {
        _waveformSlot = Slot.FindChildOrAdd("Waveform");
        // Between the plate and the controls, so the trace reads as being on the panel rather than
        // floating in front of it.
        _waveformSlot.LocalPosition.Value = new float3(0f, 0f, -PlateDepth * 0.4f);

        _waveformMesh = _waveformSlot.GetOrAttachComponent<Meshes.AudioWaveformMesh>();
        _waveformMesh.Source.Target = audio;
        _waveformMesh.Style.Value = Meshes.WaveformStyle.Waveform;
        _waveformMesh.Resolution.Value = 128;
        _waveformMesh.Smoothing.Value = 0.35f;

        var material = _waveformSlot.GetOrAttachComponent<UnlitMaterial>();
        material.TintColor.Value = ToHDR(DashTheme.Accent);
        material.Culling.Value = Culling.None;
        material.BlendMode.Value = BlendMode.Alpha;

        var renderer = _waveformSlot.GetOrAttachComponent<MeshRenderer>();
        renderer.Mesh.Target = _waveformMesh;
        renderer.Material.Target = material;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
    }

    private void BuildControls(ClipPlayer player, WorldAudioOutput audio)
    {
        _controlsSlot = Slot.FindChildOrAdd("Controls");
        var rect = _controlsSlot.GetOrAttachComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(0.5f, 0.5f);
        rect.AnchorMax.Value = new float2(0.5f, 0.5f);
        rect.OffsetMin.Value = new float2(CanvasWidth * -0.5f, CanvasHeight * -0.5f);
        rect.OffsetMax.Value = new float2(CanvasWidth * 0.5f, CanvasHeight * 0.5f);
        _controlsSlot.GetOrAttachComponent<Canvas>();

        // THE HOUSE BACKDROP, not a flat image: rounded, glass-tinted, outlined, the same one every
        // other panel in the product wears.
        var background = _controlsSlot.GetComponent<RoundedPanel>() ?? _controlsSlot.AttachComponent<RoundedPanel>();
        background.Color.Value = DashTheme.GlassBackdrop;
        background.CornerRadius.Value = DashTheme.RadiusPanel;
        background.OutlineColor.Value = DashTheme.OutlineStrong;
        background.OutlineThickness.Value = DashTheme.OutlineWidth;

        var content = _controlsSlot.FindChildOrAdd("Content");
        FillRect(content);
        // ITS OWN CHUNK, and this is what made the panel a black rectangle with nothing on it. The
        // canvas root carries the backdrop; without a chunk of its own the content is batched into
        // the root's chunk and drawn underneath the very thing it is supposed to sit on, so every
        // button, label and meter went behind an opaque panel. The video player hit this and fixed it
        // there; these two were built from the pattern that predates it. -xlinka
        content.GetOrAttachComponent<GraphicChunkRoot>();
        if (content.ChildCount > 0)
            return;

        var ui = new UIBuilder(content);
        ui.Font(_font);
        ui.FontSize(16f);
        ui.TextColor(DashTheme.Text);
        ui.ForegroundColor(DashTheme.Accent);
        ui.BackgroundColor(DashTheme.Surface);

        var column = ui.VerticalLayout(6f, 10f);
        column.ForceExpandWidth.Value = true;
        column.ForceExpandHeight.Value = false;
        column.MainAlignment.Value = MainAxisAlignment.Center;
        FillRect(ui.Current);

        BuildTitleRow(ui);
        BuildTimelineRow(ui, player);
        BuildTransportRow(ui, audio);

        ui.NestOut();
    }

    private void BuildTitleRow(UIBuilder ui)
    {
        ui.PushStyle();
        RowHeight(ui, 28f);
        ui.HorizontalLayout(8f);

        ui.PushStyle().MinWidth(0f).PreferredWidth(0f).FlexibleWidth(1f);
        _titleLabel = Label(ui, "TitleLabel", "No clip", 20f, DashTheme.Text, TextHorizontalAlignment.Left);
        ui.PopStyle();

        ui.PushStyle().MinWidth(260f).PreferredWidth(260f).FlexibleWidth(0f);
        _statusLabel = Label(ui, "StatusLabel", string.Empty, 14f, DashTheme.TextDim, TextHorizontalAlignment.Right);
        ui.PopStyle();

        ui.NestOut();
        ui.PopStyle();
    }

    private void BuildTimelineRow(UIBuilder ui, ClipPlayer player)
    {
        ui.PushStyle();
        RowHeight(ui, 26f);
        ui.HorizontalLayout(10f);

        _timeline = ui.Slider(0f, 0f, 1f, null, DashTheme.Field);
        _timeline.Slot.Name.Value = "Timeline";
        Metrics(_timeline.Slot, 0f, 0f, 1f, 22f, 22f, 0f);

        // The playhead drives the slider and the slider seeks back through the same link: a hand
        // write is the only thing that can move the value away from what the drive put there last
        // frame, so that difference IS the scrub, and a drive that read its own output would seek
        // every frame.
        var driver = _timeline.Slot.GetOrAttachComponent<PlaybackPositionDriver>();
        driver.Source.Target = player;
        driver.UseNormalizedPosition.Value = true;
        driver.WriteBack.Value = true;
        driver.Target.DriveTarget(_timeline.Value);

        ui.PushStyle().MinWidth(170f).PreferredWidth(170f).FlexibleWidth(0f);
        _timeLabel = Label(ui, "TimeLabel", "0:00 / 0:00", 15f, DashTheme.TextDim, TextHorizontalAlignment.Right);
        ui.PopStyle();

        ui.NestOut();
        ui.PopStyle();
    }

    private void BuildTransportRow(UIBuilder ui, WorldAudioOutput audio)
    {
        ui.PushStyle();
        RowHeight(ui, 38f);
        ui.HorizontalLayout(6f);

        ui.PushStyle().MinWidth(100f).PreferredWidth(100f).FlexibleWidth(0f);
        _playButton = ui.Button("Play", OnPlayPausePressed, DashTheme.Surface);
        _playButton.Slot.Name.Value = "PlayPause";
        _playLabel = ChildText(_playButton);

        ui.PushStyle().MinWidth(80f).PreferredWidth(80f);
        ui.Button("Stop", OnStopPressed, DashTheme.Surface);

        _loopButton = ui.Button("Loop", OnLoopPressed, DashTheme.Surface);
        _loopButton.Slot.Name.Value = "Loop";

        _spatialButton = ui.Button("3D", OnSpatialPressed, DashTheme.Surface);
        _spatialButton.Slot.Name.Value = "Spatialize";
        ui.PopStyle();
        ui.PopStyle();

        // Volume is heard, not measured: a linear fader spends most of its travel in a range nobody
        // can tell apart, so the slider is squared into the gain and rooted back out of it.
        ui.PushStyle().MinWidth(0f).PreferredWidth(0f).FlexibleWidth(1f);
        float initial = MathF.Sqrt(Clamp01(audio.Volume.Value));
        _volumeSlider = ui.Slider(initial, 0f, 1f, OnVolumeChanged, DashTheme.Field);
        _volumeSlider.Slot.Name.Value = "Volume";
        Metrics(_volumeSlider.Slot, 80f, 80f, 1f, 22f, 22f, 0f);
        ui.PopStyle();

        ui.PushStyle().MinWidth(70f).PreferredWidth(70f).FlexibleWidth(0f);
        ui.Button("-5s", OnSeekBackPressed, DashTheme.Surface);
        ui.Button("+5s", OnSeekForwardPressed, DashTheme.Surface);
        ui.PopStyle();

        ui.NestOut();
        ui.PopStyle();
    }

    // ACTIONS
    // Method groups on this component, not closures: a Button stores its action as target + method
    // name, so a closure would survive only in this process and a duplicated player would come up dead.

    [SyncMethod]
    public void OnPlayPausePressed(Button button, UIInteractionContext context) => TogglePlayback();

    [SyncMethod]
    public void OnStopPressed(Button button, UIInteractionContext context) => Stop();

    [SyncMethod]
    public void OnLoopPressed(Button button, UIInteractionContext context) => Loop = !Loop;

    [SyncMethod]
    public void OnSpatialPressed(Button button, UIInteractionContext context)
    {
        var audio = AudioOutput.Target;
        if (audio == null || audio.IsDestroyed)
            return;
        bool on = !audio.Spatialize.Value;
        audio.Spatialize.Value = on;
        audio.SpatialBlend.Value = on ? 1f : 0f;
    }

    [SyncMethod]
    public void OnSeekBackPressed(Button button, UIInteractionContext context) => SeekBy(-SeekStep);

    [SyncMethod]
    public void OnSeekForwardPressed(Button button, UIInteractionContext context) => SeekBy(SeekStep);

    [SyncMethod]
    public void OnVolumeChanged(Slider slider, float value)
    {
        var audio = AudioOutput.Target;
        if (audio == null || audio.IsDestroyed)
            return;
        float t = Clamp01(value);
        audio.Volume.Value = t * t;
    }

    private void SeekBy(float seconds)
    {
        var player = Transport;
        if (player == null || player.IsDestroyed)
            return;
        float target = player.Position + seconds;
        if (target < 0f)
            target = 0f;
        double length = player.ClipLength;
        if (length > 0.0 && target > (float)length)
            target = (float)length;
        player.Position = target;
    }

    // A track and a door click are not the same kind of sound and must not be counted against the
    // same volume. Everything imported used to come in as a sound effect, so turning the effects
    // slider down muted the music somebody put on, and the music slider did nothing at all.
    //
    // The rule is the length: ten seconds or more is a piece of music or a recording, under that is
    // an effect. A .wav is an effect whatever its length, because that is the format effects arrive
    // in and a long one is usually a loop.
    //
    // Every clip stays spatialized, short ones included. The player is a panel somebody is holding - a
    // visible object in the room - and a visible object whose sound comes from everywhere at once
    // reads as a bug. Music gets a much longer carry instead, so it fills a room rather than dying
    // a few steps from the panel. -xlinka
    private const float SoundEffectDistance = 25f;
    private const float MultimediaDistance = 120f;
    private const double MultimediaSeconds = 10.0;

    private static float DistanceFor(Lumora.Core.Audio.AudioTypeGroup group)
        => group == Lumora.Core.Audio.AudioTypeGroup.Multimedia ? MultimediaDistance : SoundEffectDistance;

    private void ClassifyClip()
    {
        if (TypeClassified.Value || World?.IsAuthority != true)
            return;

        var clip = Clip.Target?.Asset;
        if (clip == null || clip.Duration <= 0.0)
            return;

        bool wav = (Clip.Target?.URL.Value?.AbsolutePath ?? "").EndsWith(".wav", System.StringComparison.OrdinalIgnoreCase);
        var group = !wav && clip.Duration >= MultimediaSeconds
            ? Lumora.Core.Audio.AudioTypeGroup.Multimedia
            : Lumora.Core.Audio.AudioTypeGroup.SoundEffect;

        TypeClassified.Value = true;
        if (TypeGroup.Value != group)
            TypeGroup.Value = group;
        ApplyTypeGroup();
    }

    // A kind somebody chose, kept over the guess from the clip's length.
    public void SetKind(Lumora.Core.Audio.AudioTypeGroup group)
    {
        TypeClassified.Value = true;
        TypeGroup.Value = group;
        ApplyTypeGroup();
    }

    private void ApplyTypeGroup()
    {
        var audio = AudioOutput.Target;
        if (audio == null || audio.IsDestroyed)
            return;
        var group = TypeGroup.Value;
        audio.AudioTypeGroup.Value = group;
        audio.MaxDistance.Value = DistanceFor(group);
    }

    // PER-FRAME

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();

        if (!_wired)
            Wire();

        ClassifyClip();
        UpdateSurfaces();

        _labelPoll -= World?.Time?.Delta ?? 0f;
        if (_labelPoll > 0f)
            return;
        _labelPoll = 0.1f;
        UpdateLabels();
    }

    // Sizing is computed locally on every peer from a field they all read, and written silently: a
    // replicated mesh size would be four machines posting the same answer at each other.
    private void UpdateSurfaces()
    {
        float width = PanelWidth.Value;
        if (width < 0.05f)
            width = 0.05f;
        if (width == _lastPanelWidth)
            return;
        _lastPanelWidth = width;

        float scale = width / CanvasWidth;
        float height = CanvasHeight * scale;

        if (_controlsSlot != null && !_controlsSlot.IsDestroyed)
            _controlsSlot.LocalScale.SetValueSilently(new float3(scale, scale, scale), change: true);

        if (_plateMesh != null && !_plateMesh.IsDestroyed)
            _plateMesh.Size.SetValueSilently(new float2(width + PlateMargin * 2f, height + PlateMargin * 2f));

        // Wholly behind the face. Centred on the plate, its front sat 5 mm in front of the controls, and
        // the beam met the collider first: every button on the panel was dead and only the grab answered. -xlinka
        if (_plateCollider != null && !_plateCollider.IsDestroyed)
        {
            _plateCollider.Size.SetValueSilently(new float3(width + PlateMargin * 2f, height + PlateMargin * 2f, 0.02f));
            _plateCollider.Offset.SetValueSilently(new float3(0f, 0f, -0.01f));
        }

        // The trace spans the panel and stands as tall as the panel's middle band, which is the part
        // the controls leave empty.
        if (_waveformMesh != null && !_waveformMesh.IsDestroyed)
        {
            _waveformMesh.Width.SetValueSilently(width * 0.94f);
            _waveformMesh.Height.SetValueSilently(height * 0.42f);
            _waveformMesh.Thickness.SetValueSilently(System.Math.Max(height * 0.012f, 0.0015f));
        }
    }

    private void UpdateLabels()
    {
        var player = Transport;
        var provider = Provider;

        SetText(_titleLabel, TitleFor(provider));

        string? error = player?.LoadError;
        if (!string.IsNullOrEmpty(error))
        {
            SetText(_statusLabel, error!);
            SetColor(_statusLabel, DashTheme.Negative);
        }
        else if (provider != null && provider.IsLoadPending)
        {
            SetText(_statusLabel, "LOADING...");
            SetColor(_statusLabel, DashTheme.TextDim);
        }
        else
        {
            SetText(_statusLabel, string.Empty);
        }

        double length = player?.ClipLength ?? -1.0;
        SetText(_timeLabel, $"{FormatTime(player?.Position ?? 0f)} / {FormatTime(length > 0.0 ? length : 0.0)}");
        SetText(_playLabel, IsPlaying ? "Pause" : "Play");

        var audio = AudioOutput.Target;
        bool loop = Loop;
        bool spatial = audio != null && !audio.IsDestroyed && audio.Spatialize.Value;
        if (!_toggleStateKnown || loop != _lastLoop || spatial != _lastSpatialize)
        {
            _toggleStateKnown = true;
            _lastLoop = loop;
            _lastSpatialize = spatial;
            Highlight(_loopButton, loop);
            Highlight(_spatialButton, spatial);
        }

        if (_volumeSlider != null && !_volumeSlider.IsDestroyed && audio != null && !audio.IsDestroyed)
        {
            float wanted = MathF.Sqrt(Clamp01(audio.Volume.Value));
            if (MathF.Abs(wanted - _volumeSlider.Value.Value) > 0.01f)
                _volumeSlider.Value.SetValueSilently(wanted);
        }
    }

    private static string TitleFor(StaticAudioClip? provider)
    {
        var url = provider?.URL.Value;
        if (url == null)
            return "No clip";

        string name = System.IO.Path.GetFileName(url.IsAbsoluteUri && url.IsFile ? url.LocalPath : url.OriginalString);
        if (!string.IsNullOrEmpty(name))
            return name;
        return url.IsAbsoluteUri && !string.IsNullOrEmpty(url.Host) ? url.Host : url.OriginalString;
    }

    // RECONNECT

    // Everything here is a lookup, never a create: this runs on a peer that RECEIVED the item, and a
    // build on that side would give the world two of every part.
    private void Wire()
    {
        if (_wired || Slot == null || Slot.IsDestroyed)
            return;

        _plateSlot = Slot.FindChild("Plate", recursive: false);
        _controlsSlot = Slot.FindChild("Controls", recursive: false);
        // The panel arrives a slot at a time, so latching on the canvas alone would cache a screenful
        // of nulls and leave that peer's labels frozen for good. Wait for the rows.
        var content = _controlsSlot?.FindChild("Content", recursive: false);
        if (_controlsSlot == null || content == null || content.ChildCount == 0)
            return;

        _wired = true;
        _built = true;

        _plateMesh = _plateSlot?.GetComponent<RoundedQuadMesh>();
        _plateCollider = _plateSlot?.GetComponent<BoxCollider>();
        _waveformSlot = Slot.FindChild("Waveform", recursive: false);
        _waveformMesh = _waveformSlot?.GetComponent<Meshes.AudioWaveformMesh>();
        _font = Slot.FindChild("Font", recursive: false)?.GetComponent<FontProvider>();

        _titleLabel = FindText("TitleLabel");
        _statusLabel = FindText("StatusLabel");
        _timeLabel = FindText("TimeLabel");

        _timeline = _controlsSlot.FindChild("Timeline", recursive: true)?.GetComponent<Slider>();
        _volumeSlider = _controlsSlot.FindChild("Volume", recursive: true)?.GetComponent<Slider>();
        _playButton = FindButton("PlayPause");
        _loopButton = FindButton("Loop");
        _spatialButton = FindButton("Spatialize");
        _playLabel = ChildText(_playButton);
    }

    private Text? FindText(string name)
        => _controlsSlot?.FindChild(name, recursive: true)?.GetComponent<Text>();

    private Button? FindButton(string name)
        => _controlsSlot?.FindChild(name, recursive: true)?.GetComponent<Button>();

    // HELPERS

    // The slot name is load-bearing: a peer that received this item finds its labels by these names,
    // and the builder would otherwise hand every one of them the same "Text".
    private Text Label(UIBuilder ui, string name, string content, float size, in color tint, TextHorizontalAlignment alignment)
    {
        var text = ui.Text(content, size, tint);
        text.Slot.Name.Value = name;
        text.HorizontalAlignment.Value = alignment;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        text.WordWrap.Value = false;
        return text;
    }

    private static Text? ChildText(Button? button)
        => button == null || button.IsDestroyed
            ? null
            : button.Slot?.FindChild("Text", recursive: false)?.GetComponent<Text>();

    private static void RowHeight(UIBuilder ui, float height)
        => ui.MinHeight(height).PreferredHeight(height).FlexibleHeight(0f);

    private static void Metrics(Slot slot, float minWidth, float preferredWidth, float flexibleWidth,
        float minHeight, float preferredHeight, float flexibleHeight)
    {
        var element = slot.GetComponent<LayoutElement>() ?? slot.AttachComponent<LayoutElement>();
        element.MinWidth.Value = minWidth;
        element.PreferredWidth.Value = preferredWidth;
        element.FlexibleWidth.Value = flexibleWidth;
        element.MinHeight.Value = minHeight;
        element.PreferredHeight.Value = preferredHeight;
        element.FlexibleHeight.Value = flexibleHeight;
    }

    private static void FillRect(Slot slot)
    {
        var rect = slot.GetComponent<RectTransform>() ?? slot.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;
        rect.OffsetMin.Value = float2.Zero;
        rect.OffsetMax.Value = float2.Zero;
    }

    private static void SetText(Text? text, string content)
    {
        if (text == null || text.IsDestroyed)
            return;
        if (!string.Equals(text.Content.Value, content, StringComparison.Ordinal))
            text.Content.SetValueSilently(content);
    }

    private static void SetColor(Text? text, in color tint)
    {
        if (text == null || text.IsDestroyed)
            return;
        text.Color.SetValueSilently(tint);
    }

    // Local-only, like everything else the panel paints: the state behind it already replicates, so
    // every peer arrives at the same colour without anyone broadcasting it.
    private static void Highlight(Button? button, bool on)
    {
        if (button == null || button.IsDestroyed)
            return;
        color normal = on ? DashTheme.Accent : DashTheme.Surface;
        color hover = on ? DashTheme.AccentHover : DashTheme.SurfaceHover;
        color pressed = on ? DashTheme.AccentPressed : DashTheme.SurfacePressed;
        foreach (var driver in button.ColorDrivers)
        {
            if (driver == null || driver.IsDestroyed)
                continue;
            driver.NormalColor.SetValueSilently(normal);
            driver.HighlightColor.SetValueSilently(hover);
            driver.PressedColor.SetValueSilently(pressed);
            driver.DisabledColor.SetValueSilently(DashTheme.Field);
            driver.Apply();
        }
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0)
            return "0:00";
        int total = (int)seconds;
        int minutes = total / 60;
        int secs = total % 60;
        if (minutes < 60)
            return $"{minutes}:{secs:00}";
        int hours = minutes / 60;
        return $"{hours}:{minutes % 60:00}:{secs:00}";
    }

    private static colorHDR ToHDR(in color value) => new colorHDR(value.r, value.g, value.b, value.a);

    private static float Clamp01(float value) => value < 0f ? 0f : (value > 1f ? 1f : value);
}
