// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Core.Assets;
using Lumora.Core.Components.Audio;
using Lumora.Core.Components.Twitch;
using Lumora.Core.Math;
using Lumora.Core.Photos;

namespace Lumora.Core.Components;

// What this machine's camera does, kept on this machine. While on, one camera stands in whichever world has
// focus: the local user's own if they have one there, otherwise a fresh one that is not saved with the world.
// Every choice lives here and is pushed onto that camera, so it follows the user from world to world; the
// framing, the smoothing and most switches are kept in the settings and follow the account. Who else may work
// the camera, who is framed and who is in a group are not kept: they are about the people in the room.
//
// It also owns what goes with streaming: the camera on the desktop window (in a headset, or on a desktop while
// the dash is shut), the camera's ears, the live mark, the link to the streaming program and the chat. The dash
// widgets read and change all of it here.
//
// This runs in userspace. The camera lives in another world, so everything done to it is queued onto that
// world's own update. -xlinka
[ComponentCategory("Hidden")]
public sealed class CameraDirector : Component
{
    public static CameraDirector? Local { get; private set; }

    public event Action? StateChanged;

    internal const string ObsAddressKey = "Engine.Stream.ObsAddress";
    internal const string ObsPasswordKey = "Engine.Stream.ObsPassword";
    public const string DefaultObsAddress = "ws://127.0.0.1:4455";

    // NOT KEPT

    private bool _active;
    public bool Active { get => _active; set => Set(ref _active, value); }

    private PhotoCameraMode _mode = PhotoCameraMode.ThirdPerson;
    public PhotoCameraMode Mode
    {
        get => _mode;
        set
        {
            if (value != PhotoCameraMode.Manual)
            {
                _anchor = null;
                _route = null;
            }
            Set(ref _mode, value);
        }
    }

    // Who is framed, by user key; empty frames the user at this machine.
    private string _subject = string.Empty;
    public string Subject { get => _subject; set => Set(ref _subject, value ?? string.Empty); }

    public readonly HashSet<string> Operators = new(StringComparer.Ordinal);
    public readonly HashSet<string> GroupInclude = new(StringComparer.Ordinal);
    public readonly HashSet<string> GroupExclude = new(StringComparer.Ordinal);

    private bool _anyoneCanOperate;
    public bool AnyoneCanOperate { get => _anyoneCanOperate; set => Set(ref _anyoneCanOperate, value); }
    private bool _renderForEveryone;
    public bool RenderForEveryone { get => _renderForEveryone; set => Set(ref _renderForEveryone, value); }
    private bool _privateUI;
    public bool PrivateUI { get => _privateUI; set => Set(ref _privateUI, value); }
    private bool _flipPreview;
    public bool FlipPreview { get => _flipPreview; set => Set(ref _flipPreview, value); }
    private bool _mirror;
    public bool Mirror { get => _mirror; set => Set(ref _mirror, value); }
    private bool _hideBody;
    public bool HideBody { get => _hideBody; set => Set(ref _hideBody, value); }
    private bool _showFrustum;
    public bool ShowFrustum { get => _showFrustum; set => Set(ref _showFrustum, value); }
    // Photos from the panel go through the timer.
    private bool _timed;
    public bool Timed { get => _timed; set => Set(ref _timed, value); }
    private bool _showLive;
    public bool ShowLive { get => _showLive; set => Set(ref _showLive, value); }
    private bool? _audioFromCamera;
    public bool AudioFromCamera
    {
        get => _audioFromCamera ?? (Engine.Current?.AudioSystem?.HasStreamOutput ?? false);
        set
        {
            _audioFromCamera = value;
            StateChanged?.Invoke();
        }
    }
    // What the camera's own trigger takes.
    private PhotoShape _handShape = PhotoShape.Flat;
    public PhotoShape HandShape { get => _handShape; set => Set(ref _handShape, value); }

    private CameraAnchor? _anchor;
    public CameraAnchor? Anchor => _anchor is { IsDestroyed: false } ? _anchor : null;

    private CameraRoute? _route;
    public CameraRoute? Route => _route is { IsDestroyed: false } ? _route : null;
    private volatile bool _routeRestart;
    private volatile CameraRoute? _madeRoute;

    // KEPT, FOLLOWING THE ACCOUNT

    private float _fieldOfView = 60f;
    public float FieldOfView { get => _fieldOfView; set => Keep(ref _fieldOfView, System.Math.Clamp(value, 0.1f, 179f), "FieldOfView"); }
    private float _orbit;
    public float Orbit { get => _orbit; set => Keep(ref _orbit, Wrap(value), "Orbit"); }
    private float _distance = 1.5f;
    public float Distance { get => _distance; set => Keep(ref _distance, MathF.Max(0.1f, value), "Distance"); }
    private float _height;
    public float Height { get => _height; set => Keep(ref _height, value, "Height"); }
    private float _pitch = 5f;
    public float Pitch { get => _pitch; set => Keep(ref _pitch, System.Math.Clamp(value, -45f, 45f), "Pitch"); }
    private float _roll;
    public float Roll { get => _roll; set => Keep(ref _roll, System.Math.Clamp(value, -180f, 180f), "Roll"); }
    private float _eyeOffset = 0.02f;
    public float EyeOffset { get => _eyeOffset; set => Keep(ref _eyeOffset, System.Math.Clamp(value, -0.2f, 0.2f), "EyeOffset"); }
    private float _framingX = 0.5f;
    private float _framingY = 0.33f;
    public float2 Framing => new(_framingX, _framingY);
    private bool _aimAhead;
    public bool AimAhead { get => _aimAhead; set => Keep(ref _aimAhead, value, "AimAhead"); }
    private bool _avoidOcclusion = true;
    public bool AvoidOcclusion { get => _avoidOcclusion; set => Keep(ref _avoidOcclusion, value, "AvoidOcclusion"); }
    private bool _keepInWorld;
    public bool KeepInWorld { get => _keepInWorld; set => Keep(ref _keepInWorld, value, "KeepInWorld"); }
    private bool _wobble;
    public bool Wobble { get => _wobble; set => Keep(ref _wobble, value, "Wobble"); }
    private bool _hideNameplates;
    public bool HideNameplates { get => _hideNameplates; set => Keep(ref _hideNameplates, value, "HideNameplates"); }
    private bool _hideLasers;
    public bool HideLasers { get => _hideLasers; set => Keep(ref _hideLasers, value, "HideLasers"); }
    private bool _eyesOnCamera;
    public bool EyesOnCamera { get => _eyesOnCamera; set => Keep(ref _eyesOnCamera, value, "EyesOnCamera"); }
    private float _groupRadius = 2.5f;
    public float GroupRadius { get => _groupRadius; set => Keep(ref _groupRadius, System.Math.Clamp(value, 0.5f, 10f), "GroupRadius"); }
    private float _groupMargin = 0.5f;
    public float GroupMargin { get => _groupMargin; set => Keep(ref _groupMargin, System.Math.Clamp(value, 0.1f, 2f), "GroupMargin"); }
    private float _positionSpeed = 2f;
    public float PositionSpeed { get => _positionSpeed; set => Keep(ref _positionSpeed, System.Math.Clamp(value, 0.5f, 6f), "PositionSpeed"); }
    private float _angleSpeed = 0.25f;
    public float AngleSpeed { get => _angleSpeed; set => Keep(ref _angleSpeed, System.Math.Clamp(value, 0.1f, 2f), "AngleSpeed"); }
    private float _framingSpeed = 5f;
    public float FramingSpeed { get => _framingSpeed; set => Keep(ref _framingSpeed, System.Math.Clamp(value, 1f, 20f), "FramingSpeed"); }
    private bool _anchorEase = true;
    public bool AnchorEase { get => _anchorEase; set => Keep(ref _anchorEase, value, "AnchorEase"); }
    private float _anchorSpeed = 2.5f;
    public float AnchorSpeed { get => _anchorSpeed; set => Keep(ref _anchorSpeed, System.Math.Clamp(value, 0f, 40f), "AnchorSpeed"); }
    private bool _anchorLinear;
    public bool AnchorLinear { get => _anchorLinear; set => Keep(ref _anchorLinear, value, "AnchorLinear"); }
    private bool _spawnPhotosInWorld = true;
    public bool SpawnPhotosInWorld { get => _spawnPhotosInWorld; set => Keep(ref _spawnPhotosInWorld, value, "SpawnPhotosInWorld"); }
    // The stream carries the operator's own voice from where the camera hears it.
    private bool _ownVoiceOnStream = true;
    public bool OwnVoiceOnStream { get => _ownVoiceOnStream; set => Keep(ref _ownVoiceOnStream, value, "OwnVoiceOnStream"); }
    // The camera goes on the desktop window when the streaming program starts streaming or recording.
    private bool _autoMirror;
    public bool AutoMirror { get => _autoMirror; set => Keep(ref _autoMirror, value, "AutoMirror"); }
    private string _chatChannel = string.Empty;
    public string ChatChannel { get => _chatChannel; set => Keep(ref _chatChannel, TwitchChat.NormalizeChannel(value), "ChatChannel"); }

    // STATE OF THE CAMERA

    private volatile PhotoCamera? _camera;
    private World? _cameraWorld;
    private readonly Dictionary<World, PhotoCamera> _spawned = new();
    // Worlds where a favourite camera is still coming down.
    private readonly HashSet<World> _fetching = new();
    private string _status = string.Empty;
    public string Status => _status;
    public PhotoCamera? Camera => _camera is { IsDestroyed: false } camera ? camera : null;
    public RenderTexture? Feed => Camera?.Feed;
    public World? CameraWorld => _cameraWorld;

    private PhotoCameraMode? _pushedMode;
    private readonly List<User> _users = new();
    private bool _pushQueued;
    private Action? _push;
    private bool _reload;
    private volatile bool _notify;
    private int _nudges;

    // THE STREAMING PROGRAM

    // This machine's one link, shared with the capture watch that puts the recording mark on the nametag.
    public ObsLink Obs => CaptureWatch.Obs;
    private bool _wasOut;
    private double _obsPoll;
    public string ObsAddress
    {
        get => Settings.ReadValue(ObsAddressKey, DefaultObsAddress) ?? DefaultObsAddress;
        set => Settings.WriteValue(ObsAddressKey, value ?? string.Empty);
    }
    public string ObsPassword
    {
        get => Settings.ReadValue(ObsPasswordKey, string.Empty) ?? string.Empty;
        set => Settings.WriteValue(ObsPasswordKey, value ?? string.Empty);
    }
    public bool ObsConnecting { get; private set; }

    // THE CHAT

    private TwitchChat? _chat;

    public override void OnStart()
    {
        base.OnStart();
        Local = this;
        Load();
        _push = Push;
        Settings.Changed += OnSettingChanged;
        Obs.StateChanged += OnObsChanged;
    }

    public override void OnDestroy()
    {
        Settings.Changed -= OnSettingChanged;
        Obs.StateChanged -= OnObsChanged;
        DesktopFeed.Hide(this);
        LiveStatus.IsLive = false;
        RemoveCameras();
        if (ReferenceEquals(Local, this))
            Local = null;
        base.OnDestroy();
    }

    private void OnSettingChanged(string key, bool fromAccount)
    {
        if (fromAccount && key.StartsWith(EngineSettings.CameraKeyPrefix, StringComparison.Ordinal))
            _reload = true;
    }

    private void OnObsChanged() => _notify = true;

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (_reload)
        {
            _reload = false;
            Load();
            _notify = true;
        }
        if (_notify)
        {
            _notify = false;
            StateChanged?.Invoke();
        }
        ApplyNudges(delta);
        if (_madeRoute is { } made)
        {
            _madeRoute = null;
            PlayRoute(made);
        }
        WatchObs(delta);

        var world = Engine.Current?.WorldManager?.FocusedWorld;
        if (!_active)
        {
            if (_spawned.Count > 0 || _camera != null)
                RemoveCameras();
            DropEars();
        }
        else if (world != null && !world.IsDestroyed && world != World && !_pushQueued)
        {
            _pushQueued = true;
            world.RunSynchronously(_push!);
        }

        var feed = _active ? Feed : null;
        if (_mirror && feed != null)
            DesktopFeed.Show(this, feed, Camera?.Slot);
        else
            DesktopFeed.Hide(this);
        LiveStatus.IsLive = _active && (_mirror || _showLive || Obs.Streaming || Obs.Recording);
    }

    // On the camera's world, once per frame of that world.
    private void Push()
    {
        _pushQueued = false;
        var world = Engine.Current?.WorldManager?.FocusedWorld;
        if (!_active || world == null || world.IsDestroyed || world.LocalUser == null)
            return;
        if (!ReferenceEquals(world, _cameraWorld))
        {
            _cameraWorld = world;
            _pushedMode = null;
        }
        var camera = PhotoCamera.MineIn(world);
        if (camera == null)
        {
            camera = SpawnIn(world);
            if (camera == null)
            {
                _camera = null;
                return;
            }
        }
        _camera = camera;
        _status = string.Empty;

        if (_pushedMode is { } pushed && camera.Mode.Value != pushed)
            _mode = camera.Mode.Value;
        var mode = _mode;
        if (!CameraRules.ModeAllowed(world, mode))
            mode = camera.Mode.Value;
        Put(camera.Mode, mode);
        _pushedMode = mode;

        _users.Clear();
        world.GetAllUsers(_users);
        var anchor = Anchor;
        var route = Route;
        bool routed = route != null && ReferenceEquals(route.World, world) && mode == PhotoCameraMode.Manual;
        Put(camera.Route, routed ? route : null);
        Put(camera.Anchor, !routed && anchor != null && ReferenceEquals(anchor.World, world) && mode == PhotoCameraMode.Manual ? anchor : null);
        if (_routeRestart && routed)
        {
            _routeRestart = false;
            camera.RestartRoute();
        }
        Put(camera.Subject, FindUser(_users, _subject));
        Put(camera.FieldOfView, _fieldOfView);
        Put(camera.Orbit, _orbit);
        Put(camera.Distance, _distance);
        Put(camera.Height, _height);
        Put(camera.Pitch, _pitch);
        Put(camera.Roll, _roll);
        Put(camera.EyeOffset, _eyeOffset);
        Put(camera.Framing, Framing);
        Put(camera.AimAhead, _aimAhead);
        Put(camera.PositionSpeed, _positionSpeed);
        Put(camera.AngleSpeed, _angleSpeed);
        Put(camera.FramingSpeed, _framingSpeed);
        Put(camera.AvoidOcclusion, _avoidOcclusion);
        Put(camera.KeepInWorld, _keepInWorld);
        Put(camera.Wobble, _wobble);
        Put(camera.GroupRadius, _groupRadius);
        Put(camera.GroupMargin, _groupMargin);
        Put(camera.HideBody, _hideBody);
        Put(camera.ShowFrustum, _showFrustum);
        Put(camera.EyesOnCamera, _eyesOnCamera);
        Put(camera.HideNameplates, _hideNameplates);
        Put(camera.HideLasers, _hideLasers);
        Put(camera.RenderForEveryone, _renderForEveryone);
        Put(camera.AnyoneCanOperate, _anyoneCanOperate);
        Put(camera.AnchorEase, _anchorEase);
        Put(camera.AnchorSpeed, _anchorSpeed);
        Put(camera.AnchorLinear, _anchorLinear);
        Match(_users, camera.Operators, Operators);
        Match(_users, camera.GroupInclude, GroupInclude);
        Match(_users, camera.GroupExclude, GroupExclude);
        camera.PrivateUIHere = _privateUI;
        Ears(world, camera);
    }

    private PhotoCamera? SpawnIn(World world)
    {
        if (_spawned.TryGetValue(world, out var known) && !known.IsDestroyed)
            return known;
        if (_fetching.Contains(world))
            return null;
        try
        {
            // The user's favourite camera arrives after its fetch, and until then there is none to push to.
            PhotoCamera? made = null;
            _fetching.Add(world);
            FavouriteSpawn.Camera(world, keep: false, camera =>
            {
                _fetching.Remove(world);
                if (camera == null)
                    return;
                if (!_active)
                {
                    camera.Slot.Destroy();
                    return;
                }
                _spawned[world] = camera;
                made = camera;
            });
            return made;
        }
        catch (UnauthorizedAccessException)
        {
            _fetching.Remove(world);
            _status = "This world does not let you add a camera";
            return null;
        }
    }

    private void RemoveCameras()
    {
        foreach (var (world, camera) in _spawned)
        {
            if (world.IsDestroyed)
                continue;
            var target = camera;
            world.RunSynchronously(() =>
            {
                if (!target.IsDestroyed)
                    target.Slot.Destroy();
            });
        }
        _spawned.Clear();
        _fetching.Clear();
        _camera = null;
        _cameraWorld = null;
        _pushedMode = null;
    }

    // THE CAMERA'S EARS
    //
    // With a stream device the camera's ears feed the stream and this machine keeps its own. Without one
    // the camera's ears replace this machine's: you hear what the camera hears, minus your own voice.

    private Slot? _earSlot;
    private AudioListener? _ear;
    private AudioListener? _suspended;

    private void Ears(World world, PhotoCamera camera)
    {
        bool want = AudioFromCamera;
        var root = world.LocalUser?.Root;
        var primary = root?.PrimaryListener;
        if (!want || root == null)
        {
            DropEars();
            return;
        }
        if (_ear == null || _ear.IsDestroyed || !ReferenceEquals(_earSlot?.Parent, camera.Slot))
        {
            DropEars();
            _earSlot = camera.Slot.AddLocalSlot("Ears");
            _earSlot.LocalPosition.Value = PhotoCamera.LensOffset;
            _ear = _earSlot.AttachComponent<AudioListener>();
        }
        bool stream = Engine.Current?.AudioSystem?.HasStreamOutput ?? false;
        if (_ear.Streaming.Value != stream)
            _ear.Streaming.Value = stream;
        var wearer = stream ? null : world.LocalUser;
        if (!ReferenceEquals(_ear.ActiveUser.Target, wearer))
            _ear.ActiveUser.Target = wearer!;
        var hearsMe = stream && _ownVoiceOnStream;
        if (root.VoiceOutput is { } voice && voice.IsLocallyExcluded(_ear) == hearsMe)
            voice.SetLocallyExcluded(_ear, !hearsMe);
        var suspend = stream ? null : primary;
        if (!ReferenceEquals(suspend, _suspended))
        {
            if (_suspended is { IsDestroyed: false })
                _suspended.SuspendedHere = false;
            _suspended = suspend;
        }
        if (_suspended != null)
            _suspended.SuspendedHere = true;
    }

    private void DropEars()
    {
        if (_suspended is { IsDestroyed: false })
            _suspended.SuspendedHere = false;
        _suspended = null;
        var slot = _earSlot;
        _earSlot = null;
        _ear = null;
        if (slot is { IsDestroyed: false })
            slot.World?.RunSynchronously(() =>
            {
                if (!slot.IsDestroyed)
                    slot.Destroy();
            });
    }

    // WHAT THE WIDGETS DO

    // A click on the picture: the subject goes there. Clicks near the edge are ignored.
    public void SetFraming(float x, float yFromTop)
    {
        if (x < 0.1f || x > 0.9f || yFromTop < 0.1f || yFromTop > 0.9f)
            return;
        if (_mode is not (PhotoCameraMode.ThirdPerson or PhotoCameraMode.Group or PhotoCameraMode.Stationary))
            return;
        _framingX = x;
        _framingY = yFromTop;
        Write("FramingX", x);
        Write("FramingY", yFromTop);
        StateChanged?.Invoke();
    }

    public enum Nudge { Closer, Farther, Up, Down, Left, Right }

    // Held buttons move the camera at a steady rate until let go.
    public void Hold(Nudge nudge, bool down)
    {
        int bit = 1 << (int)nudge;
        _nudges = down ? _nudges | bit : _nudges & ~bit;
    }

    private void ApplyNudges(float delta)
    {
        if (_nudges == 0)
            return;
        bool eyes = _mode == PhotoCameraMode.FirstPerson;
        float vertical = (Held(Nudge.Up) ? 1f : 0f) - (Held(Nudge.Down) ? 1f : 0f);
        float turn = (Held(Nudge.Right) ? 1f : 0f) - (Held(Nudge.Left) ? 1f : 0f);
        float away = (Held(Nudge.Farther) ? 1f : 0f) - (Held(Nudge.Closer) ? 1f : 0f);
        if (eyes)
        {
            if (vertical != 0f)
                Pitch = _pitch - vertical * 10f * delta;
            if (turn != 0f)
                Roll = _roll + turn * 10f * delta;
            return;
        }
        if (vertical != 0f)
            Height = _height + vertical * delta;
        if (turn != 0f)
            Orbit = _orbit + turn * 90f * delta;
        if (away != 0f)
            Distance = MathF.Max(0.2f, _distance + away * delta);
    }

    private bool Held(Nudge nudge) => (_nudges & (1 << (int)nudge)) != 0;

    // Framing back to how it starts.
    public void ResetFraming()
    {
        FieldOfView = 60f;
        Orbit = 0f;
        Distance = 1.5f;
        Height = 0f;
        Pitch = 5f;
        Roll = 0f;
        EyeOffset = 0.02f;
        _framingX = 0.5f;
        _framingY = 0.33f;
        Write("FramingX", _framingX);
        Write("FramingY", _framingY);
        StateChanged?.Invoke();
    }

    public void UseAnchor(CameraAnchor anchor)
    {
        if (anchor == null || anchor.IsDestroyed)
            return;
        var user = anchor.User;
        if (user != null && !user.IsMine)
            return;
        _active = true;
        _mode = PhotoCameraMode.Manual;
        _anchor = anchor;
        _route = null;
        StateChanged?.Invoke();
    }

    // Glides the camera through the route from its first stop; the same route again starts it over.
    public void PlayRoute(CameraRoute route)
    {
        if (route == null || route.IsDestroyed)
            return;
        _active = true;
        _mode = PhotoCameraMode.Manual;
        _anchor = null;
        _route = route;
        _routeRestart = true;
        StateChanged?.Invoke();
    }

    public void Release()
    {
        _anchor = null;
        _route = null;
        StateChanged?.Invoke();
    }

    // A route through every anchor in the focused world, in the order the anchors page lists them, timed at
    // a walking pace, and the camera set off along it.
    public void CreateRoute()
    {
        var world = _cameraWorld ?? Engine.Current?.WorldManager?.FocusedWorld;
        if (world == null || world.IsDestroyed || world == World)
            return;
        world.RunSynchronously(() =>
        {
            var stops = new List<CameraAnchor>();
            foreach (var anchor in CameraAnchor.InWorld(world))
            {
                if (anchor != null && !anchor.IsDestroyed)
                    stops.Add(anchor);
            }
            stops.Sort((a, b) => a.ReferenceID.CompareTo(b.ReferenceID));
            if (stops.Count < 2)
            {
                _status = CameraLocale.RouteNeedsAnchors.Resolve();
                _notify = true;
                return;
            }
            try
            {
                _madeRoute = CameraRoute.Spawn(world, stops, CameraRoute.SuggestedDuration(stops));
            }
            catch (UnauthorizedAccessException)
            {
                _status = CameraLocale.RouteRefused.Resolve();
                _notify = true;
            }
        });
    }

    // An anchor where the lens is now, at the width of view it has now.
    public void CreateAnchor()
    {
        var camera = Camera;
        var world = _cameraWorld;
        if (camera == null || world == null || world.IsDestroyed)
            return;
        world.RunSynchronously(() =>
        {
            if (camera.IsDestroyed)
                return;
            float scale = camera.Slot.GlobalScale.x;
            var rotation = camera.Slot.GlobalRotation;
            var lens = camera.Slot.GlobalPosition + rotation * (PhotoCamera.LensOffset * scale);
            try
            {
                CameraAnchor.Spawn(world, lens, rotation, camera.LiveFieldOfView);
            }
            catch (UnauthorizedAccessException)
            {
                _status = "This world does not let you add an anchor";
            }
        });
    }

    // From the panel: the photo comes out in front of whoever took it, through the timer when it is on.
    public void TakePhoto(PhotoShape shape)
    {
        var camera = Camera;
        var world = _cameraWorld;
        if (camera == null || world == null || world.IsDestroyed)
            return;
        bool timed = _timed;
        bool inWorld = _spawnPhotosInWorld;
        world.RunSynchronously(() =>
        {
            if (camera.IsDestroyed)
                return;
            if (timed)
                camera.StartCountdown(shape, EngineSettings.PhotoTimerSeconds);
            else
                camera.Capture(shape, inWorld, inFront: true);
        });
    }

    public void ToggleOperator(User user) => Toggle(Operators, user);

    public void SetGroup(User user, bool? include)
    {
        var key = KeyOf(user);
        GroupInclude.Remove(key);
        GroupExclude.Remove(key);
        if (include == true)
            GroupInclude.Add(key);
        else if (include == false)
            GroupExclude.Add(key);
        StateChanged?.Invoke();
    }

    public bool? GroupOf(User user)
    {
        var key = KeyOf(user);
        return GroupInclude.Contains(key) ? true : GroupExclude.Contains(key) ? false : null;
    }

    public bool IsSubject(User user) => _subject.Length == 0 ? ReferenceEquals(user, user.World?.LocalUser) : _subject == KeyOf(user);

    public void Frame(User user) => Subject = ReferenceEquals(user, user.World?.LocalUser) ? string.Empty : KeyOf(user);

    private void Toggle(HashSet<string> set, User user)
    {
        var key = KeyOf(user);
        if (!set.Remove(key))
            set.Add(key);
        StateChanged?.Invoke();
    }

    public static string KeyOf(User user)
        => !string.IsNullOrEmpty(user.UserID.Value) ? user.UserID.Value : "machine:" + (user.MachineID.Value ?? string.Empty);

    private static User? FindUser(List<User> users, string key)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        foreach (var user in users)
        {
            if (user != null && !user.IsDestroyed && KeyOf(user) == key)
                return user;
        }
        return null;
    }

    private static void Match(List<User> users, SyncRefList<User> list, HashSet<string> keys)
    {
        int wanted = 0;
        foreach (var user in users)
        {
            if (user != null && !user.IsDestroyed && keys.Contains(KeyOf(user)))
                wanted++;
        }
        bool same = list.Count == wanted;
        if (same)
        {
            foreach (var user in list)
            {
                if (user == null || !keys.Contains(KeyOf(user)))
                {
                    same = false;
                    break;
                }
            }
        }
        if (same)
            return;
        list.Clear();
        foreach (var user in users)
        {
            if (user != null && !user.IsDestroyed && keys.Contains(KeyOf(user)))
                list.Add(user);
        }
    }

    // THE STREAMING PROGRAM

    public async Task<bool> ConnectObsAsync(string address, string password)
    {
        ObsAddress = address;
        ObsPassword = password;
        if (!TryAddress(address, out var host, out int port))
            return false;
        ObsConnecting = true;
        StateChanged?.Invoke();
        try
        {
            return await Obs.ConnectAsync(host, port, string.IsNullOrEmpty(password) ? null : password).ConfigureAwait(false);
        }
        finally
        {
            ObsConnecting = false;
        }
    }

    public static bool TryAddress(string address, out string host, out int port)
    {
        host = "127.0.0.1";
        port = 4455;
        var text = (address ?? string.Empty).Trim();
        if (text.Length == 0)
            return true;
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "ws://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return false;
        host = uri.Host;
        port = uri.IsDefaultPort || uri.Port <= 0 ? 4455 : uri.Port;
        return true;
    }

    // Statuses are polled each second while linked; going out (streaming or recording) can put the camera
    // on the desktop window.
    private void WatchObs(float delta)
    {
        if (!Obs.IsConnected)
        {
            _wasOut = false;
            return;
        }
        _obsPoll += delta;
        if (_obsPoll >= 1.0)
        {
            _obsPoll = 0;
            _ = Obs.RefreshStatusAsync();
        }
        bool isOut = Obs.Streaming || Obs.Recording;
        if (isOut && !_wasOut && _autoMirror)
        {
            _active = true;
            _mirror = true;
            StateChanged?.Invoke();
        }
        _wasOut = isOut;
    }

    // THE CHAT
    //
    // One chat reader for this machine, in userspace, reading the channel kept above.

    public TwitchChat? Chat
    {
        get
        {
            if (_chatChannel.Length == 0)
                return null;
            if (_chat == null || _chat.IsDestroyed)
            {
                _chat = Slot.AddSlot("Camera Chat").AttachComponent<TwitchChat>();
            }
            if (_chat.Channel.Value != _chatChannel)
                _chat.Channel.Value = _chatChannel;
            return _chat;
        }
    }

    // SETTINGS

    private void Load()
    {
        _fieldOfView = Read("FieldOfView", 60f);
        _orbit = Read("Orbit", 0f);
        _distance = Read("Distance", 1.5f);
        _height = Read("Height", 0f);
        _pitch = Read("Pitch", 5f);
        _roll = Read("Roll", 0f);
        _eyeOffset = Read("EyeOffset", 0.02f);
        _framingX = Read("FramingX", 0.5f);
        _framingY = Read("FramingY", 0.33f);
        _aimAhead = Read("AimAhead", false);
        _avoidOcclusion = Read("AvoidOcclusion", true);
        _keepInWorld = Read("KeepInWorld", false);
        _wobble = Read("Wobble", false);
        _hideNameplates = Read("HideNameplates", false);
        _hideLasers = Read("HideLasers", false);
        _eyesOnCamera = Read("EyesOnCamera", false);
        _groupRadius = Read("GroupRadius", 2.5f);
        _groupMargin = Read("GroupMargin", 0.5f);
        _positionSpeed = Read("PositionSpeed", 2f);
        _angleSpeed = Read("AngleSpeed", 0.25f);
        _framingSpeed = Read("FramingSpeed", 5f);
        _anchorEase = Read("AnchorEase", true);
        _anchorSpeed = Read("AnchorSpeed", 2.5f);
        _anchorLinear = Read("AnchorLinear", false);
        _spawnPhotosInWorld = Read("SpawnPhotosInWorld", true);
        _ownVoiceOnStream = Read("OwnVoiceOnStream", true);
        _autoMirror = Read("AutoMirror", false);
        _chatChannel = Read("ChatChannel", string.Empty) ?? string.Empty;
    }

    private static T Read<T>(string name, T fallback) => Settings.ReadValue(EngineSettings.CameraKeyPrefix + name, fallback);

    private static void Write<T>(string name, T value) => Settings.WriteValue(EngineSettings.CameraKeyPrefix + name, value);

    private void Keep<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        Write(name, value);
        StateChanged?.Invoke();
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        StateChanged?.Invoke();
    }

    private static void Put<T>(Sync<T> field, T value)
    {
        if (!EqualityComparer<T>.Default.Equals(field.Value, value))
            field.Value = value;
    }

    private static void Put<T>(SyncRef<T> field, T? value) where T : class, IWorldElement
    {
        if (!ReferenceEquals(field.Target, value))
            field.Target = value!;
    }

    private static float Wrap(float degrees)
    {
        degrees %= 360f;
        if (degrees > 180f)
            degrees -= 360f;
        else if (degrees < -180f)
            degrees += 360f;
        return degrees;
    }
}
