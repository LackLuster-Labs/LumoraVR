// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Localization;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

// AVATAR ARRIVAL
//
// While somebody's avatar is still coming in on THIS machine, its meshes in flight from whoever owns them
// and its textures behind them, the avatar stays out of sight until it can be shown whole, so nobody sees
// pieces popping in one by one, a checker body or a rig standing in its bind pose. A figure of light
// stands where the person is instead: head and hands where theirs are, a body worked out from
// those, filling from the floor up as the avatar arrives, with a readout over it saying what is happening,
// how far along, and the avatar's name. When the avatar is ready the light climbs off the figure from the
// feet up and the avatar is there underneath.
//
// One per user, on every peer, beside the name plate and like it LOCAL top to bottom: each viewer measures
// what is on its own machine, so two people watching a third arrive can be at different points. Nothing
// here replicates or saves. The avatar is hidden only from this machine's screens (Slot.SetLocallyHidden);
// it still runs, collides and drives its rig, so it is in the right pose the frame it shows.
//
// An avatar that is all here when it is put on never sees any of this. One that takes a moment is held out
// of sight a moment; the figure only appears past a short grace, so a quick load is a short blink rather
// than a flash of light. A mesh that cannot be had fails the arrival: the figure turns amber and stays, and
// says why, until a cloud part that was only late lands after all (LateCloudParts) and the arrival carries on.
// A texture that cannot be had does not; the surface draws without it, as it would anywhere.
//
// Cost: idle it is one registry lookup a frame. Arriving it walks the avatar once (and again at most once a
// second if it changes shape), asks its parts where they stand five times a second, and poses nine pieces
// a frame, writing only what moved. Every stand-in in a world draws with one material. -xlinka
[ComponentCategory("Hidden")]
public sealed class AvatarArrival : Component
{
    public const string RootSlotName = "Avatar Arrival";

    private const float GraceSeconds = 0.5f;
    private const float FadeInSeconds = 0.4f;
    private const float DissolveSeconds = 0.9f;
    private const float EvaluateSeconds = 0.2f;
    private const float FailedEvaluateSeconds = 1f;
    private const float RetakeSeconds = 1f;

    // Every part in and still not drawing after this long: show it anyway. A gate that never opens must
    // not keep somebody invisible for the rest of the session.
    private const float StuckGateSeconds = 10f;

    private const int MaxNameChars = 24;

    private enum Phase { Idle, Holding, Showing, Revealing, Failed }

    private readonly AvatarArrivalCensus _census = new();
    private AvatarEquipManager? _manager;
    private NameplateManager? _nameplate;
    private float _nameplateSearchIn;
    private Slot? _avatar;
    private Slot? _hidden;
    private AvatarArrivalFigure? _figure;
    private AvatarArrivalPlate? _plate;
    private bool _visualsOn;

    private Phase _phase;
    private float _phaseAge;
    private float _evaluateIn;
    private float _retakeIn;
    private int _censusEpoch;
    private float _stuckFor;
    private bool _readySeen;

    private float _shown;
    private float _bar;
    private float _presence;

    private AvatarArrivalCensus.Stage _textStage = (AvatarArrivalCensus.Stage)(-1);
    private int _textPercent = -1;
    private int _textParts = -1;
    private int _textDone = -1;
    private long _textTenths = -1;
    private int _textMissing = -1;
    private int _textBroken = -1;
    private string _stateText = string.Empty;
    private string _percentText = string.Empty;
    private string _detailText = string.Empty;
    private string _avatarName = string.Empty;

    // Read by checks: whether this machine is holding the avatar out of sight, whether the figure is up,
    // and the readout as it stands.
    public bool IsHoldingAvatar => _hidden != null;
    public bool IsStandInShown => _visualsOn;
    public float Progress => _shown;
    public string StateText => _stateText;
    public string PercentText => _percentText;
    public string DetailText => _detailText;

    // Find-or-build beside the user's equip manager, on every peer.
    public static AvatarArrival? Ensure(AvatarEquipManager manager)
    {
        var slot = manager?.Slot;
        if (slot == null || slot.IsDestroyed || manager!.World == null)
            return null;

        var existing = slot.FindChild(RootSlotName, recursive: false);
        var found = existing?.GetComponent<AvatarArrival>();
        if (found != null && !found.IsDestroyed)
            return found;
        existing?.Destroy();

        var root = slot.AddLocalSlot(RootSlotName);
        root.Persistent.Value = false;
        root.GetOrAttachComponent<ImmutableComponent>();
        var arrival = root.AttachComponent<AvatarArrival>();
        arrival.Bind(manager);
        return arrival;
    }

    // An equip made on this machine says so at once, so a just-spawned avatar is held before the frame
    // it would first be drawn in. One that replicated in is seen by the update that follows its arrival,
    // which also comes before any frame draws it.
    private void Bind(AvatarEquipManager? manager)
    {
        if (ReferenceEquals(manager, _manager))
            return;
        if (_manager != null && !_manager.IsDestroyed)
            _manager.OnAvatarChanged -= OnAvatarChanged;
        _manager = manager;
        if (manager != null)
            manager.OnAvatarChanged += OnAvatarChanged;
    }

    // A fresh equip is looked at again even when it is the avatar already followed: taken off and put back
    // on within one update, it may be carrying parts it did not have before.
    private void OnAvatarChanged(Slot _)
    {
        if (!IsDestroyed && _manager != null)
            Follow(_manager, again: _phase == Phase.Idle);
    }

    private void Follow(AvatarEquipManager manager, bool again = false)
    {
        var avatar = EngineSettings.AvatarArrival ? manager.EquippedAvatar : null;
        if (avatar != null && avatar.IsDestroyed)
            avatar = null;
        if (again || !ReferenceEquals(avatar, _avatar))
            Track(avatar);
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        var manager = _manager;
        if (manager == null || manager.IsDestroyed)
        {
            Bind(Slot?.Parent?.GetComponent<AvatarEquipManager>());
            manager = _manager;
            if (manager == null)
                return;
        }

        Follow(manager);
        if (_phase == Phase.Idle)
            return;

        _phaseAge += delta;
        _retakeIn -= delta;

        switch (_phase)
        {
            case Phase.Holding:
                if (Step(delta))
                {
                    Release();
                    return;
                }
                if (_phaseAge >= GraceSeconds)
                    Enter(Phase.Showing);
                break;

            case Phase.Showing:
            case Phase.Failed:
                if (Step(delta))
                    Reveal();
                break;

            case Phase.Revealing:
                if (_phaseAge >= DissolveSeconds)
                {
                    Retire();
                    return;
                }
                break;
        }

        if (_visualsOn)
            Animate(manager, delta);
    }

    // A different avatar, or none. Whatever this was holding back is let go first.
    private void Track(Slot? avatar)
    {
        Unhide();
        bool wasShowing = _phase is Phase.Showing or Phase.Failed;
        _avatar = avatar;
        _census.Clear();
        _shown = _bar = 0f;
        _readySeen = false;
        _stuckFor = 0f;
        _textStage = (AvatarArrivalCensus.Stage)(-1);
        _textPercent = _textParts = _textDone = _textMissing = _textBroken = -1;
        _textTenths = -1;

        if (avatar == null)
        {
            Retire();
            return;
        }

        _census.Take(avatar);
        _censusEpoch = Slot.StructureEpoch;
        _retakeIn = RetakeSeconds;
        _census.Evaluate(World);
        if (_census.Ready)
        {
            Retire();
            return;
        }

        _avatarName = NameOf(avatar);
        _hidden = avatar;
        avatar.SetLocallyHidden(true);
        _evaluateIn = EvaluateSeconds;
        // Swapping from one arriving avatar to another keeps the figure up rather than blinking it.
        Enter(wasShowing ? Phase.Showing : Phase.Holding);
        Readout();
    }

    // Asks the census on its cadence. True when the avatar can be shown: ready on two looks in a row, so
    // a surface the platform is still swapping its material on is not caught mid-swap.
    private bool Step(float delta)
    {
        _evaluateIn -= delta;
        if (_evaluateIn > 0f)
            return false;
        float interval = _phase == Phase.Failed ? FailedEvaluateSeconds : EvaluateSeconds;
        _evaluateIn = interval;

        var avatar = _avatar;
        if (avatar == null || avatar.IsDestroyed)
            return false;
        int epoch = Slot.StructureEpoch;
        if (epoch != _censusEpoch && _retakeIn <= 0f)
        {
            _census.Take(avatar);
            _censusEpoch = epoch;
            _retakeIn = RetakeSeconds;
        }
        _census.Evaluate(World);
        // 100% is the reveal itself, which waits for a second look; until then it is 99 at most.
        _shown = MathF.Max(_shown, MathF.Min(_census.Progress, 0.99f));

        _stuckFor = _census.AssetsIn && _census.State == AvatarArrivalCensus.Stage.Building ? _stuckFor + interval : 0f;
        bool ready = _census.Ready || _stuckFor >= StuckGateSeconds;

        if (_census.BlockingFailure && _phase == Phase.Showing)
            Enter(Phase.Failed);
        else if (!_census.BlockingFailure && _phase == Phase.Failed)
            Enter(Phase.Showing);

        if (!ready)
        {
            _readySeen = false;
            Readout();
            return false;
        }
        if (!_readySeen)
        {
            _readySeen = true;
            Readout();
            return false;
        }
        return true;
    }

    private void Enter(Phase phase)
    {
        _phase = phase;
        _phaseAge = 0f;
        if (phase is Phase.Showing or Phase.Failed)
            ShowVisuals();
    }

    // Ready before the figure ever came up: just let the avatar be seen.
    private void Release()
    {
        Unhide();
        Retire();
    }

    // The avatar shows and the figure leaves over it. Only now does the readout say 100%.
    private void Reveal()
    {
        Unhide();
        _shown = 1f;
        _textStage = (AvatarArrivalCensus.Stage)(-1);
        Readout(revealing: true);
        if (!_visualsOn)
        {
            Retire();
            return;
        }
        _presence = 1f;
        Enter(Phase.Revealing);
    }

    private void Retire()
    {
        _phase = Phase.Idle;
        _phaseAge = 0f;
        _presence = 0f;
        HideVisuals();
    }

    private void Unhide()
    {
        var hidden = _hidden;
        _hidden = null;
        if (hidden != null && !hidden.IsDestroyed)
            hidden.SetLocallyHidden(false);
    }

    private void ShowVisuals()
    {
        if (_figure == null || _figure.Root.IsDestroyed)
        {
            var kit = AvatarArrivalKit.For(World);
            if (kit == null || Slot == null)
                return;
            _figure = new AvatarArrivalFigure(Slot, kit);
            _plate = new AvatarArrivalPlate(Slot, NameplateTheme.For(World));
            _visualsOn = true;
        }
        if (!_visualsOn)
        {
            _figure.Root.ActiveSelf.Value = true;
            if (_plate != null)
                _plate.Root.ActiveSelf.Value = true;
            _visualsOn = true;
            _presence = 0f;
        }
        _bar = _shown;
        _plate?.SetText(_stateText, _phase == Phase.Failed ? string.Empty : _percentText, _detailText, _phase == Phase.Failed);
    }

    private void HideVisuals()
    {
        if (!_visualsOn)
            return;
        _visualsOn = false;
        if (_figure != null && !_figure.Root.IsDestroyed)
            _figure.Root.ActiveSelf.Value = false;
        if (_plate != null && !_plate.Root.IsDestroyed)
            _plate.Root.ActiveSelf.Value = false;
    }

    private void Animate(AvatarEquipManager manager, float delta)
    {
        var user = manager.UserRoot.Target ?? Slot?.ActiveUserRoot;
        if (user == null || user.IsDestroyed || _figure == null || _plate == null)
            return;

        if (_phase == Phase.Revealing)
            _presence = 1f + System.Math.Clamp(_phaseAge / DissolveSeconds, 0f, 1f);
        else
            _presence = MathF.Min(1f, _presence + delta / FadeInSeconds);
        _bar += (_shown - _bar) * (1f - MathF.Exp(-8f * delta));

        _figure.Pose(user, out float floorY, out float topY);
        // Whole centimetres and half percents: a head bobbing a millimetre repaints nothing.
        _figure.Paint(Quantize(_presence, 0.01f), Quantize(floorY, 0.01f), Quantize(topY, 0.01f),
            Quantize(_bar, 0.005f), _phase == Phase.Failed);

        bool own = user.IsLocalUserRoot && !UserInputState.FocusedExternalCameraActive;
        _figure.SetOwnView(own);
        _plate.SetOwnView(own);
        _plate.SetBar(_bar);
        _plate.Place(ReadoutAnchor(manager, user, delta));
    }

    private static float Quantize(float value, float step) => MathF.Round(value / step) * step;

    // Under the person's name plate when it is up, else over their head; never down inside the figure's head.
    private float3 ReadoutAnchor(AvatarEquipManager manager, UserRoot user, float delta)
    {
        float s = System.Math.Clamp(user.GlobalScale, 0.05f, 50f);
        var up = (user.Slot.GlobalRotation * float3.Up).Normalized;
        var head = user.HeadPosition;

        var nameplate = _nameplate;
        if (nameplate == null || nameplate.IsDestroyed)
        {
            _nameplate = null;
            _nameplateSearchIn -= delta;
            if (_nameplateSearchIn <= 0f)
            {
                _nameplateSearchIn = 1f;
                _nameplate = nameplate = manager.Slot?.FindChild(NameplateManager.RootSlotName, recursive: false)
                    ?.GetComponent<NameplateManager>();
            }
        }

        var plate = nameplate?.PlateSlot;
        var at = plate != null && !plate.IsDestroyed && plate.IsActive
            ? plate.GlobalPosition - up * (0.105f * s)
            : head + up * (0.32f * s);
        float above = float3.Dot(at - head, up);
        float least = 0.22f * s;
        if (above < least)
            at += up * (least - above);
        return at;
    }

    // The words on the readout, rebuilt only when something they say has changed.
    private void Readout(bool revealing = false)
    {
        var stage = revealing ? AvatarArrivalCensus.Stage.Ready : _census.State;
        int percent = (int)MathF.Floor(_shown * 100f);
        if (!revealing)
            percent = System.Math.Min(percent, 99);
        long tenths = _census.BytesReceived * 10 / (1024 * 1024);

        if (stage != _textStage)
        {
            _textStage = stage;
            _stateText = stage switch
            {
                AvatarArrivalCensus.Stage.Downloading => "AvatarArrival.Downloading".AsLocale("Downloading").Resolve(),
                AvatarArrivalCensus.Stage.Preparing => "AvatarArrival.Preparing".AsLocale("Preparing").Resolve(),
                AvatarArrivalCensus.Stage.Building => "AvatarArrival.Building".AsLocale("Building").Resolve(),
                AvatarArrivalCensus.Stage.Failed => "AvatarArrival.Failed".AsLocale("Couldn't load").Resolve(),
                _ => "AvatarArrival.Ready".AsLocale("Ready").Resolve(),
            };
            _textParts = -1;
        }
        if (percent != _textPercent)
        {
            _textPercent = percent;
            _percentText = percent + "%";
        }

        bool failed = stage == AvatarArrivalCensus.Stage.Failed;
        if (_census.Parts != _textParts || _census.PartsDone != _textDone || tenths != _textTenths
            || _census.MissingMeshes != _textMissing || _census.BrokenMeshes != _textBroken)
        {
            _textParts = _census.Parts;
            _textDone = _census.PartsDone;
            _textTenths = tenths;
            _textMissing = _census.MissingMeshes;
            _textBroken = _census.BrokenMeshes;
            _detailText = failed ? Reason() : Detail(tenths);
        }

        _plate?.SetText(_stateText, failed ? string.Empty : _percentText, _detailText, failed);
    }

    private string Detail(long tenths)
    {
        string parts = "AvatarArrival.Parts".AsLocale("{0} of {1} parts", _census.PartsDone, _census.Parts).Resolve();
        string text = _avatarName.Length > 0 ? _avatarName + " · " + parts : parts;
        if (tenths > 0)
            text += " · " + "AvatarArrival.Megabytes".AsLocale("{0} MB", (tenths / 10f).ToString("0.0")).Resolve();
        return text;
    }

    private string Reason()
    {
        string why = _census.MissingMeshes > 0
            ? "AvatarArrival.ReasonMissing".AsLocale("{0} of its parts never arrived", _census.MissingMeshes).Resolve()
            : "AvatarArrival.ReasonBroken".AsLocale("{0} of its parts couldn't be read", _census.BrokenMeshes).Resolve();
        return _avatarName.Length > 0 ? _avatarName + " · " + why : why;
    }

    private static string NameOf(Slot avatar)
    {
        string name = (avatar.SlotName.Value ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        // Dots, not an ellipsis glyph: the plate face may not carry one (see NameplateManager).
        return name.Length <= MaxNameChars ? name : name[..(MaxNameChars - 3)] + "...";
    }

    public override void OnDestroy()
    {
        Bind(null);
        Unhide();
        _figure = null;
        _plate = null;
        base.OnDestroy();
    }
}
