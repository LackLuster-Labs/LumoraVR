// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Photos;

namespace Lumora.Core.Components;

// A camera to film and take photos with, the one a stream is shot through.
//
// Everyone sees the camera; its operator's machine moves it, by the mode: riding the subject's eyes, keeping
// a distance from their face, pulling back to fit everyone standing with them, standing still and turning to
// keep them framed, or carried by hand. Every mode but the last places the subject at the framing point on
// the picture, eases in at the set speeds, and can step in front of walls rather than film through them. An
// anchor takes the camera over and glides it to a fixed spot; a route glides it through anchors in turn.
//
// What it films is drawn on the operator's machine only, unless it is set to draw for everyone (then every
// machine films it for its own screen). Name plates and lasers can be left out of the view by band, objects
// by list. The body hides itself where it would be in the way: always while riding the eyes, and close to a
// viewer's own head when it follows. Held, the holding hand's trigger takes a photo and the other button
// starts the timer; the timer counts down on the camera where everyone can see it. -xlinka
[ComponentCategory("Interaction/Tools")]
public class PhotoCamera : Component
{
    public readonly SyncRef<User> Operator = new();
    public readonly SyncRefList<User> Operators = new();
    public readonly Sync<bool> AnyoneCanOperate = new();

    public readonly Sync<PhotoCameraMode> Mode = new(PhotoCameraMode.ThirdPerson);
    // Who is framed; nobody means the operator.
    public readonly SyncRef<User> Subject = new();
    public readonly Sync<float> FieldOfView = new(60f);
    // Degrees round the subject from in front of their face, how far from it and how much higher.
    public readonly Sync<float> Orbit = new();
    public readonly Sync<float> Distance = new(1.5f);
    public readonly Sync<float> Height = new();
    // Riding the eyes: degrees down and of roll, and how far ahead of the eyes, metres.
    public readonly Sync<float> Pitch = new(5f);
    public readonly Sync<float> Roll = new();
    public readonly Sync<float> EyeOffset = new(0.02f);
    // Where on the picture the subject sits, from the left and from the top.
    public readonly Sync<float2> Framing = new(new float2(0.5f, 0.33f));
    // Frames a point half a metre in front of the face instead of the head itself.
    public readonly Sync<bool> AimAhead = new();
    public readonly Sync<float> PositionSpeed = new(2f);
    public readonly Sync<float> AngleSpeed = new(0.25f);
    public readonly Sync<float> FramingSpeed = new(5f);
    public readonly Sync<bool> AvoidOcclusion = new(true);
    // Group and stationary shots stay in the world rather than riding whatever the subject stands on.
    public readonly Sync<bool> KeepInWorld = new();
    public readonly Sync<bool> Wobble = new();
    public readonly Sync<float> GroupRadius = new(2.5f);
    public readonly Sync<float> GroupMargin = new(0.5f);
    public readonly SyncRefList<User> GroupInclude = new();
    public readonly SyncRefList<User> GroupExclude = new();

    public readonly Sync<bool> HideBody = new();
    public readonly Sync<bool> ShowFrustum = new();
    // The operator's avatar looks at the camera whenever it is in front of them.
    public readonly Sync<bool> EyesOnCamera = new();
    public readonly Sync<bool> HideNameplates = new();
    public readonly Sync<bool> HideLasers = new();
    public readonly Sync<bool> RenderForEveryone = new();
    public readonly SyncRefList<Slot> LeftOut = new();

    public readonly SyncRef<CameraAnchor> Anchor = new();
    public readonly Sync<bool> AnchorEase = new(true);
    public readonly Sync<float> AnchorSpeed = new(2.5f);
    public readonly Sync<bool> AnchorLinear = new();
    public readonly SyncRef<CameraRoute> Route = new();

    // A countdown started by the operator, for everyone to see: its length, and a counter that moves on each
    // start or stop.
    public readonly Sync<float> CountdownSeconds = new();
    public readonly Sync<int> CountdownStamp = new();

    public const float BodyWidth = 0.13f;
    public const float BodyHeight = 0.085f;
    public const float BodyDepth = 0.05f;
    public const int FeedWidth = 1920;
    public const int FeedHeight = 1080;
    public const float Aspect = 16f / 9f;
    // Just past the glass: the feed and every photo film from here.
    public static readonly float3 LensOffset = new(0f, 0f, -(BodyDepth * 0.5f + 0.03f));
    private const float HideNearHead = 0.6f;
    private const float Deg = MathF.PI / 180f;
    private static readonly float3 Ahead = new(0f, 0f, -1f);

    private static readonly ConditionalWeakTable<World, List<PhotoCamera>> Registry = new();

    // Set by this machine's director for its own camera: films this machine's private interface too.
    public bool PrivateUIHere { get; set; }
    public bool Busy => _busy;
    public RenderTexture? Feed => _feedTexture?.Asset as RenderTexture;
    public float LiveFieldOfView => Route.Target != null && _routeAt != null ? _routeFov
        : Anchor.Target != null && _anchorAt != null ? _anchorFov : FieldOfView.Value;
    // Put somewhere by an anchor or a route rather than by its mode.
    public bool Placed => Anchor.Target != null || Route.Target != null;
    // Seconds along the route it is gliding, and whether a route that does not loop has reached its end.
    public double RouteTime => _routeAt != null ? System.Math.Max(0, _routeClock - _leadSeconds) : 0;
    public bool RouteFinished => _routeAt != null && _routeDone;
    public double CountdownLeft => _countdownEnds >= 0 && World != null ? System.Math.Max(0, _countdownEnds - World.Time.TotalTime) : -1;

    private Slot? _visuals;
    private Slot? _bodyParts;
    private Slot? _frustum;
    private UnlitMaterial? _screenMaterial;
    private UnlitMaterial? _lightMaterial;
    private TextRenderer? _countFront;
    private TextRenderer? _countBack;
    private float _frustumFov = -1f;

    private Slot? _feedSlot;
    private Camera? _feedCamera;
    private RenderTextureProvider? _feedTexture;
    private int _leftOutSignature = -1;

    private Chirality _claimed = Chirality.None;
    private bool _busy;
    private double _countdownEnds = -1;
    private PhotoShape _countdownShape;
    private bool _countdownMine;
    private int _seenStamp;

    private PhotoCameraMode _lastMode = (PhotoCameraMode)(-1);
    private Slot? _space;
    private bool _smoothed;
    private bool _turned;
    private float3 _origin, _originMid, _facing, _facingMid;
    private floatQ _turn, _turnMid;
    private float _extra, _extraMid;
    private float _occlusion = float.PositiveInfinity;
    private float _wobbleWeight;
    private float3 _wobbleSeed;
    private readonly HashSet<User> _members = new();
    private readonly List<float3> _heads = new();
    private readonly List<Slot> _skip = new();
    private readonly List<User> _users = new();
    private CameraAnchor? _anchorAt;
    private float _anchorFov;
    private float3 _anchorPosMid;
    private floatQ _anchorRotMid;
    private float _anchorFovMid;
    private CameraRoute? _routeAt;
    private double _routeClock;
    private bool _routeDone;
    private float _routeFov;
    private float _leadSeconds;
    private float3 _leadPosition;
    private floatQ _leadRotation;
    private float _leadFov;

    public static IReadOnlyList<PhotoCamera> InWorld(World? world)
        => world != null && Registry.TryGetValue(world, out var list) ? list : Array.Empty<PhotoCamera>();

    // The camera the local user runs in this world.
    public static PhotoCamera? MineIn(World? world)
    {
        var me = world?.LocalUser;
        if (me == null)
            return null;
        foreach (var camera in InWorld(world))
        {
            if (!camera.IsDestroyed && ReferenceEquals(camera.Operator.Target, me))
                return camera;
        }
        return null;
    }

    // In front of the local user, looking where they look, its screen to them.
    public static PhotoCamera? Spawn(World world, bool keep = true)
    {
        var root = world.LocalUser?.Root;
        if (world.RootSlot == null || root == null)
            return null;
        using var own = world.EnterSpawnAllocation();
        var slot = world.RootSlot.AddSlot("Camera");
        if (!keep)
            slot.Persistent.Value = false;
        slot.GlobalPosition = root.HeadPosition + root.HeadRotation * (Ahead * 0.45f) + float3.Down * 0.08f;
        slot.GlobalRotation = root.HeadRotation;
        return slot.AttachComponent<PhotoCamera>();
    }

    public override void OnAttach()
    {
        base.OnAttach();
        Operator.Target = World?.LocalUser!;
        var collider = Slot.AttachComponent<BoxCollider>();
        collider.Size.Value = new float3(BodyWidth, BodyHeight, BodyDepth + 0.03f);
        collider.CharacterCollider.Value = false;
        Slot.AttachComponent<Grabbable>();
    }

    public override void OnStart()
    {
        base.OnStart();
        if (World != null)
        {
            var list = Registry.GetOrCreateValue(World);
            if (!list.Contains(this))
                list.Add(this);
        }
        var random = new Random();
        _wobbleSeed = new float3(random.NextSingle() * 2000f - 1000f, random.NextSingle() * 2000f - 1000f, random.NextSingle() * 2000f - 1000f);
        _seenStamp = CountdownStamp.Value;
    }

    public override void OnDestroy()
    {
        if (World != null && Registry.TryGetValue(World, out var list))
            list.Remove(this);
        if (_claimed != Chirality.None)
            Engine.Current?.InputInterface?.ReleaseHand(_claimed, this);
        _claimed = Chirality.None;
        DesktopFeed.Hide(this);
        DestroyFeed();
        if (_visuals is { IsDestroyed: false })
            _visuals.Destroy();
        base.OnDestroy();
    }

    public bool IsMine => World is { } world && Operator.Target is { } user && ReferenceEquals(user, world.LocalUser);

    public bool MayOperate(User? user)
    {
        if (user == null)
            return false;
        return ReferenceEquals(user, Operator.Target) || AnyoneCanOperate.Value || Operators.Contains(user);
    }

    public bool RendersHere => IsMine || RenderForEveryone.Value || (World?.LocalUser is { } me && Operators.Contains(me));

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        var world = World;
        if (world == null)
            return;
        double now = world.Time.TotalTime;
        if (IsMine)
            Simulate(delta);
        UpdateFeed();
        UpdateVisuals(now);
        HoldControls();
        UpdateCountdown(now);
    }

    // MOVEMENT

    private void Simulate(float delta)
    {
        var grab = Slot.GetComponent<Grabbable>();
        if (grab != null)
        {
            bool free = !Placed;
            if (grab.AllowGrab.Value != free)
                grab.AllowGrab.Value = free;
        }
        // Only a camera you can pick up stops a laser: one riding your eyes or flying round your face would
        // otherwise sit in front of everything you point at.
        var collider = Slot.GetComponent<BoxCollider>();
        bool solid = !Placed && !HideBody.Value && Mode.Value is PhotoCameraMode.Manual or PhotoCameraMode.Stationary;
        if (collider != null && collider.Enabled.Value != solid)
            collider.Enabled.Value = solid;
        if (Route.Target is { IsDestroyed: false } route && route.HasPath)
        {
            _anchorAt = null;
            FollowRoute(route, delta);
            return;
        }
        _routeAt = null;
        if (Anchor.Target is { IsDestroyed: false } anchor)
        {
            FollowAnchor(anchor, delta);
            return;
        }
        _anchorAt = null;
        if (grab is { IsGrabbed: true } && Mode.Value is PhotoCameraMode.FirstPerson or PhotoCameraMode.ThirdPerson or PhotoCameraMode.Group)
            Mode.Value = PhotoCameraMode.Manual;

        var mode = Mode.Value;
        if (mode != _lastMode)
        {
            _lastMode = mode;
            _smoothed = false;
            _turned = false;
            _members.Clear();
            _occlusion = float.PositiveInfinity;
        }
        var root = SubjectRoot();
        if (root?.HeadSlot == null)
            return;
        switch (mode)
        {
            case PhotoCameraMode.FirstPerson:
                RideEyes(root, delta);
                break;
            case PhotoCameraMode.ThirdPerson:
                Follow(root, delta, group: false);
                break;
            case PhotoCameraMode.Group:
                Follow(root, delta, group: true);
                break;
            case PhotoCameraMode.Stationary:
                Space(null);
                Aim(Slot.GlobalPosition, LookPoint(root), root.Slot.GlobalRotation * float3.Up, Framing.Value, delta);
                break;
        }
    }

    // The framed user, when the world lets this camera frame someone else; otherwise its operator.
    public UserRoot? SubjectRoot()
    {
        var world = World;
        var subject = Subject.Target;
        if (subject == null || subject.IsDestroyed || RootOf(subject) == null || !CameraRules.FramingOthersAllowed(world))
            subject = Operator.Target;
        return subject == null ? null : RootOf(subject);
    }

    // A user's root as every machine sees it: the record only fills Root in on the host and on the user's
    // own machine, the link carries it everywhere.
    public static UserRoot? RootOf(User user)
        => user.Root is { IsDestroyed: false } root ? root : user.UserRootRef.Target is { IsDestroyed: false } linked ? linked : null;

    private void Space(Slot? space)
    {
        if (ReferenceEquals(space, _space))
            return;
        _space = space;
        _smoothed = false;
        _turned = false;
    }

    private float3 LookPoint(UserRoot root)
    {
        var head = root.HeadPosition;
        return AimAhead.Value ? head + root.HeadRotation * (Ahead * 0.5f * root.GlobalScale) : head;
    }

    private void RideEyes(UserRoot root, float delta)
    {
        Space(root.Slot);
        float scale = root.GlobalScale;
        var head = root.HeadRotation;
        var turn = head * floatQ.AxisAngle(Ahead, Roll.Value * Deg) * floatQ.AxisAngle(float3.Right, -Pitch.Value * Deg);
        var local = root.Slot.GlobalRotationToLocal(turn);
        if (!_turned)
        {
            _turn = _turnMid = local;
            _turned = true;
        }
        SmoothSlerp(ref _turn, local, ref _turnMid, delta * FramingSpeed.Value);
        Slot.GlobalPosition = root.HeadPosition + head * (new float3(0f, 0.02f, -EyeOffset.Value) * scale);
        Slot.GlobalRotation = root.Slot.LocalRotationToGlobal(_turn);
    }

    private void Follow(UserRoot root, float delta, bool group)
    {
        var space = group ? GroupSpace(root) : root.Slot;
        Space(space);
        float scale = root.GlobalScale;
        float unit = ReferenceEquals(space, root.Slot) ? 1f : scale;
        var upGlobal = root.Slot.GlobalRotation * float3.Up;
        var up = DirectionTo(space, upGlobal).Normalized;
        var face = DirectionTo(space, root.HeadRotation * Ahead);
        face -= up * float3.Dot(face, up);
        if (face.LengthSquared < 1e-6f)
            face = DirectionTo(space, root.Slot.GlobalRotation * Ahead);
        face = face.Normalized;

        float3 look;
        float2 framing = Framing.Value;
        float distance = Distance.Value * unit;
        if (group)
        {
            Gather(root, scale);
            look = Centre(out float radius);
            if (_members.Count > 1)
                framing = new float2(0.5f, framing.y);
            float v = System.Math.Clamp(LiveFieldOfView, 5f, 170f) * Deg * 0.5f;
            float h = MathF.Atan(MathF.Tan(v) * Aspect);
            float need = MathF.Max(radius / MathF.Sin(v), radius / MathF.Sin(h));
            float extra = MathF.Max(0f, need - distance);
            if (!_smoothed)
                _extra = _extraMid = extra;
            SmoothLerp(ref _extra, extra, ref _extraMid, delta * PositionSpeed.Value);
            distance += _extra;
        }
        else
            look = LookPoint(root);

        var origin = PointTo(space, group ? look : root.HeadPosition) + up * (Height.Value * unit);
        if (!_smoothed)
        {
            _origin = _originMid = origin;
            _facing = _facingMid = face;
            _smoothed = true;
        }
        SmoothLerp(ref _origin, origin, ref _originMid, delta * PositionSpeed.Value);
        SmoothLerp(ref _facing, face, ref _facingMid, delta * AngleSpeed.Value);
        var facing = _facing.LengthSquared > 1e-6f ? _facing.Normalized : face;
        var direction = floatQ.AxisAngle(up, Orbit.Value * Deg) * facing;
        var position = PointFrom(space, _origin + direction * distance + Wobbling(delta) * unit);
        if (AvoidOcclusion.Value)
            position = Unblocked(look, position, delta);
        Slot.GlobalPosition = position;
        Aim(position, look, upGlobal, framing, delta);
    }

    private Slot? GroupSpace(UserRoot root)
    {
        if (KeepInWorld.Value)
            return null;
        var parent = root.Slot.Parent;
        return parent == null || ReferenceEquals(parent, World?.RootSlot) ? null : parent;
    }

    // Everyone standing with the subject joins; a member stays until they walk past the margin.
    private void Gather(UserRoot root, float scale)
    {
        var subject = root.ActiveUser;
        var centre = root.HeadPosition;
        float join = GroupRadius.Value * scale;
        float leave = (GroupRadius.Value + GroupMargin.Value) * scale;
        _members.RemoveWhere(user => !ReferenceEquals(user, subject)
            && (user.IsDestroyed || RootOf(user)?.HeadSlot == null || GroupExclude.Contains(user)
                || (!GroupInclude.Contains(user) && (RootOf(user)!.HeadPosition - centre).Length > leave)));
        if (subject != null)
            _members.Add(subject);
        _users.Clear();
        World!.GetAllUsers(_users);
        foreach (var user in _users)
        {
            if (user == null || RootOf(user) is not { HeadSlot: not null } other || GroupExclude.Contains(user))
                continue;
            if (GroupInclude.Contains(user) || (other.HeadPosition - centre).Length <= join)
                _members.Add(user);
        }
    }

    // The middle of every member's head and the smallest ball round them all, with a head's width to spare.
    private float3 Centre(out float radius)
    {
        _heads.Clear();
        float pad = 0.15f;
        foreach (var user in _members)
        {
            if (RootOf(user) is not { HeadSlot: not null } member)
                continue;
            _heads.Add(member.HeadPosition);
            pad = MathF.Max(pad, 0.15f * member.GlobalScale);
        }
        if (_heads.Count == 0)
        {
            radius = 0f;
            return Slot.GlobalPosition;
        }
        var a = _heads[0];
        var b = Farthest(a);
        var c = Farthest(b);
        var centre = (b + c) * 0.5f;
        radius = (b - c).Length * 0.5f;
        for (int i = 0; i < _heads.Count; i++)
        {
            float d = (_heads[i] - centre).Length;
            if (d <= radius)
                continue;
            float grown = (radius + d) * 0.5f;
            centre += (_heads[i] - centre) * ((grown - radius) / d);
            radius = grown;
        }
        radius += pad;
        return centre;
    }

    private float3 Farthest(float3 from)
    {
        var best = from;
        float far = -1f;
        for (int i = 0; i < _heads.Count; i++)
        {
            float d = (_heads[i] - from).LengthSquared;
            if (d > far)
            {
                far = d;
                best = _heads[i];
            }
        }
        return best;
    }

    // Walls and floors between the subject and the camera bring it in front of them; people, props and the
    // camera itself do not.
    private float3 Unblocked(float3 look, float3 wanted, float delta)
    {
        var world = World!;
        var ray = wanted - look;
        float length = ray.Length;
        if (length < 1e-4f)
            return wanted;
        var direction = ray / length;
        _skip.Clear();
        _skip.Add(Slot);
        _users.Clear();
        world.GetAllUsers(_users);
        foreach (var user in _users)
        {
            if (user != null && RootOf(user)?.Slot is { } body)
                _skip.Add(body);
        }
        float reach = length;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (!world.Physics.Raycast(look, direction, length, _skip, out var hit) || hit.Slot == null)
                break;
            var collider = hit.Slot.GetComponent<Collider>();
            if (collider == null || collider.CharacterCollider.Value)
            {
                reach = hit.Distance;
                break;
            }
            _skip.Add(hit.Slot);
        }
        if (_occlusion > reach)
            _occlusion = reach;
        else if (_occlusion < length)
            _occlusion += (reach * 1.1f - _occlusion) * System.Math.Clamp(delta * PositionSpeed.Value * 2f, 0f, 1f);
        else
            _occlusion = float.PositiveInfinity;
        return _occlusion < length ? look + direction * _occlusion : wanted;
    }

    // A slow drift, as from a hand holding the camera, eased in and out over a second.
    private float3 Wobbling(float delta)
    {
        float target = Wobble.Value ? 1f : 0f;
        _wobbleWeight += System.Math.Clamp(target - _wobbleWeight, -delta, delta);
        if (_wobbleWeight <= 0f)
            return float3.Zero;
        float t = (float)(World?.Time.TotalTime ?? 0) * 0.2f;
        return new float3(Simplex.Noise1D(_wobbleSeed.x + t), Simplex.Noise1D(_wobbleSeed.y + t), Simplex.Noise1D(_wobbleSeed.z + t))
            * (0.2f * _wobbleWeight);
    }

    // Turned so the point lands where the framing says on the picture, eased in the space it moves in.
    private void Aim(float3 from, float3 at, float3 up, float2 framing, float delta)
    {
        var toward = at - from;
        if (toward.LengthSquared < 1e-8f)
            return;
        float tanV = MathF.Tan(System.Math.Clamp(LiveFieldOfView, 5f, 170f) * Deg * 0.5f);
        var ray = new float3((framing.x * 2f - 1f) * tanV * Aspect, (1f - framing.y * 2f) * tanV, -1f).Normalized;
        var wanted = PhotoShot.Looking(toward.Normalized, up) * FromTo(ray, Ahead);
        var local = _space == null ? wanted : _space.GlobalRotationToLocal(wanted);
        if (!_turned)
        {
            _turn = _turnMid = local;
            _turned = true;
        }
        SmoothSlerp(ref _turn, local, ref _turnMid, delta * FramingSpeed.Value);
        Slot.GlobalRotation = _space == null ? _turn : _space.LocalRotationToGlobal(_turn);
    }

    // Glides so the lens sits on the anchor, at the anchor's width of view; or jumps there.
    private void FollowAnchor(CameraAnchor anchor, float delta)
    {
        var root = anchor.CameraRoot;
        float scale = Slot.GlobalScale.x;
        var rotation = root.GlobalRotation;
        var position = root.GlobalPosition - rotation * (LensOffset * scale);
        float fov = anchor.FieldOfView.Value;
        if (!ReferenceEquals(_anchorAt, anchor))
        {
            _anchorAt = anchor;
            _anchorFov = FieldOfView.Value;
            _anchorPosMid = Slot.GlobalPosition;
            _anchorRotMid = Slot.GlobalRotation;
            _anchorFovMid = _anchorFov;
        }
        if (!AnchorEase.Value)
        {
            Slot.GlobalPosition = position;
            Slot.GlobalRotation = rotation;
            _anchorFov = fov;
            return;
        }
        float speed = MathF.Max(0.01f, AnchorSpeed.Value);
        var current = Slot.GlobalPosition;
        var turn = Slot.GlobalRotation;
        if (AnchorLinear.Value)
        {
            var gap = position - current;
            float step = speed * delta;
            current = gap.Length <= step ? position : current + gap.Normalized * step;
            turn = TurnToward(turn, rotation, speed * 45f * Deg * delta);
            _anchorFov += System.Math.Clamp(fov - _anchorFov, -speed * 10f * delta, speed * 10f * delta);
        }
        else
        {
            SmoothLerp(ref current, position, ref _anchorPosMid, delta * speed);
            SmoothSlerp(ref turn, rotation, ref _anchorRotMid, delta * speed);
            SmoothLerp(ref _anchorFov, fov, ref _anchorFovMid, delta * speed);
        }
        Slot.GlobalPosition = current;
        Slot.GlobalRotation = turn;
    }

    // Starts again from the top of the route on the next frame.
    public void RestartRoute() => _routeAt = null;

    // Along the route at its own pace. With the glide on, a camera that starts away from the route first eases
    // over onto its first stop; with it off the camera cuts straight there.
    private void FollowRoute(CameraRoute route, float delta)
    {
        if (!ReferenceEquals(_routeAt, route))
        {
            _routeAt = route;
            _routeClock = 0;
            _routeDone = false;
            float scale = Slot.GlobalScale.x;
            _leadRotation = Slot.GlobalRotation;
            _leadPosition = Slot.GlobalPosition + _leadRotation * (LensOffset * scale);
            _leadFov = LiveFieldOfView;
            _leadSeconds = AnchorEase.Value ? LeadIn(route) : 0f;
        }
        else
            _routeClock += delta;

        float3 lens;
        floatQ rotation;
        float fov;
        if (_routeClock < _leadSeconds)
        {
            route.Sample(0, out var start, out var startTurn, out var startFov);
            float t = (float)(_routeClock / _leadSeconds);
            float k = t * t * t * (t * (t * 6f - 15f) + 10f);
            lens = float3.Lerp(_leadPosition, start, k);
            rotation = floatQ.Slerp(_leadRotation, startTurn, k);
            fov = _leadFov + (startFov - _leadFov) * k;
        }
        else
            _routeDone = !route.Sample(_routeClock - _leadSeconds, out lens, out rotation, out fov);
        Slot.GlobalRotation = rotation;
        Slot.GlobalPosition = lens - rotation * (LensOffset * Slot.GlobalScale.x);
        _routeFov = fov;
    }

    // Long enough to move and turn onto the route at about its own pace, between a moment and a few seconds.
    private float LeadIn(CameraRoute route)
    {
        route.Sample(0, out var start, out var startTurn, out _);
        float metres = (start - _leadPosition).Length;
        float angle = 2f * MathF.Acos(System.Math.Clamp(MathF.Abs(floatQ.Dot(_leadRotation, startTurn)), 0f, 1f));
        if (metres < 0.02f && angle < 0.02f)
            return 0f;
        return System.Math.Clamp(MathF.Max(metres / 1.2f, angle / 1.2f), 0.8f, 3f);
    }

    // SMOOTHING
    //
    // Two eased stages, the second chasing the first, so a move starts gently and settles gently.

    private static void SmoothLerp(ref float3 current, float3 target, ref float3 middle, float d)
    {
        float k = System.Math.Clamp(d * 2f, 0f, 1f);
        middle = float3.Lerp(middle, target, k);
        current = float3.Lerp(current, middle, k);
    }

    private static void SmoothLerp(ref float current, float target, ref float middle, float d)
    {
        float k = System.Math.Clamp(d * 2f, 0f, 1f);
        middle += (target - middle) * k;
        current += (middle - current) * k;
    }

    private static void SmoothSlerp(ref floatQ current, floatQ target, ref floatQ middle, float d)
    {
        float k = System.Math.Clamp(d * 2f, 0f, 1f);
        middle = floatQ.Slerp(middle, target, k);
        current = floatQ.Slerp(current, middle, k);
    }

    private static floatQ TurnToward(floatQ from, floatQ to, float maxRadians)
    {
        float dot = MathF.Abs(from.x * to.x + from.y * to.y + from.z * to.z + from.w * to.w);
        float angle = 2f * MathF.Acos(System.Math.Clamp(dot, -1f, 1f));
        if (angle <= maxRadians || angle < 1e-5f)
            return to;
        return floatQ.Slerp(from, to, maxRadians / angle);
    }

    // The turn that carries one direction onto another.
    private static floatQ FromTo(float3 from, float3 to)
    {
        float dot = float3.Dot(from, to);
        if (dot > 0.99999f)
            return floatQ.Identity;
        if (dot < -0.99999f)
        {
            var axis = float3.Cross(float3.Up, from);
            if (axis.LengthSquared < 1e-6f)
                axis = float3.Cross(float3.Right, from);
            return floatQ.AxisAngle(axis.Normalized, MathF.PI);
        }
        var cross = float3.Cross(from, to);
        return new floatQ(cross.x, cross.y, cross.z, 1f + dot).Normalized;
    }

    private static float3 PointTo(Slot? space, float3 point) => space == null ? point : space.GlobalPointToLocal(point);
    private static float3 PointFrom(Slot? space, float3 point) => space == null ? point : space.LocalPointToGlobal(point);
    private static float3 DirectionTo(Slot? space, float3 direction) => space == null ? direction : space.GlobalDirectionToLocal(direction);

    // WHAT IT FILMS

    private void UpdateFeed()
    {
        // A world in the background is not being looked at, so its cameras stop filming.
        bool want = RendersHere && World?.Focus == World.WorldFocus.Focused;
        if (want && _feedCamera == null)
            BuildFeed();
        else if (!want && _feedCamera != null)
            DestroyFeed();
        if (_feedCamera == null || _feedCamera.IsDestroyed)
            return;
        float scale = Slot.GlobalScale.x;
        bool eyes = IsMine && Mode.Value == PhotoCameraMode.FirstPerson && !Placed;
        Put(_feedCamera.FieldOfView, System.Math.Clamp(LiveFieldOfView, 1f, 170f));
        Put(_feedCamera.CullingMask, BandMask(HideNameplates.Value, HideLasers.Value));
        Put(_feedCamera.WearerView, eyes);
        Put(_feedCamera.RenderPrivateUI, IsMine && PrivateUIHere);
        Put(_feedCamera.NearClip, MathF.Max(1e-4f, (eyes ? 0.01f : 0.05f) * scale));
        int signature = LeftOut.Count;
        foreach (var slot in LeftOut)
            signature = signature * 31 + (slot?.ReferenceID.GetHashCode() ?? 0);
        if (signature != _leftOutSignature)
        {
            _leftOutSignature = signature;
            _feedCamera.ExcludeRender.Clear();
            if (_visuals != null)
                _feedCamera.ExcludeRender.Add(_visuals);
            foreach (var slot in LeftOut)
            {
                if (slot is { IsDestroyed: false })
                    _feedCamera.ExcludeRender.Add(slot);
            }
        }
    }

    public static int BandMask(bool hideNameplates, bool hideLasers)
    {
        int mask = -1;
        if (hideNameplates)
            mask &= ~(RenderLayerOverride.NameplateLayer | RenderLayerOverride.OwnNameplateLayer);
        if (hideLasers)
            mask &= ~RenderLayerOverride.PointerLayer;
        return mask;
    }

    private void BuildFeed()
    {
        _feedSlot = Slot.AddLocalSlot("Feed");
        _feedSlot.LocalPosition.Value = LensOffset;
        _feedTexture = _feedSlot.AttachComponent<RenderTextureProvider>();
        _feedTexture.Size.Value = new int2(FeedWidth, FeedHeight);
        _feedCamera = _feedSlot.AttachComponent<Camera>();
        _feedCamera.FarClip.Value = 4000f;
        _feedCamera.TargetTexture.Target = _feedTexture;
        _leftOutSignature = -1;
        if (_screenMaterial != null)
            _screenMaterial.Texture.Target = _feedTexture;
        Put(_screenMaterial?.TintColor, colorHDR.White);
    }

    private void DestroyFeed()
    {
        if (_screenMaterial is { IsDestroyed: false })
        {
            _screenMaterial.Texture.Target = null!;
            Put(_screenMaterial.TintColor, new colorHDR(0.02f, 0.02f, 0.025f, 1f));
        }
        if (_feedSlot is { IsDestroyed: false })
            _feedSlot.Destroy();
        _feedSlot = null;
        _feedCamera = null;
        _feedTexture = null;
    }

    // WHAT EVERYONE SEES
    //
    // Built on every machine for itself, so each one can hide it for its own viewer without telling anyone.

    private void UpdateVisuals(double now)
    {
        if (_visuals == null || _visuals.IsDestroyed)
            BuildVisuals();
        float scale = Slot.GlobalScale.x;
        var mode = Mode.Value;
        bool free = !Placed;
        bool hidden = HideBody.Value || (free && mode == PhotoCameraMode.FirstPerson);
        if (!hidden && free && mode is PhotoCameraMode.ThirdPerson or PhotoCameraMode.Group
            && World?.LocalUser?.Root is { HeadSlot: not null } viewer)
            hidden = (viewer.HeadPosition - Slot.GlobalPosition).Length < HideNearHead * MathF.Max(scale, viewer.GlobalScale);
        SetActive(_bodyParts, !hidden);
        SetActive(_frustum, ShowFrustum.Value);
        if (ShowFrustum.Value && MathF.Abs(_frustumFov - LiveFieldOfView) > 0.05f)
            ShapeFrustum(LiveFieldOfView);
        bool live = Operator.Target is { } user && user.IsLive.Value;
        Put(_lightMaterial?.TintColor, live ? new colorHDR(1f, 0.08f, 0.06f, 1f) : new colorHDR(0.05f, 0.02f, 0.02f, 1f));

        double left = _countdownEnds >= 0 ? _countdownEnds - now : -1;
        bool counting = left > 0;
        string text = counting ? $"{left:0.0}" : string.Empty;
        color tint = !counting ? color.White
            : ((int)(now / (left < 2 ? 0.25 : 0.5)) % 2 == 0 ? new color(1f, 0.25f, 0.2f, 1f) : color.White);
        ShowCount(_countFront, text, tint);
        ShowCount(_countBack, text, tint);
    }

    private static void ShowCount(TextRenderer? count, string text, color tint)
    {
        if (count == null || count.IsDestroyed)
            return;
        Put(count.Text, text);
        Put(count.Color, tint);
    }

    private void BuildVisuals()
    {
        _visuals = Slot.AddLocalSlot("Visuals");
        _bodyParts = _visuals.AddLocalSlot("Body");

        var body = _bodyParts.AddLocalSlot("Box");
        var box = body.AttachComponent<BoxMesh>();
        box.Size.Value = new float3(BodyWidth, BodyHeight, BodyDepth);
        Paint(body, box, new colorHDR(0.08f, 0.085f, 0.1f, 1f), 0.4f);

        var lensSlot = _bodyParts.AddLocalSlot("Lens");
        lensSlot.LocalPosition.Value = new float3(0f, 0f, -(BodyDepth * 0.5f + 0.014f));
        lensSlot.LocalRotation.Value = floatQ.AxisAngle(float3.Right, MathF.PI * 0.5f);
        var lens = lensSlot.AttachComponent<CylinderMesh>();
        lens.Radius.Value = 0.024f;
        lens.Height.Value = 0.028f;
        Paint(lensSlot, lens, new colorHDR(0.03f, 0.03f, 0.035f, 1f), 0.9f);

        var screenSlot = _bodyParts.AddLocalSlot("Screen");
        screenSlot.LocalPosition.Value = new float3(0f, 0f, BodyDepth * 0.5f + 0.0008f);
        screenSlot.LocalRotation.Value = floatQ.AxisAngle(float3.Up, MathF.PI);
        var screen = screenSlot.AttachComponent<QuadMesh>();
        screen.Size.Value = new float2(BodyWidth * 0.86f, BodyWidth * 0.86f / Aspect);
        var screenRenderer = screenSlot.AttachComponent<MeshRenderer>();
        screenRenderer.Mesh.Target = screen;
        _screenMaterial = screenSlot.AttachComponent<UnlitMaterial>();
        _screenMaterial.TintColor.Value = _feedTexture != null ? colorHDR.White : new colorHDR(0.02f, 0.02f, 0.025f, 1f);
        if (_feedTexture != null)
            _screenMaterial.Texture.Target = _feedTexture;
        screenRenderer.Material.Target = _screenMaterial;

        var lightSlot = _bodyParts.AddLocalSlot("Light");
        lightSlot.LocalPosition.Value = new float3(BodyWidth * 0.36f, BodyHeight * 0.5f + 0.004f, -BodyDepth * 0.2f);
        var light = lightSlot.AttachComponent<SphereMesh>();
        light.Radius.Value = 0.006f;
        var lightRenderer = lightSlot.AttachComponent<MeshRenderer>();
        lightRenderer.Mesh.Target = light;
        _lightMaterial = lightSlot.AttachComponent<UnlitMaterial>();
        lightRenderer.Material.Target = _lightMaterial;

        // Text with no font draws nothing.
        var fontSlot = _visuals.AddLocalSlot("Font");
        var font = fontSlot.AttachComponent<FontProvider>();
        font.URL.Value = Lumora.Core.Components.UI.DashTheme.FontRegular;
        // Each count reads from its own side only; drawn from both, each side showed the other's mirrored. -xlinka
        var oneSided = fontSlot.AttachComponent<TextMaterial>();
        oneSided.Culling.Value = Culling.Back;
        _countFront = Count(_visuals, "Count Front", new float3(0f, BodyHeight * 0.5f + 0.035f, -BodyDepth * 0.5f), floatQ.Identity, font, oneSided);
        _countBack = Count(_visuals, "Count Back", new float3(0f, BodyHeight * 0.5f + 0.035f, BodyDepth * 0.5f), floatQ.AxisAngle(float3.Up, MathF.PI), font, oneSided);

        _frustum = _visuals.AddLocalSlot("Frustum");
        _frustum.LocalPosition.Value = LensOffset;
        _frustumFov = -1f;
        _frustum.ActiveSelf.Value = false;
    }

    private static TextRenderer Count(Slot parent, string name, float3 position, floatQ rotation, FontProvider font, TextMaterial style)
    {
        var slot = parent.AddLocalSlot(name);
        slot.LocalPosition.Value = position;
        slot.LocalRotation.Value = rotation;
        var text = slot.AttachComponent<TextRenderer>();
        text.Font.Target = font;
        text.Style.Target = style;
        text.Size.Value = 0.05f;
        text.HorizontalAlign.Value = TextHorizontalAlignment.Center;
        text.VerticalAlign.Value = TextVerticalAlignment.Middle;
        text.Color.Value = color.White;
        return text;
    }

    // What the lens takes in, as edges reaching two metres out.
    private void ShapeFrustum(float fov)
    {
        if (_frustum == null)
            return;
        _frustumFov = fov;
        _frustum.DestroyChildren();
        float far = 2f;
        float halfV = MathF.Tan(System.Math.Clamp(fov, 1f, 170f) * Deg * 0.5f) * far;
        float halfH = halfV * Aspect;
        var corners = new[]
        {
            new float3(-halfH, -halfV, -far), new float3(halfH, -halfV, -far),
            new float3(halfH, halfV, -far), new float3(-halfH, halfV, -far),
        };
        var tint = new colorHDR(0.9f, 0.1f, 0.1f, 0.35f);
        for (int i = 0; i < 4; i++)
        {
            Edge(_frustum, float3.Zero, corners[i], tint);
            Edge(_frustum, corners[i], corners[(i + 1) % 4], tint);
        }
    }

    private static void Edge(Slot parent, float3 from, float3 to, colorHDR tint)
    {
        var span = to - from;
        float length = span.Length;
        if (length < 1e-5f)
            return;
        var slot = parent.AddLocalSlot("Edge");
        slot.LocalPosition.Value = (from + to) * 0.5f;
        slot.LocalRotation.Value = PhotoShot.Looking(span / length, MathF.Abs(span.y) > length * 0.99f ? float3.Right : float3.Up);
        var mesh = slot.AttachComponent<BoxMesh>();
        mesh.Size.Value = new float3(0.003f, 0.003f, length);
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        var material = slot.AttachComponent<UnlitMaterial>();
        material.TintColor.Value = tint;
        material.BlendMode.Value = BlendMode.Alpha;
        renderer.Material.Target = material;
    }

    private static void Paint(Slot slot, ProceduralMesh mesh, colorHDR tint, float smoothness)
    {
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        var material = slot.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = tint;
        material.Smoothness.Value = smoothness;
        renderer.Material.Target = material;
    }

    private static void SetActive(Slot? slot, bool active)
    {
        if (slot != null && !slot.IsDestroyed && slot.ActiveSelf.Value != active)
            slot.ActiveSelf.Value = active;
    }

    private static void Put<T>(Sync<T>? field, T value)
    {
        if (field != null && !EqualityComparer<T>.Default.Equals(field.Value, value))
            field.Value = value;
    }

    // HANDS ON THE CAMERA

    // Held by someone allowed to work it, the holding hand's buttons are the camera's.
    private void HoldControls()
    {
        var input = Engine.Current?.InputInterface;
        var me = World?.LocalUser;
        var grabber = Slot.GetComponent<Grabbable>()?.Grabber;
        var side = Chirality.None;
        if (grabber != null && grabber.Slot?.ActiveUserRoot?.ActiveUser is { } holder && ReferenceEquals(holder, me) && MayOperate(me)
            && !Placed)
            side = grabber.Slot.GetComponentInParents<HandTool>()?.Side.Value ?? Chirality.None;
        if (input != null && side != _claimed)
        {
            if (_claimed != Chirality.None)
                input.ReleaseHand(_claimed, this);
            if (side != Chirality.None)
                input.ClaimHand(side, this);
            _claimed = side;
        }
        if (input == null || side == Chirality.None)
            return;
        var hand = input.Actions?.Interaction(side);
        if (hand == null)
            return;
        var shape = CameraDirector.Local?.HandShape ?? PhotoShape.Flat;
        if (hand.Primary.Pressed)
            Capture(shape, CameraDirector.Local?.SpawnPhotosInWorld ?? true);
        else if (hand.Secondary.Pressed)
            StartCountdown(shape, EngineSettings.PhotoTimerSeconds);
    }

    // COUNTDOWN

    // Starts the count, or stops one already running. The machine that started it takes the photo.
    public void StartCountdown(PhotoShape shape, float seconds)
    {
        var world = World;
        if (world == null)
            return;
        bool stop = _countdownEnds >= 0;
        _countdownEnds = stop ? -1 : world.Time.TotalTime + seconds;
        _countdownShape = shape;
        _countdownMine = !stop;
        if (IsMine)
        {
            CountdownSeconds.Value = stop ? 0f : seconds;
            CountdownStamp.Value = CountdownStamp.Value + 1;
            _seenStamp = CountdownStamp.Value;
        }
    }

    private void UpdateCountdown(double now)
    {
        if (CountdownStamp.Value != _seenStamp)
        {
            _seenStamp = CountdownStamp.Value;
            if (!_countdownMine)
                _countdownEnds = CountdownSeconds.Value > 0f ? now + CountdownSeconds.Value : -1;
        }
        if (_countdownEnds < 0 || now < _countdownEnds)
            return;
        _countdownEnds = -1;
        if (!_countdownMine)
            return;
        _countdownMine = false;
        Capture(_countdownShape, CameraDirector.Local?.SpawnPhotosInWorld ?? true);
    }

    // PHOTOS

    // A photo through the lens, the way the view is filmed, printed above the camera turned to whoever took
    // it, or in front of them; a world that will not take it, or a photo not wanted in the world, goes
    // straight to the inventory.
    public void Capture(PhotoShape shape, bool inWorld = true, bool inFront = false)
    {
        var world = World;
        if (world == null || _busy)
            return;
        var user = world.LocalUser;
        _busy = true;
        float scale = Slot.GlobalScale.x;
        var rotation = Slot.GlobalRotation;
        var lens = Slot.GlobalPosition + rotation * (LensOffset * scale);
        float fov = System.Math.Clamp(LiveFieldOfView, 5f, 170f);
        var size = EngineSettings.PhotoSizes[System.Math.Clamp(EngineSettings.PhotoSize, 0, EngineSettings.PhotoSizes.Length - 1)];
        var encoding = shape == PhotoShape.Object ? ImageEncoding.Png : EngineSettings.PhotoEncoding;
        int mask = BandMask(HideNameplates.Value, true);
        var leaveOut = new List<Slot>();
        if (_visuals != null)
            leaveOut.Add(_visuals);
        foreach (var slot in LeftOut)
        {
            if (slot is { IsDestroyed: false })
                leaveOut.Add(slot);
        }
        bool eyes = IsMine && Mode.Value == PhotoCameraMode.FirstPerson && !Placed;
        var up = rotation * float3.Up;
        float3 printAt;
        floatQ facing;
        var head = user?.Root;
        if (inFront && head != null)
        {
            float userScale = head.GlobalScale;
            printAt = head.HeadPosition + head.HeadRotation * (Ahead * 0.5f * userScale);
            facing = PhotoShot.Facing((head.HeadPosition - printAt).Normalized, head.Slot.GlobalRotation * float3.Up);
        }
        else
        {
            printAt = shape == PhotoShape.Panorama ? Slot.GlobalPosition + up * (0.2f * scale) : Slot.GlobalPosition + up * (0.14f * scale);
            var toward = (head?.HeadPosition ?? printAt + rotation * float3.Forward) - printAt;
            facing = PhotoShot.Facing(toward.LengthSquared > 1e-6f ? toward.Normalized : rotation * float3.Forward, up);
        }
        var facts = new PhotoFacts(user, Describe(shape), lens, rotation, fov);
        var self = Slot;
        StartTask(async () =>
        {
            try
            {
                var picture = shape switch
                {
                    PhotoShape.Stereo => await Stereo(world, lens, rotation, scale, size, fov, mask, leaveOut, eyes),
                    PhotoShape.Panorama => await Panorama(world, lens, rotation, size, mask, leaveOut, eyes),
                    PhotoShape.Object => await Alone(world, lens, rotation, fov, size, self),
                    _ => await Flat(world, lens, rotation, size, fov, mask, leaveOut, eyes),
                };
                if (picture == null)
                {
                    Logging.Logger.Warn("Photos: the camera gave back no picture");
                    return;
                }
                var (bytes, written) = PhotoEncoder.Encode(picture.Rgba, picture.Width, picture.Height, encoding, PhotoShot.Quality(encoding));
                if (bytes == null)
                    return;
                float width = (shape == PhotoShape.Panorama ? 0.3f : 0.2f) * scale;
                await PhotoPrint.MakeAsync(world, bytes, written, printAt, facing, width, facts, shape, inWorld);
            }
            finally
            {
                await WorldContext.ToWorld();
                _busy = false;
            }
        });
    }

    private static string Describe(PhotoShape shape) => shape switch
    {
        PhotoShape.Stereo => "Camera, stereo",
        PhotoShape.Panorama => "Camera, 360",
        PhotoShape.Object => "Camera, object",
        _ => "Camera",
    };

    private static async System.Threading.Tasks.Task<PixelFrame?> Flat(World world, float3 lens, floatQ rotation,
        (int Width, int Height) size, float fov, int mask, List<Slot> leaveOut, bool eyes)
    {
        var frames = await PhotoShot.TakeRawAsync(world, new[] { (lens, rotation) },
            new PhotoLens(fov, size.Width, size.Height) { Mask = mask, LeaveOut = leaveOut, WearerView = eyes });
        return frames?[0];
    }

    // One picture per eye, an eye's width apart, side by side: left, then right.
    private static async System.Threading.Tasks.Task<PixelFrame?> Stereo(World world, float3 lens, floatQ rotation, float scale,
        (int Width, int Height) size, float fov, int mask, List<Slot> leaveOut, bool eyes)
    {
        var half = rotation * float3.Right * (0.032f * scale);
        var frames = await PhotoShot.TakeRawAsync(world, new[] { (lens - half, rotation), (lens + half, rotation) },
            new PhotoLens(fov, size.Width, size.Height) { Mask = mask, LeaveOut = leaveOut, WearerView = eyes });
        if (frames?[0] is not { } left || frames[1] is not { } right || left.Width != right.Width || left.Height != right.Height)
            return null;
        return new PixelFrame(left.Width * 2, left.Height, PhotoStitch.SideBySide(left.Rgba, right.Rgba, left.Width, left.Height));
    }

    // All the way round from the lens, level, its middle where the camera faces.
    private static async System.Threading.Tasks.Task<PixelFrame?> Panorama(World world, float3 lens, floatQ rotation,
        (int Width, int Height) size, int mask, List<Slot> leaveOut, bool eyes)
    {
        int width = size.Width >= 2560 ? 4096 : 2048;
        int face = width / 4;
        var forward = rotation * Ahead;
        forward.y = 0f;
        var level = PhotoShot.Looking(forward.LengthSquared > 1e-6f ? forward.Normalized : Ahead, float3.Up);
        var views = new (float3, floatQ)[PhotoStitch.FaceCount];
        for (int i = 0; i < views.Length; i++)
        {
            var (x, y, z, w) = PhotoStitch.FaceRotation(i);
            views[i] = (lens, level * new floatQ(x, y, z, w));
        }
        var frames = await PhotoShot.TakeRawAsync(world, views, new PhotoLens(90f, face, face) { Mask = mask, LeaveOut = leaveOut, WearerView = eyes });
        if (frames == null)
            return null;
        var faces = new byte[frames.Length][];
        for (int i = 0; i < frames.Length; i++)
        {
            if (frames[i] is not { } frame || frame.Width != face || frame.Height != face)
                return null;
            faces[i] = frame.Rgba;
        }
        return new PixelFrame(width, width / 2, PhotoStitch.Equirect(faces, face, width));
    }

    // Whatever the lens points at, alone on a clear background, framed to fit.
    private static async System.Threading.Tasks.Task<PixelFrame?> Alone(World world, float3 lens, floatQ rotation, float fov,
        (int Width, int Height) size, Slot self)
    {
        var skip = new List<Slot> { self };
        if (world.LocalUser?.Root?.Slot is { } body)
            skip.Add(body);
        if (!world.Physics.Raycast(lens, rotation * Ahead, 100f, skip, out var hit) || hit.Slot == null)
        {
            Logging.Logger.Log("Photos: nothing in front of the camera to take alone");
            return null;
        }
        if (ObjectAt(hit.Slot) is not { } target || !SlotBoundsHelper.TryComputeWorldBounds(target, out var bounds))
            return null;
        var centre = bounds.Center;
        float radius = MathF.Max(0.02f, bounds.Extents.Length);
        float v = fov * Deg * 0.5f;
        float h = MathF.Atan(MathF.Tan(v) * Aspect);
        float distance = MathF.Max(radius / MathF.Sin(v), radius / MathF.Sin(h));
        var toward = centre - lens;
        var direction = toward.LengthSquared > 1e-6f ? toward.Normalized : rotation * Ahead;
        var position = centre - direction * distance;
        // Filmed alone, the world's lights sit on bands this camera does not draw, so it brings a studio's.
        var frames = await PhotoShot.TakeRawAsync(world, new[] { (position, PhotoShot.Looking(direction, rotation * float3.Up)) },
            new PhotoLens(fov, size.Width, size.Height) { Only = target, Transparent = true, Studio = true, NearClip = MathF.Max(0.005f, (distance - radius) * 0.5f) });
        return frames?[0];
    }

    // One object: a person whole, else the nearest marked or grabbable slot above the hit, else the top of
    // its pile under the world root. Never the world itself.
    private static Slot? ObjectAt(Slot hit)
    {
        if (hit.ActiveUserRoot?.Slot is { } person)
            return person;
        for (var current = hit; current != null && !current.IsRootSlot; current = current.Parent)
        {
            if (current.GetComponent<ObjectRoot>() != null || current.GetComponent<Grabbable>() != null)
                return current;
        }
        var top = hit;
        while (top.Parent != null && !top.Parent.IsRootSlot)
            top = top.Parent;
        return top.IsRootSlot ? null : top;
    }

    // EYES

    // Where a wearer's eyes go when one of their cameras wants them: the nearest such lens within 75 degrees
    // of where they face.
    public static bool TryEyeTarget(User wearer, float3 head, float3 facing, out float3 target)
    {
        target = default;
        float best = float.MaxValue;
        foreach (var camera in InWorld(wearer.World))
        {
            if (camera.IsDestroyed || !camera.EyesOnCamera.Value || !ReferenceEquals(camera.Operator.Target, wearer))
                continue;
            var lens = camera.Slot.GlobalPosition + camera.Slot.GlobalRotation * (LensOffset * camera.Slot.GlobalScale.x);
            var toward = lens - head;
            float distance = toward.Length;
            if (distance < 1e-3f || float3.Dot(toward / distance, facing) < MathF.Cos(75f * Deg))
                continue;
            if (distance < best)
            {
                best = distance;
                target = lens;
            }
        }
        return best < float.MaxValue;
    }
}
