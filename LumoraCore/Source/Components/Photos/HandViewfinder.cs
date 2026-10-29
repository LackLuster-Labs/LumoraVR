// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Photos;

namespace Lumora.Core.Components;

// A camera made with one hand. An L with the left hand, index up and thumb out to the side, the other fingers
// curled and nothing held, opens a viewfinder in the corner the L makes: what shows in it is what the photo
// takes, from the eye through the frame, wider the closer the hand. While it is open the right hand works it
// and nothing else: its trigger takes the photo, its other button starts a timed one, which leaves the frame
// where it is and counts down so its taker can walk into the picture. Everyone in the world sees the frame and
// what is in it, from either side, unless photos are kept out of the world; the photo lands in the world in the
// frame's place. On a screen there are no hands: the photo key held shows the frame in front of the view and
// letting go takes what the screen shows, the timed key starts the count instead. On the user's root, working
// only in the world in focus; does nothing on anyone else's machine. -xlinka
[ComponentCategory("Users")]
public class HandViewfinder : Component
{
    private const float ArmRate = 4f;
    private const float DisarmBelow = 0.4f;
    private const int PreviewWidth = 640;
    private const int PreviewHeight = 360;
    private const float Aspect = 16f / 9f;
    // A lens this close to the eye films the way the eye sees. Any other camera sees the wearer's own head there
    // from the inside, because that head is hidden from the eyes alone and shows to mirrors and cameras: a photo
    // from the eye came out as a mirror's view of your own skull. -xlinka
    private const float AtTheEye = 0.3f;
    // Everyone's copy of the preview films from the photographer's eye too, and starts past that head. -xlinka
    private const float PreviewNearAtTheEye = 0.15f;
    // The far side's picture and line sit behind the rim from the eye's side. -xlinka
    private const float BackOffset = -0.002f;
    private const double AskPatience = 0.3;
    private const double AskOverlap = 1.0;
    private const double AskStale = 2.0;

    // A screen's frame films from the eye at the screen's own width of view; a hand's from the frame itself,
    // as wide as the frame looks from the eye.
    private readonly record struct Frame(float3 Eye, float3 Center, float3 Up, float3 Side, float Width, float Height, float ScreenFov = 0f)
    {
        public float3 Looking => (Center - Eye).Normalized;
        public float FieldOfView => ScreenFov > 0f ? ScreenFov
            : 2f * MathF.Atan(Height * 0.5f / MathF.Max((Center - Eye).Length, 0.01f)) * (180f / MathF.PI);
        public floatQ CameraRotation => PhotoShot.Looking(Looking, Up);
        public floatQ FacingEye => PhotoShot.Facing(-Looking, Up);
        public float3 CameraPosition => ScreenFov > 0f ? Eye : Center + Looking * 0.02f;
    }

    // A photo asked for from outside the photo keys, the platform's own screenshot key. The viewfinder in the
    // world in focus takes it from the screen, unless the photo keys were at work round the same moment: the
    // platform's key is often the photo key too, and that press makes its own photo. -xlinka
    private static long _askedAt;

    public static void AskForPhoto() => Interlocked.Exchange(ref _askedAt, Stopwatch.GetTimestamp());

    private float _charge;
    private bool _armed;
    private bool _screenTimed;
    private bool _claimed;
    private bool _busy;
    private double _timerEnds = -1;
    private Frame _held;
    private double _flashUntil;
    private long _keysAt;

    private Slot? _view;
    private Slot? _picture;
    private QuadMesh? _pictureQuad;
    private QuadMesh? _rimQuad;
    private UnlitMaterial? _rim;
    private TextRenderer? _hint;
    private TextRenderer? _hintBack;
    private Camera? _camera;
    private Slot? _cameraSlot;
    private RenderTextureProvider? _feed;
    private bool _builtShared;
    private bool _shareRefused;

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        var world = World;
        var root = Slot?.ActiveUserRoot;
        var input = Engine.Current?.InputInterface;
        // Every world the user is in has one of these and every one hears the keys. Only the world on screen takes
        // the photo: one in the background would film the world in view from wherever its own body stands. -xlinka
        if (world == null || root == null || input == null || !ReferenceEquals(root.ActiveUser, world.LocalUser) || !world.IsFocused)
        {
            Close(input);
            return;
        }
        double now = world.Time.TotalTime;
        Asked(input, root);

        if (_timerEnds >= 0)
        {
            Counting(now, root);
            return;
        }
        if (!input.IsVRActive)
        {
            Claim(input, false);
            if (!_busy)
                Screen(input, root, now);
            return;
        }
        if (!EngineSettings.HandFrame || _busy)
        {
            if (!_busy)
                Close(input);
            return;
        }

        bool shaped = ReadL(input, root, out var frame);
        _charge = shaped ? MathF.Min(1f, _charge + delta * ArmRate) : MathF.Max(0f, _charge - delta * ArmRate);
        if (!_armed && _charge >= 1f)
            _armed = true;
        else if (_armed && _charge < DisarmBelow)
            _armed = false;
        Claim(input, _armed);
        if (_charge <= 0f || !shaped && !_armed)
        {
            Hide();
            return;
        }
        if (shaped)
            _held = frame;
        Show(_held, _armed ? Ready(now) : Arming, _armed ? "Trigger: photo    B: timer" : string.Empty, string.Empty, _charge);

        if (!_armed)
            return;
        var right = input.Actions?.Interaction(Chirality.Right);
        if (right == null)
            return;
        if (right.Primary.Pressed)
            Take(_held, "Hand viewfinder");
        else if (right.Secondary.Pressed)
            StartTimer(now);
    }

    // The photo keys: held, the frame shows in front of the view; let go, the photo or the count.
    private void Screen(InputInterface input, UserRoot root, double now)
    {
        var keys = input.Actions?.Camera;
        if (keys == null)
            return;
        bool framing = keys.TakePhoto.Held || keys.TimedPhoto.Held;
        if (framing && ScreenFrame(input, root) is { } frame)
        {
            // A timed chord let go of its modifier first leaves the plain key down: once seen, the frame is a timed one. -xlinka
            _screenTimed |= keys.TimedPhoto.Held;
            _held = frame;
            Show(frame, ReadyColor, _screenTimed ? "Let go to start the timer" : "Let go to take the photo", string.Empty, 1f);
            return;
        }
        bool timed = _screenTimed;
        _screenTimed = false;
        if ((keys.TakePhoto.Released || keys.TimedPhoto.Released) && _view is { ActiveSelf.Value: true })
        {
            if (timed)
            {
                StartTimer(now);
                return;
            }
            Take(_held, "Screen");
        }
        Hide();
    }

    private void Asked(InputInterface input, UserRoot root)
    {
        long stamp = Stopwatch.GetTimestamp();
        if (input.Actions?.Camera is { } keys
            && (keys.TakePhoto.Held || keys.TakePhoto.Released || keys.TimedPhoto.Held || keys.TimedPhoto.Released))
            _keysAt = stamp;
        long asked = Interlocked.Read(ref _askedAt);
        if (asked == 0)
            return;
        double since = (stamp - asked) / (double)Stopwatch.Frequency;
        bool keysToo = _keysAt != 0 && System.Math.Abs(asked - _keysAt) / (double)Stopwatch.Frequency < AskOverlap;
        if (!keysToo && since >= 0 && since < AskPatience)
            return;
        Interlocked.CompareExchange(ref _askedAt, 0, asked);
        if (keysToo || since > AskStale || _busy || _timerEnds >= 0 || ScreenFrame(input, root) is not { } frame)
            return;
        Take(frame, "Screen");
    }

    // Twenty centimetres in front of what the screen shows, a third of its height. In a headset, the head.
    private static Frame? ScreenFrame(InputInterface input, UserRoot root)
    {
        bool screen = !input.IsVRActive;
        float fov = screen && input.DesktopCameraFovY > 1f ? input.DesktopCameraFovY : EngineSettings.FieldOfView;
        float3 eye;
        floatQ rotation;
        if (screen && input.DesktopCameraHasOverride && input.DesktopCameraPoseValid)
        {
            eye = input.DesktopCameraPosition;
            rotation = input.DesktopCameraRotation;
        }
        else if (root.HeadSlot is { IsDestroyed: false } head)
        {
            eye = head.GlobalPosition;
            rotation = head.GlobalRotation;
        }
        else
            return null;
        const float away = 0.2f;
        float height = 2f * away * MathF.Tan(fov * 0.5f * MathF.PI / 180f) * 0.35f;
        var looking = rotation * float3.Backward;
        var up = rotation * float3.Up;
        return new Frame(eye, eye + looking * away, up, rotation * float3.Right, height * Aspect, height, fov);
    }

    private static readonly color Arming = new(0.95f, 0.25f, 0.2f, 1f);
    private static readonly color ReadyColor = new(0.2f, 0.85f, 1f, 1f);

    private color Ready(double now) => now < _flashUntil ? color.White : ReadyColor;

    // The L: index straight and up, thumb straight and out across it, the rest curled, the hand empty.
    private bool ReadL(InputInterface input, UserRoot root, out Frame frame)
    {
        frame = default;
        var hand = input.LeftHand;
        if (hand == null || !hand.IsTracking || HoldingLeft(root))
            return false;
        var knuckle = hand.Index.Proximal.Tracked.Position;
        var tip = hand.Index.Tip.Tracked.Position;
        var along = tip - knuckle;
        float reach = along.Length;
        if (reach < 0.02f || reach < Bones(hand.Index) * 0.85f)
            return false;
        var index = along / reach;

        var thumbBase = hand.Thumb.Proximal.Tracked.Position;
        var thumbAlong = hand.Thumb.Tip.Tracked.Position - thumbBase;
        float thumbReach = thumbAlong.Length;
        if (thumbReach < 0.015f || thumbReach < Bones(hand.Thumb) * 0.75f)
            return false;
        var thumb = thumbAlong / thumbReach;
        float across = float3.Dot(thumb, index);
        if (across > 0.6f || across < -0.6f)
            return false;

        if (!Curled(hand.Middle) || !Curled(hand.Ring) || !Curled(hand.Pinky))
            return false;

        var up = root.Slot.GlobalRotation * float3.Up;
        if (float3.Dot(index, up) < 0.75f)
            return false;

        var side = (thumb - index * float3.Dot(thumb, index)).Normalized;
        float height = reach * 1.6f;
        float width = height * Aspect;
        var center = knuckle + side * (width * 0.5f) + index * (height * 0.5f);
        frame = new Frame(root.HeadPosition, center, index, side, width, height);
        return true;
    }

    private static float Bones(HandFinger finger)
    {
        float sum = 0f;
        HandSegment? previous = null;
        foreach (var segment in finger)
        {
            if (segment.SegmentType == FingerSegmentType.Metacarpal)
                continue;
            if (previous != null)
                sum += (segment.Tracked.Position - previous.Tracked.Position).Length;
            previous = segment;
        }
        return sum;
    }

    // Its tip pulled back toward its knuckle.
    private static bool Curled(HandFinger finger)
        => (finger.Tip.Tracked.Position - finger.Proximal.Tracked.Position).Length < Bones(finger) * 0.6f;

    private static bool HoldingLeft(UserRoot root)
    {
        foreach (var tool in root.Slot.GetComponentsInChildren<HandTool>())
        {
            if (tool.Side.Value == Chirality.Left && tool.IsHoldingObjects)
                return true;
        }
        return false;
    }

    private void Claim(InputInterface? input, bool claim)
    {
        if (input == null || claim == _claimed)
            return;
        _claimed = claim;
        if (claim)
            input.ClaimHand(Chirality.Right, this);
        else
            input.ReleaseHand(Chirality.Right, this);
    }

    private void StartTimer(double now)
    {
        _timerEnds = now + EngineSettings.PhotoTimerSeconds;
        _armed = false;
        _charge = 0f;
        Claim(Engine.Current?.InputInterface, false);
    }

    // The frame stays put while it counts down, blinking faster at the end, then takes the photo.
    private void Counting(double now, UserRoot root)
    {
        double left = _timerEnds - now;
        if (left <= 0)
        {
            _timerEnds = -1;
            Take(_held, "Hand viewfinder, timed");
            return;
        }
        bool fast = left < 2.0;
        bool lit = (int)(left * (fast ? 6 : 2)) % 2 == 0;
        string count = $"{System.Math.Ceiling(left):0}";
        Show(_held, lit ? ReadyColor : new color(1f, 0.6f, 0.1f, 1f), count, count, 1f);
    }

    // Others see the frame and its picture too, unless photos are kept out of the world. -xlinka
    private bool Shares => !_shareRefused && (CameraDirector.Local?.SpawnPhotosInWorld ?? true);

    private bool AtEye(float3 position)
        => Slot?.ActiveUserRoot is { } root && (position - root.HeadPosition).Length < AtTheEye * MathF.Max(root.GlobalScale, 0.01f);

    private void Take(Frame frame, string camera)
    {
        var world = World;
        var user = world?.LocalUser;
        if (world == null || _busy)
            return;
        _busy = true;
        _flashUntil = world.Time.TotalTime + 0.15;
        var size = EngineSettings.PhotoSizes[System.Math.Clamp(EngineSettings.PhotoSize, 0, EngineSettings.PhotoSizes.Length - 1)];
        var encoding = EngineSettings.PhotoEncoding;
        var view = _view;
        bool wearer = AtEye(frame.CameraPosition);
        bool inWorld = Shares;
        StartTask(async () =>
        {
            try
            {
                var leaveOut = view != null ? new List<Slot> { view } : null;
                var frames = await PhotoShot.TakeRawAsync(world, new[] { (frame.CameraPosition, frame.CameraRotation) },
                    new PhotoLens(frame.FieldOfView, size.Width, size.Height) { LeaveOut = leaveOut, WearerView = wearer });
                if (frames?[0] is { } picture)
                {
                    var (image, written) = PhotoEncoder.Encode(picture.Rgba, picture.Width, picture.Height, encoding, PhotoShot.Quality(encoding));
                    if (image != null)
                        await PhotoPrint.MakeAsync(world, image, written, frame.Center, frame.FacingEye, frame.Width,
                            new PhotoFacts(user, camera, frame.CameraPosition, frame.CameraRotation, frame.FieldOfView), PhotoShape.Flat, inWorld);
                }
                else
                    Logging.Logger.Warn("Photos: the camera gave back no picture");
            }
            finally
            {
                await WorldContext.ToWorld();
                _busy = false;
            }
        });
    }

    // The preview in the frame, a rim behind it in the state's colour, a line of help under it; the far side's
    // line is what the others need, the count.
    private void Show(Frame frame, color rimColor, string hint, string othersHint, float grow)
    {
        Build();
        if (_view == null || _picture == null || _pictureQuad == null || _rimQuad == null || _rim == null || _camera == null)
            return;
        float scale = 0.6f + 0.4f * grow;
        _view.GlobalPosition = frame.Center;
        _view.GlobalRotation = frame.FacingEye;
        _view.GlobalScale = float3.One * scale;
        _pictureQuad.Size.Value = new float2(frame.Width, frame.Height);
        _rimQuad.Size.Value = new float2(frame.Width * 1.04f, frame.Height * 1.06f);
        _rim.TintColor.Value = new colorHDR(rimColor.r, rimColor.g, rimColor.b, 1f);
        Line(_hint, hint, frame.Height, 0f);
        Line(_hintBack, othersHint, frame.Height, BackOffset);
        var cameraSlot = _camera.Slot;
        cameraSlot.GlobalPosition = frame.CameraPosition;
        cameraSlot.GlobalRotation = frame.CameraRotation;
        _camera.FieldOfView.Value = frame.FieldOfView;
        _camera.NearClip.Value = frame.ScreenFov > 0f
            ? PreviewNearAtTheEye * MathF.Max(Slot?.ActiveUserRoot?.GlobalScale ?? 1f, 0.01f)
            : 0.02f;
        if (!_view.ActiveSelf.Value)
            _view.ActiveSelf.Value = true;
        if (!cameraSlot.ActiveSelf.Value)
            cameraSlot.ActiveSelf.Value = true;
        Filming(true);
    }

    private static void Line(TextRenderer? line, string text, float frameHeight, float depth)
    {
        if (line == null || line.IsDestroyed)
            return;
        line.Text.Value = text;
        line.Slot.LocalPosition.Value = new float3(0f, -frameHeight * 0.62f, depth);
        line.Size.Value = frameHeight * 0.1f;
    }

    // The feed draws only while the frame shows; a switched-off slot alone does not stop it.
    private void Filming(bool on)
    {
        if (_feed?.Asset is RenderTexture texture && texture.RenderEnabled != on)
            texture.SetRenderEnabled(on);
    }

    private void Build()
    {
        bool share = Shares;
        if (World == null || _view is { IsDestroyed: false } && share == _builtShared)
            return;
        DestroyParts();
        try
        {
            BuildParts(share);
        }
        catch (UnauthorizedAccessException)
        {
            // Nothing may go up under this body here, so the frame stays its owner's alone. -xlinka
            DestroyParts();
            _shareRefused = true;
            BuildParts(false);
        }
    }

    // Shared, the parts ride under the user's own root: everyone gets them and they go when the user does. -xlinka
    private void BuildParts(bool share)
    {
        _builtShared = share;
        var holder = share ? Slot : World.RootSlot;
        _view = Part(holder, "Viewfinder", share);
        // The frame is interface: every eye sees it, cameras leave it out unless told to film it.
        _view.AttachComponent<RenderLayerOverride>().Layer.Value = RenderLayerOverride.PrivateLayer;

        var rimSlot = Part(_view, "Rim", share);
        rimSlot.LocalPosition.Value = new float3(0f, 0f, -0.001f);
        _rimQuad = rimSlot.AttachComponent<QuadMesh>();
        var rimRenderer = rimSlot.AttachComponent<MeshRenderer>();
        rimRenderer.Mesh.Target = _rimQuad;
        _rim = rimSlot.AttachComponent<UnlitMaterial>();
        _rim.Culling.Value = Culling.None;
        rimRenderer.Material.Target = _rim;

        var cameraSlot = Part(holder, "Viewfinder Camera", share);
        _cameraSlot = cameraSlot;
        var feed = cameraSlot.AttachComponent<RenderTextureProvider>();
        _feed = feed;
        feed.Size.Value = new int2(PreviewWidth, PreviewHeight);
        _camera = cameraSlot.AttachComponent<Camera>();
        _camera.NearClip.Value = 0.02f;
        _camera.FarClip.Value = 2000f;
        _camera.TargetTexture.Target = feed;
        _camera.ExcludeRender.Add(_view);

        // One picture each side, both facing out, so the far side reads the right way round instead of mirrored. -xlinka
        _picture = Part(_view, "Picture", share);
        _pictureQuad = _picture.AttachComponent<QuadMesh>();
        var renderer = _picture.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = _pictureQuad;
        var material = _picture.AttachComponent<UnlitMaterial>();
        material.Texture.Target = feed;
        material.Culling.Value = Culling.Back;
        renderer.Material.Target = material;
        var back = Part(_view, "Picture Back", share);
        back.LocalPosition.Value = new float3(0f, 0f, BackOffset);
        back.LocalRotation.Value = floatQ.AxisAngleRad(float3.Up, MathF.PI);
        var backRenderer = back.AttachComponent<MeshRenderer>();
        backRenderer.Mesh.Target = _pictureQuad;
        backRenderer.Material.Target = material;

        // Text with no font draws nothing.
        var fontSlot = Part(_view, "Font", share);
        var font = fontSlot.AttachComponent<FontProvider>();
        font.URL.Value = Lumora.Core.Components.UI.DashTheme.FontRegular;
        var oneSided = fontSlot.AttachComponent<TextMaterial>();
        oneSided.Culling.Value = Culling.Back;
        _hint = Label(Part(_view, "Hint", share), font, oneSided);
        var backHint = Part(_view, "Hint Back", share);
        backHint.LocalRotation.Value = floatQ.AxisAngleRad(float3.Up, MathF.PI);
        _hintBack = Label(backHint, font, oneSided);
    }

    private static Slot Part(Slot parent, string name, bool share)
    {
        if (!share)
            return parent.AddLocalSlot(name);
        var slot = parent.AddSlot(name);
        slot.Persistent.Value = false;
        return slot;
    }

    private static TextRenderer Label(Slot slot, FontProvider font, TextMaterial style)
    {
        var text = slot.AttachComponent<TextRenderer>();
        text.Font.Target = font;
        text.Style.Target = style;
        text.Color.Value = color.White;
        text.HorizontalAlign.Value = TextHorizontalAlignment.Center;
        text.VerticalAlign.Value = TextVerticalAlignment.Middle;
        return text;
    }

    private void Hide()
    {
        Filming(false);
        if (_view is { IsDestroyed: false } && _view.ActiveSelf.Value)
            _view.ActiveSelf.Value = false;
        if (_camera is { IsDestroyed: false } && _camera.Slot.ActiveSelf.Value)
            _camera.Slot.ActiveSelf.Value = false;
    }

    private void Close(InputInterface? input)
    {
        _charge = 0f;
        _armed = false;
        _screenTimed = false;
        _timerEnds = -1;
        Claim(input, false);
        Hide();
    }

    private void DestroyParts()
    {
        if (_cameraSlot is { IsDestroyed: false })
            _cameraSlot.Destroy();
        if (_view is { IsDestroyed: false })
            _view.Destroy();
        _view = null;
        _picture = null;
        _pictureQuad = null;
        _rimQuad = null;
        _rim = null;
        _hint = null;
        _hintBack = null;
        _camera = null;
        _cameraSlot = null;
        _feed = null;
    }

    public override void OnDestroy()
    {
        Claim(Engine.Current?.InputInterface, false);
        DestroyParts();
        base.OnDestroy();
    }
}
