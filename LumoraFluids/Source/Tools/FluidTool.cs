// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.UI;
using Lumora.Core.Input;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Fluids;

// Pours, throws and sprays fluid. Pour is a stream off the tip for as long as the trigger is held, Blob grows a lump
// at the tip and throws it on release, Spray is a cone of fine drops. Secondary sucks fluid back up.
//
// Nothing about a pour is ever written to the tool. Whoever holds a tool they do not own may not edit it, and in a
// Social world nobody may edit what they only carry, the host included, so a pour that lived on the tool would pour
// nothing for exactly the people a world hands its tools to. The live pour rides on the holder's own FluidPerson,
// one pair per hand keyed by the tool's RefID, a blob is one entry on their release ring, and suction is a request
// on their request ring. Every machine reads those, checks the world's pour rule against whoever wrote them, and
// runs the stream itself, so the stream is never sent.
//
// In a headset the stream leaves the tip along it and the trigger's pull past 0.3 scales the flow, the brush's
// curve. On a desktop it arcs onto what the pointer is on, up to 3 m off, solved as a throw from the tip, with the
// brush's depth guards so a sweep off a table edge carries on over its plane instead of dropping to the floor.
// The flow goes out on the trigger edge and then at most 15 times a second, only when it moved. -xlinka
[ComponentCategory("Interaction/Tools")]
public sealed class FluidTool : EquippableTool
{
    public const float MinFlow = 0.02f;
    public const float MaxFlow = 2f;
    public const float DefaultFlow = 0.3f;
    // trigger pull below which nothing flows
    public const float StartPull = 0.3f;
    // m, at the user's own size
    public const float SuctionReach = 0.08f;
    // L/s
    public const float SuctionRate = 0.5f;
    // L
    public const float MaxBlob = 0.5f;
    // m, how far off a desktop pour may land
    public const float DesktopReach = 3f;

    private const double PublishSeconds = 1.0 / 15.0;
    private const double SuctionSeconds = 0.25;
    private const float FlowStep = 0.03f;
    private const float AirDepth = 1.2f;
    private const float MinJet = 0.6f;
    private const float MaxJet = 4f;
    private const float Gravity = 9.81f;

    public readonly Sync<FluidPreset> Kind = new(FluidPreset.Slime);
    public readonly Sync<colorHDR> Color = new(new colorHDR(0.45f, 0.85f, 0.35f, 1f));
    public readonly Sync<FluidPourMode> Mode = new(FluidPourMode.Pour);
    // L/s at a full pull
    public readonly Sync<float> Flow = new(DefaultFlow);
    // 0..1, viscosity on a log curve within the kind
    public readonly Sync<float> Thickness = new(0.5f);
    public readonly Sync<float> Stickiness = new(0.5f);
    // mm
    public readonly Sync<float> Nozzle = new(6f);
    public readonly SyncRef<FluidSubstance> Substance = new();
    // null = the world's loose system
    public readonly SyncRef<FluidSystem> Target = new();

    private static readonly colorHDR[] Swatches =
    {
        new(0.55f, 0.75f, 0.95f, 1f),
        new(0.45f, 0.85f, 0.35f, 1f),
        new(0.95f, 0.62f, 0.12f, 1f),
        new(0.95f, 0.26f, 0.30f, 1f),
        new(1.00f, 0.88f, 0.25f, 1f),
        new(0.55f, 0.49f, 0.96f, 1f),
        new(1.00f, 0.45f, 0.78f, 1f),
        new(0.96f, 0.96f, 0.92f, 1f),
        new(0.42f, 0.30f, 0.20f, 1f),
    };

    private static bool _toldViewOnly;

    // The pour or blob in hand, on the holder's machine only.
    private FluidPerson? _person;
    private int _hand;
    private bool _pouring;
    private bool _blobbing;
    private FluidPourMode _pourMode;
    private double _startedAt;
    private int _seed;
    private int _number;
    private float _published;
    private double _lastPublish;
    private float _carried;
    private float _pulled;
    private float _blob;
    private float3 _startPoint;
    private float3 _launch;
    private float3 _aimPoint;

    private bool _onSurface;
    private float _depth;
    private Slot[]? _exclude;

    private float3 _lastTip;
    private float3 _tipVelocity;
    private bool _haveTip;

    private bool _sucking;
    private double _lastSuck;
    private bool _toldNotYours;
    private int _suctionRequests;

    protected override colorHDR VisualTint => Color.Value;

    public override bool UsesLaser => !InHeadset;

    public bool IsPouring => _pouring;

    public bool IsBlobbing => _blobbing;

    public bool IsSucking => _sucking;

    // What the pour in hand, or the last one, carried: the published flow over the time it was out, which is what
    // every other machine runs.
    public float CarriedLitres => _carried;

    // The same pour as the trigger asked for it, before any publishing.
    public float PulledLitres => _pulled;

    public float BlobLitres => _blob;

    public float LiveFlow => _published;

    public int PourNumber => _number;

    public int SuctionRequests => _suctionRequests;

    // Where the stream leaves the tip and how fast, world space, as the last frame solved it.
    public float3 LaunchVelocity => _launch;

    public float3 AimPoint => _aimPoint;

    private static bool InHeadset => Engine.Current?.InputInterface?.IsVRActive == true;

    private float UserScale
    {
        get
        {
            float scale = Slot?.ActiveUserRoot?.GlobalScale ?? 1f;
            return float.IsFinite(scale) && scale > 1e-4f ? scale : 1f;
        }
    }

    public override void OnInit()
    {
        base.OnInit();
        EquipName.Value = "Fluid Tool";
    }

    public static string PresetName(FluidPreset kind) => kind + " Pourer";

    // One tool per kind on the shelf, each in its colour and in the mode that shows the kind off. Any of them pours
    // any kind from its ring; the name is only what it is called on the shelf.
    public void UsePreset(FluidPreset kind)
    {
        Kind.Value = kind;
        EquipName.Value = PresetName(kind);
        Color.Value = KindColour(kind);
        Mode.Value = KindMode(kind);
        Flow.Value = KindFlow(kind);
        Thickness.Value = 0.5f;
        Stickiness.Value = 0.5f;
        if (EnsureSubstance() is { } substance)
            ApplySubstance(substance);
    }

    public static colorHDR KindColour(FluidPreset kind) => kind switch
    {
        FluidPreset.Water => Swatches[0],
        FluidPreset.Oil => new colorHDR(0.85f, 0.70f, 0.25f, 1f),
        FluidPreset.Paint => Swatches[3],
        FluidPreset.Honey => Swatches[2],
        FluidPreset.Slime => Swatches[1],
        FluidPreset.Glue => Swatches[7],
        FluidPreset.Mud => Swatches[8],
        _ => Swatches[5],
    };

    private static FluidPourMode KindMode(FluidPreset kind) => kind switch
    {
        FluidPreset.Paint => FluidPourMode.Spray,
        FluidPreset.Slime or FluidPreset.Mud => FluidPourMode.Blob,
        _ => FluidPourMode.Pour,
    };

    private static float KindFlow(FluidPreset kind) => kind switch
    {
        FluidPreset.Water => 0.3f,
        FluidPreset.Oil => 0.2f,
        FluidPreset.Paint => 0.15f,
        FluidPreset.Honey => 0.1f,
        FluidPreset.Slime => 0.2f,
        FluidPreset.Glue => 0.1f,
        FluidPreset.Mud => 0.25f,
        _ => DefaultFlow,
    };

    public override void OnStart()
    {
        base.OnStart();
        if (MayShape())
            UpdateDrop();
    }

    public override void OnChanges()
    {
        base.OnChanges();
        if (!MayShape())
            return;
        ApplyVisualTint(VisualTint);
        UpdateDrop();
        if (Substance.Target is { IsDestroyed: false } substance && ReferenceEquals(substance.Slot, Slot))
            ApplySubstance(substance);
    }

    public override void OnDequipped()
    {
        base.OnDequipped();
        EndAll();
    }

    public override void OnDestroy()
    {
        EndAll();
        base.OnDestroy();
    }

    private void EndAll()
    {
        if (_pouring)
            EndPour();
        if (_blobbing)
            ThrowBlob(dropped: true);
        _sucking = false;
    }

    // Only the machine that made the tool, or the host while nobody holds it, keeps its look and its substance in
    // step with its settings. Never the holder as such: a holder's write to a tool it does not own is refused.
    private bool MayShape()
        => World != null && Slot != null && (IsMintedHere || (World.IsAuthority && Slot.ActiveUserRoot == null));

    // SUBSTANCE

    private FluidSubstance? EnsureSubstance()
    {
        if (Slot == null)
            return null;
        if (Substance.Target is { IsDestroyed: false } current)
            return current;
        FluidSubstance? made = null;
        Guarded(() =>
        {
            made = Slot.GetComponent<FluidSubstance>() ?? Slot.AttachComponent<FluidSubstance>();
            Substance.Target = made;
        });
        return made;
    }

    // The kind's preset, then thickness and stickiness as two decades either side of it, and the tool's colour.
    // Written only when something moved, so a change elsewhere on the tool costs the substance nothing.
    private void ApplySubstance(FluidSubstance substance)
    {
        var kind = Kind.Value;
        Guarded(() =>
        {
            if (substance.Kind.Value != kind)
                substance.UsePreset(kind);
            float viscosity = BaseViscosity(kind) * Decades(Thickness.Value);
            if (MathF.Abs(substance.Viscosity.Value - viscosity) > viscosity * 1e-4f)
                substance.Viscosity.Value = viscosity;
            float sticky = BaseStickiness(kind) * Decades(Stickiness.Value);
            if (MathF.Abs(substance.Stickiness.Value - sticky) > MathF.Max(sticky, 1f) * 1e-4f)
                substance.Stickiness.Value = sticky;
            if (substance.Tint.Value != Color.Value)
                substance.Tint.Value = Color.Value;
        });
    }

    private static float Decades(float dial) => MathF.Pow(10f, (System.Math.Clamp(dial, 0f, 1f) - 0.5f) * 2f);

    // Pa s and Pa, the middle of each kind's dial.
    private static float BaseViscosity(FluidPreset kind) => kind switch
    {
        FluidPreset.Water => 0.001f,
        FluidPreset.Oil => 0.08f,
        FluidPreset.Paint => 0.5f,
        FluidPreset.Honey => 10f,
        FluidPreset.Slime => 30f,
        FluidPreset.Glue => 50f,
        FluidPreset.Mud => 20f,
        _ => 1f,
    };

    private static float BaseStickiness(FluidPreset kind) => kind switch
    {
        FluidPreset.Water => 0f,
        FluidPreset.Oil => 5f,
        FluidPreset.Paint => 50f,
        FluidPreset.Honey => 150f,
        FluidPreset.Slime => 400f,
        FluidPreset.Glue => 1500f,
        FluidPreset.Mud => 80f,
        _ => 100f,
    };

    // VISUAL

    // A drop on the tip in the tool's colour, as wide as the nozzle.
    protected override void BuildVisualExtras(Slot visual)
    {
        EnsureBead(visual, "Drop", float3.Up * 0.0255f, 1f, VisualTint);
    }

    private void UpdateDrop()
    {
        var drop = VisualSlot?.FindChild("Drop", recursive: false);
        if (drop == null || drop.IsDestroyed)
            return;
        float radius = System.Math.Clamp(Nozzle.Value * 0.0005f + 0.002f, 0.003f, 0.012f);
        var scale = float3.One * radius;
        if (drop.LocalScale.Value != scale)
            drop.LocalScale.Value = scale;
        SetBeadTint(drop, VisualTint);
    }

    // PRESSES

    public override bool OnPrimaryPress()
    {
        if (_pouring || _blobbing)
            return true;
        if (!InHeadset && PointerOnPanel())
            return false;
        var world = World;
        if (world == null)
            return false;
        if (!MayPour(world, world.LocalUser, Target.Target))
        {
            Notice(FluidLocale.NotAllowed);
            return true;
        }
        TellViewOnlyOnce();
        _haveTip = false;
        SampleTip(0f);
        return Mode.Value == FluidPourMode.Blob ? BeginBlob() : BeginPour(Mode.Value);
    }

    public override bool OnPrimaryHold()
    {
        float delta = World?.Time.Delta ?? 0f;
        SampleTip(delta);
        if (_pouring)
        {
            UpdatePour(delta);
            return true;
        }
        if (_blobbing)
        {
            _blob = MathF.Min(MaxBlob, _blob + FlowNow() * delta);
            _pulled = _blob;
            Aim(starting: false);
            return true;
        }
        return false;
    }

    public override bool OnPrimaryRelease()
    {
        if (_pouring)
        {
            EndPour();
            return true;
        }
        if (_blobbing)
        {
            ThrowBlob(dropped: false);
            return true;
        }
        return false;
    }

    public override bool OnSecondaryPress()
    {
        if (_pouring || _blobbing)
            return false;
        _sucking = true;
        _toldNotYours = false;
        _lastSuck = double.NegativeInfinity;
        Suck();
        return true;
    }

    public override bool OnSecondaryHold()
    {
        if (!_sucking)
            return false;
        Suck();
        return true;
    }

    public override bool OnSecondaryRelease()
    {
        if (!_sucking)
            return false;
        _sucking = false;
        return true;
    }

    // A click on a panel or a touch target is that thing's click, not a pour onto it.
    private bool PointerOnPanel()
    {
        var laser = ActiveTool?.Laser;
        return laser != null && (laser.CurrentPointerTarget != null || laser.CurrentRayTarget != null);
    }

    // POURING

    private bool BeginPour(FluidPourMode mode)
    {
        var person = OwnPerson(World, attach: true);
        if (person == null)
            return false;
        _person = person;
        _hand = HandIndex();
        _pourMode = mode;
        _startedAt = World!.SessionSeconds;
        _number = NextPourNumber(person);
        _seed = HashCode.Combine(ReferenceID.RawValue, _number, Environment.TickCount);
        _carried = 0f;
        _pulled = 0f;
        Aim(starting: true);
        _startPoint = Tip;
        _pouring = true;
        AskForLooseSystem(person);
        Publish(FlowNow());
        return true;
    }

    private void UpdatePour(float delta)
    {
        // What was out there since the last frame, before this frame's change of mind.
        _carried += _published * delta;
        float flow = FlowNow();
        _pulled += flow * delta;
        Aim(starting: false);
        double now = World?.Time.TotalTime ?? 0;
        bool moved = MathF.Abs(flow - _published) > MathF.Max(_published, MinFlow) * FlowStep || (flow <= 0f) != (_published <= 0f);
        if (moved && now - _lastPublish >= PublishSeconds)
            Publish(flow);
    }

    private void EndPour()
    {
        // The flow stayed out until this frame's zero lands, everywhere.
        _carried += _published * (World?.Time.Delta ?? 0f);
        Publish(0f);
        _pouring = false;
        if (_carried > 1e-6f)
            RecordUndo(new FluidPourUndo(World!, TargetId(), _startPoint, _carried, _number));
    }

    private void Publish(float flow)
    {
        _published = flow;
        _lastPublish = World?.Time.TotalTime ?? 0;
        var person = _person;
        if (person == null || person.IsDestroyed || person.Pour.Count < FluidPerson.PourCount)
            return;
        new FluidPour(ReferenceID.RawValue, flow, _pourMode, _startedAt, _seed, _number).Write(out var a, out var b);
        int at = _hand * 2;
        Guarded(() =>
        {
            person.Pour[at] = a;
            person.Pour[at + 1] = b;
        });
    }

    private bool BeginBlob()
    {
        var person = OwnPerson(World, attach: true);
        if (person == null)
            return false;
        _person = person;
        _hand = HandIndex();
        _blob = 0f;
        _pulled = 0f;
        _blobbing = true;
        Aim(starting: true);
        AskForLooseSystem(person);
        return true;
    }

    // One release entry carries the whole blob; every machine spawns it at the tip and throws it along. On a desktop
    // it is thrown at what the pointer is on, in a headset with the hand's own swing.
    private void ThrowBlob(bool dropped)
    {
        _blobbing = false;
        var person = _person;
        if (person == null || person.IsDestroyed || _blob <= 1e-6f)
            return;
        var velocity = dropped ? float3.Zero : InHeadset ? _tipVelocity : _launch;
        _number = NextPourNumber(person);
        var part = _hand == 0 ? BodyNode.LeftHand : BodyNode.RightHand;
        var start = Tip;
        new FluidRelease(FluidReleaseKind.Blob, start, velocity, _blob, part, 0, HashCode.Combine(ReferenceID.RawValue, _number))
            .Write(out var a, out var b);
        bool sent = false;
        Guarded(() => sent = FluidRings.Write(person.Releases, person.ReleaseSequence, a, b));
        if (sent)
            RecordUndo(new FluidPourUndo(World!, TargetId(), start, _blob, _number));
    }

    // The trigger's pull past where a pour starts, times the dial, held under the world's own pour rate.
    private float FlowNow()
    {
        float pull = 1f;
        if (InHeadset)
        {
            var input = Engine.Current?.InputInterface;
            var controller = HandIndex() == 0 ? input?.LeftController : input?.RightController;
            float trigger = controller?.TriggerValue ?? 1f;
            pull = System.Math.Clamp((trigger - StartPull) / (1f - StartPull), 0f, 1f);
        }
        float flow = pull * System.Math.Clamp(Flow.Value, MinFlow, MaxFlow);
        var rules = Rules(World);
        if (rules != null && rules.MaxPourRate.Value > 0f)
            flow = MathF.Min(flow, rules.MaxPourRate.Value);
        return flow;
    }

    private int HandIndex() => ActiveTool is { } tool && tool.Side.Value == Chirality.Left ? 0 : 1;

    private static int NextPourNumber(FluidPerson person)
    {
        int number = person.PourNumber.Value + 1;
        try
        {
            person.PourNumber.Value = number;
        }
        catch (UnauthorizedAccessException ex)
        {
            LumoraLogger.Warn($"FluidTool: refused: {ex.Message}");
        }
        return number;
    }

    // The tip's own speed, smoothed over a few frames, for the blob a headset throws.
    private void SampleTip(float delta)
    {
        var tip = Tip;
        if (_haveTip && delta > 1e-5f)
        {
            var raw = (tip - _lastTip) / delta;
            float k = 1f - MathF.Exp(-delta / 0.04f);
            _tipVelocity = float3.Lerp(_tipVelocity, raw, k);
        }
        else if (!_haveTip)
        {
            _tipVelocity = float3.Zero;
        }
        _lastTip = tip;
        _haveTip = true;
    }

    // AIM

    // Where the stream lands and how it leaves the tip. In a headset it leaves along the tip at a speed set by flow
    // and nozzle; on a desktop it is solved as a throw onto what the pointer is on.
    private void Aim(bool starting)
    {
        var tip = Tip;
        if (InHeadset || !TryPointerRay(out var origin, out var direction) || direction.LengthSquared <= 1e-8f)
        {
            _launch = TipForward * JetSpeed();
            _aimPoint = tip + TipForward * 0.5f;
            return;
        }
        direction = direction.Normalized;
        float scale = UserScale;
        float reach = DesktopReach * scale;
        if (starting)
        {
            if (TryGetAim(out var aim) && aim.HitSlot != null && float3.Distance(origin, aim.Point) <= reach)
            {
                _onSurface = true;
                _depth = float3.Distance(origin, aim.Point);
                _aimPoint = aim.Point;
            }
            else
            {
                _onSurface = false;
                _depth = AirDepth * scale;
                _aimPoint = origin + direction * _depth;
            }
        }
        else
        {
            // Following a surface looks only a little past where the pour already lands.
            float follow = MathF.Min(reach, _depth * 1.5f + 0.25f * scale);
            if (_onSurface && World?.Physics.Raycast(origin, direction, Exclude(), follow) is { } hit)
            {
                _aimPoint = hit.Point;
                _depth = float3.Distance(origin, hit.Point);
            }
            else
            {
                _aimPoint = origin + direction * _depth;
            }
        }
        _launch = Ballistic(tip, _aimPoint);
    }

    // A throw from the tip that lands on the point: a flight time that grows with distance, and the speed that
    // makes the drop land there in that time.
    public static float3 Ballistic(float3 from, float3 to)
    {
        var offset = to - from;
        float time = System.Math.Clamp(offset.Length / 2.5f, 0.2f, 0.8f);
        return offset / time + new float3(0f, 0.5f * Gravity * time, 0f);
    }

    private float JetSpeed()
    {
        float radius = System.Math.Clamp(Nozzle.Value, 1f, 30f) * 0.0005f;
        float area = MathF.PI * radius * radius;
        float speed = System.Math.Clamp(Flow.Value, MinFlow, MaxFlow) * 0.001f / area;
        return System.Math.Clamp(speed, MinJet, MaxJet) * UserScale;
    }

    private bool TryPointerRay(out float3 origin, out float3 direction)
    {
        if (AimOverride is { } aim)
        {
            origin = aim.Origin;
            direction = aim.Direction;
            return true;
        }
        var laser = ActiveTool?.Laser;
        origin = laser?.RayOrigin ?? float3.Zero;
        direction = laser?.RayDirection ?? float3.Zero;
        return laser != null;
    }

    private Slot[]? Exclude()
    {
        var own = Slot?.ActiveUserRoot?.Slot;
        if (own == null)
            return null;
        if (_exclude == null || !ReferenceEquals(_exclude[0], own))
            _exclude = new[] { own };
        return _exclude;
    }

    // SUCTION

    // A Remove request every quarter second while held, at the system it is pointed at or the one with the most fluid
    // in reach. There is no answer coming back, so this machine asks the host's own question first and says so when
    // the answer is no.
    private void Suck()
    {
        var world = World;
        var user = world?.LocalUser;
        if (world == null || user == null)
            return;
        double now = world.Time.TotalTime;
        if (now - _lastSuck < SuctionSeconds)
            return;
        _lastSuck = now;
        float3 point = SuctionPoint();
        float radius = SuctionReach * UserScale;
        var system = Target.Target is { IsDestroyed: false } target ? target : MostIn(world, point, radius);
        if (system == null)
            return;
        if (!MaySuck(world, user, system, point, radius))
        {
            if (!_toldNotYours)
            {
                _toldNotYours = true;
                Notice(FluidLocale.OnlyYours);
            }
            return;
        }
        var person = OwnPerson(world, attach: true);
        if (person == null)
            return;
        ulong id = system.ReferenceID.RawValue;
        if (Ask(person, new FluidRequest(FluidRequestKind.Remove, id, InSystem(world, id, point), radius, (float)(SuctionRate * SuctionSeconds), 0)))
            _suctionRequests++;
    }

    private float3 SuctionPoint()
    {
        if (InHeadset || !TryGetAim(out var aim) || aim.HitSlot == null)
            return Tip;
        return float3.Distance(aim.Origin, aim.Point) <= DesktopReach * UserScale ? aim.Point : Tip;
    }

    private static FluidSystem? MostIn(World world, float3 point, float radius)
    {
        var space = FluidSpace.Find(world);
        if (space == null)
            return null;
        FluidSystem? best = null;
        float most = 0f;
        foreach (var system in space.Systems)
        {
            float litres = system.IsDestroyed ? 0f : system.LitresIn(point, radius);
            if (litres > most)
            {
                most = litres;
                best = system;
            }
        }
        return best;
    }

    // The host's own rule for a Remove: your own fluid always goes, anybody else's only for someone who may edit the
    // system, or a moderator.
    public static bool MaySuck(World world, User user, FluidSystem system, float3 point, float radius)
        => MayEdit(world, user, system) || FluidRequestJudge.IsModerator(world, user) || OwnLitresIn(system, point, radius, user) > 0f;

    // On the host the gate itself answers. A guest's own gate lets its own writes through and leaves the judging to
    // the host, so there it says yes to everything; the guest's role, which the host keeps in step, says what the host
    // will say about an ordinary system.
    public static bool MayEdit(World world, User user, FluidSystem system)
    {
        if (world.IsAuthority)
            return FluidRequestJudge.MayEditSystem(world, user, system);
        var role = world.DataModelPermissions?.GetRole(user);
        if (role == null)
            return true;
        if (!world.AllowsWorldEditing)
            return false;
        bool owns = system.ReferenceID.GetUserByte() == user.AllocationID.Value;
        return role.Allows(Lumora.Warden.PermissionAction.Write, owns);
    }

    // How much of the settled fluid near point this user poured, by each cell's origin, from the ledger every
    // machine holds.
    public static float OwnLitresIn(FluidSystem system, float3 point, float radius, User user)
    {
        var slot = system.Slot;
        if (slot == null || !(radius > 0f))
            return 0f;
        float size = system.EffectiveCellSize;
        var local = slot.GlobalPointToLocal(point);
        float scale = slot.GlobalScale.x;
        float r = scale > 1e-6f ? radius / scale : radius;
        ulong me = user.ReferenceID.RawValue;
        var origins = system.Origins;
        double total = 0;
        foreach (var cell in system.Cells)
        {
            if (FluidCell.Fill(cell) == 0)
                continue;
            int origin = FluidCell.Origin(cell);
            if (origin <= 0 || origin >= origins.Count || FluidLedger.OriginUser(origins[origin]) != me)
                continue;
            if (float3.DistanceSquared(FluidCell.Centre(cell, size), local) <= r * r)
                total += FluidCell.Litres(cell, size);
        }
        return (float)total;
    }

    // POLICY

    // The world's pour rule, the same question the host asks of a pour request, so a refused pourer is told here and
    // pours nothing anywhere.
    public static bool MayPour(World world, User? user, FluidSystem? system = null)
        => user != null && FluidRequestJudge.MayPour(world, user, Rules(world), system);

    public static FluidWorldRules? Rules(World? world)
        => world?.RootSlot?.GetComponent<FluidWorldRules>() is { IsDestroyed: false } rules ? rules : null;

    private ulong TargetId() => Target.Target is { IsDestroyed: false } target ? target.ReferenceID.RawValue : 0UL;

    // With no target the pour goes to the world's loose system for its kind, which the host makes on demand and
    // fills with this tool's substance.
    private void AskForLooseSystem(FluidPerson person)
    {
        if (Target.Target != null)
            return;
        Ask(person, new FluidRequest(FluidRequestKind.EnsureLoose, 0UL, Tip, 0f, 0f, (int)Kind.Value));
        if (Substance.Target is { IsDestroyed: false } substance)
            Ask(person, new FluidRequest(FluidRequestKind.AdoptSubstance, substance.ReferenceID.RawValue, Tip, 0f, 0f, (int)Kind.Value));
    }

    private void TellViewOnlyOnce()
    {
        if (_toldViewOnly || FluidGpu.Path != FluidGpuPath.ViewOnly)
            return;
        _toldViewOnly = true;
        Notice(FluidLocale.ViewOnlyNotice);
    }

    private void Notice(LocaleText text)
    {
        var title = string.IsNullOrWhiteSpace(EquipName.Value) ? FluidLocale.ToolName.Resolve() : EquipName.Value!;
        ToolNotice.Say(World, title, text.Resolve());
    }

    // THE HOLDER'S RINGS

    // The user here's own trusted FluidPerson. Asked to, the wearer's side makes and sizes it, which only ever
    // happens on the wearer's own machine.
    public static FluidPerson? OwnPerson(World? world, bool attach)
    {
        if (world == null || world.IsDestroyed)
            return null;
        try
        {
            return attach ? FluidPerson.Ensure(world) : FluidPerson.FindFor(world.LocalUser);
        }
        catch (UnauthorizedAccessException ex)
        {
            LumoraLogger.Warn($"FluidTool: refused: {ex.Message}");
            return null;
        }
    }

    // One request on the person's own ring. Points travel in the system's own space when there is a system.
    public static bool Ask(FluidPerson person, in FluidRequest request)
    {
        request.Write(out var a, out var b);
        try
        {
            return FluidRings.Write(person.Requests, person.RequestSequence, a, b);
        }
        catch (UnauthorizedAccessException ex)
        {
            LumoraLogger.Warn($"FluidTool: refused: {ex.Message}");
            return false;
        }
    }

    // A world point in the space of the system with this RefID; the world's own space when there is none.
    public static float3 InSystem(World? world, ulong system, float3 point)
    {
        if (world == null || system == 0)
            return point;
        return world.ReferenceController.GetObjectOrNull(new RefID(system)) is FluidSystem { IsDestroyed: false, Slot: { } slot }
            ? slot.GlobalPointToLocal(point)
            : point;
    }

    // MENU

    public override void PopulateToolActions(ContextMenuPage page, ContextMenuContext context)
    {
        var kind = Kind.Value;
        var kinds = new ContextMenuPage(FluidLocale.Kind.Resolve());
        foreach (var each in Enum.GetValues<FluidPreset>())
        {
            if (each == FluidPreset.Custom)
                continue;
            var pick = each;
            kinds.AddItem(new ContextMenuItem
            {
                Label = FluidLocale.KindName(each).Resolve(),
                Detail = FluidLocale.KindDetail(each).Resolve(),
                FillColor = each == kind ? ActiveFill : ItemFill,
                OnPressed = _ => { if (!IsDestroyed) Guarded(() => Kind.Value = pick); },
            });
        }
        page.AddItem(new ContextMenuItem
        {
            Label = FluidLocale.KindName(kind).Resolve(),
            Detail = FluidLocale.Kind.Resolve(),
            FillColor = ActiveFill,
            SubPage = kinds,
        });

        var colours = new ContextMenuPage(FluidLocale.Colour.Resolve());
        foreach (var swatch in Swatches)
        {
            var pick = swatch;
            colours.AddItem(new ContextMenuItem
            {
                Label = "",
                Detail = Hex(swatch),
                FillColor = Rgba(swatch),
                OnPressed = _ => { if (!IsDestroyed) Guarded(() => Color.Value = pick); },
            });
        }
        colours.AddItem(new ContextMenuItem
        {
            Label = FluidLocale.Picker.Resolve(),
            FillColor = ItemFill,
            OnPressed = _ => OpenPicker(),
        });
        page.AddItem(new ContextMenuItem
        {
            Label = FluidLocale.Colour.Resolve(),
            Detail = Hex(Color.Value),
            FillColor = Rgba(Color.Value),
            SubPage = colours,
        });

        var mode = Mode.Value;
        var modes = new ContextMenuPage(FluidLocale.Mode.Resolve());
        foreach (var each in Enum.GetValues<FluidPourMode>())
        {
            var pick = each;
            modes.AddItem(new ContextMenuItem
            {
                Label = FluidLocale.ModeName(each).Resolve(),
                Detail = FluidLocale.ModeDetail(each).Resolve(),
                FillColor = each == mode ? ActiveFill : ItemFill,
                OnPressed = _ => { if (!IsDestroyed) Guarded(() => Mode.Value = pick); },
            });
        }
        page.AddItem(new ContextMenuItem
        {
            Label = FluidLocale.ModeName(mode).Resolve(),
            Detail = FluidLocale.Mode.Resolve(),
            FillColor = ItemFill,
            SubPage = modes,
        });

        page.AddItem(DialItem(() => FluidLocale.Flow.WithArgs(Litres(Flow.Value)).Resolve(),
            () => ToDial(Flow.Value, MinFlow, MaxFlow), v => Flow.Value = FromDial(v, MinFlow, MaxFlow), ToDial(DefaultFlow, MinFlow, MaxFlow)));
        page.AddItem(DialItem(() => FluidLocale.Thick.WithArgs(Percent(Thickness.Value)).Resolve(),
            () => Thickness.Value, v => Thickness.Value = System.Math.Clamp(v, 0f, 1f), 0.5f));
        page.AddItem(DialItem(() => FluidLocale.Sticky.WithArgs(Percent(Stickiness.Value)).Resolve(),
            () => Stickiness.Value, v => Stickiness.Value = System.Math.Clamp(v, 0f, 1f), 0.5f));

        page.AddItem(new ContextMenuItem
        {
            Label = FluidLocale.CleanMine.Resolve(),
            FillColor = ClearFill,
            OnPressed = _ => CleanMine(),
        });
    }

    // Takes back everything you poured into this tool's system, or every system when it has none. Nothing to undo:
    // what is taken away does not come back.
    public bool CleanMine()
    {
        var world = World;
        var person = OwnPerson(world, attach: true);
        if (world == null || person == null)
            return false;
        if (Target.Target is { IsDestroyed: false } target)
            return Ask(person, new FluidRequest(FluidRequestKind.ClearMine, target.ReferenceID.RawValue, float3.Zero, 0f, 0f, 0));
        bool any = false;
        if (FluidSpace.Find(world) is { } space)
        {
            foreach (var system in space.Systems)
            {
                if (!system.IsDestroyed)
                    any |= Ask(person, new FluidRequest(FluidRequestKind.ClearMine, system.ReferenceID.RawValue, float3.Zero, 0f, 0f, 0));
            }
        }
        return any;
    }

    private ContextMenuItem DialItem(Func<string> label, Func<float> read, Action<float> write, float initial)
    {
        return new ContextMenuItem
        {
            Label = label(),
            FillColor = ItemFill,
            ReadValue = () => IsDestroyed ? 0f : read(),
            WriteValue = v => { if (!IsDestroyed) Guarded(() => write(v)); },
            DefaultValue = initial,
            Refresh = item =>
            {
                var now = label();
                if (item.Label == now)
                    return false;
                item.Label = now;
                return true;
            },
        };
    }

    // Flow turned on a dial runs on a log scale: a trickle matters at the thin end and not at the fire hose end.
    public static float ToDial(float value, float min, float max)
        => System.Math.Clamp(MathF.Log(System.Math.Clamp(value, min, max) / min) / MathF.Log(max / min), 0f, 1f);

    public static float FromDial(float dial, float min, float max)
        => min * MathF.Pow(max / min, System.Math.Clamp(dial, 0f, 1f));

    private static string Litres(float perSecond) => perSecond < 0.1f ? $"{perSecond * 1000f:0} mL/s" : $"{perSecond:0.##} L/s";

    private static string Percent(float value) => $"{value * 100f:0}%";

    private static float[] Rgba(colorHDR c)
        => new[] { System.Math.Clamp(c.r, 0f, 1f), System.Math.Clamp(c.g, 0f, 1f), System.Math.Clamp(c.b, 0f, 1f), 0.95f };

    private static string Hex(colorHDR c)
    {
        int r = (int)MathF.Round(System.Math.Clamp(c.r, 0f, 1f) * 255f);
        int g = (int)MathF.Round(System.Math.Clamp(c.g, 0f, 1f) * 255f);
        int b = (int)MathF.Round(System.Math.Clamp(c.b, 0f, 1f) * 255f);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private void OpenPicker()
    {
        var world = World;
        if (IsDestroyed || world == null)
            return;
        var head = world.LocalUser?.Root?.HeadSlot;
        var position = head != null
            ? head.GlobalPosition + head.GlobalRotation * float3.Backward * (0.6f * UserScale) - float3.Up * (0.12f * UserScale)
            : Slot.GlobalPosition;
        ColorPickerPanel.Spawn(world, Color, "", position);
    }
}
