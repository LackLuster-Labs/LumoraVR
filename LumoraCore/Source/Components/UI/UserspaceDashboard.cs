// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Import;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.Touch;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

[ComponentCategory("Hidden")]
[UserspaceOnly]
public class UserspaceDashboard : UIComponent
{
    // The canvas the dash is laid out for. A window wider than 16:9 gets more width; a narrower one keeps
    // the full design width and gets more HEIGHT instead. Narrowing the canvas to the window ran the tab
    // bar, the top bar and the widget cards into each other on a near-square phone (768 wide on a Fold).
    private const int DesignWidth = 1280;
    private const int DesignHeight = 720;
    private const int MaxCaptureSide = 2560;
    // Render the offscreen texture at this multiple of the logical canvas size, then
    // downsample on display. 2x means text/edges land between display pixels with proper
    // detail to filter from instead of stretching 1:1+ from a 1280x720 buffer. - xlinka
    // 1 on a standalone headset. A ViewportTexture has no mipmaps, so every pixel of minification is
    // aliasing, and a 2560x1440 capture shown at ~1100 eye-buffer pixels tall on a Pico was being
    // sampled at more than 2:1: text turned to noise. 1280x720 there is close to 1:1 with the panel's
    // footprint. -xlinka
    private static int SupersampleScale => Engine.IsStandaloneHeadset ? 1 : 2;
    private const float CanvasScale = 0.001f;
    private const float CaptureDistance = 1f;

    // Reserved high render band for the dash display surface. Draw order is SortingOffset = material
    // RenderQueue + renderer SortingOrder (uncapped). World UI/lasers/context menus sit at ~4000-10000, so
    // 20000 keeps the dash above all of them; the local user's lasers and hands lift into the bands above
    // it while it is open (UserspaceRenderBands) so the pointer and the hand holding it stay visible on the
    // dash. -xlinka
    private const int DashSurfaceSortingOrder = UserspaceRenderBands.DashSurfaceSortingOrder;
    private static readonly float3 RigWorldPosition = new float3(0f, 1000f, 0f);

    private int _captureWidth = DesignWidth;
    private int _captureHeight = DesignHeight;

    public readonly Sync<bool> IsOpen;
    public readonly Sync<float> Distance;
    public readonly Sync<float> VerticalOffset;
    public readonly Sync<float> DisplayHeight;
    public readonly Sync<bool> FollowViewWhileOpen;
    // Freeform: in VR, stop snapping the panel in front of the view every frame.
    // Instead it stays where it was last placed (snapped in front once when you
    // open it or flip freeform on) and a grab handle lets you reposition it.
    // Desktop is always window-projected, so freeform only affects VR.
    public readonly Sync<bool> Freeform;
    public readonly AssetRef<FontSet> Font;
    public readonly AssetRef<FontSet> FontSemibold;
    public readonly AssetRef<FontSet> FontBold;
    public readonly AssetRef<FontSet> FontMono;

    private Slot? _renderRig;
    private Slot? _canvasSlot;
    private Slot? _surfaceSlot;
    private Dashboard? _dashboard;
    private RenderTextureProvider? _renderTexture;
    private CurvedPlaneMesh? _displayMesh;
    private CustomShaderMaterial? _displayMaterial;
    private MeshRenderer? _displayRenderer;
    private bool _externalDisplay;
    private FontProvider? _fontProvider;
    private FontProvider? _fontProviderSemibold;
    private FontProvider? _fontProviderBold;
    private FontProvider? _fontProviderMono;
    private Grabbable? _grabHandle;
    private bool _lastFreeform;

    // Lazy follow. The anchor is the head position with a yaw-only rotation; it stays put while the
    // wearer looks around and only starts gliding toward the head once they have walked
    // ActivationDistance or turned ActivationAngle away, then settles once within the deactivation
    // thresholds. Pitch is never followed. Pinning the panel to the full head pose every frame (the old
    // behaviour) read as jitter in a headset: the engine's head sample is a frame behind the pose the eye
    // actually renders with, so a head-locked panel swims against the world by exactly that lag. -xlinka
    private const float FollowPositionSpeed = 3f;
    private const float FollowRotationSpeed = 3f;
    private const float FollowActivationDistance = 0.75f;
    private const float FollowActivationAngle = 140f;
    private const float FollowDeactivationDistance = 0.1f;
    private const float FollowDeactivationAngle = 10f;
    private float3 _anchorPosition;
    private floatQ _anchorRotation = floatQ.Identity;
    private float3 _anchorTargetPosition;
    private floatQ _anchorTargetRotation = floatQ.Identity;
    private bool _followActivated;
    private bool _anchorValid;
    private bool _wasOpen;
    // The frame the headset's origin was drawn from at the last placement, so a step to another one shows.
    private World? _originWorld;
    private UserRoot? _originRoot;
    private Slot? _originSlot;
    private float3 _originPosition;
    private floatQ _originRotation = floatQ.Identity;
    private bool _originSeen;
    // The rig ray for a pointer with no reach behind it, a fingertip: nothing measures an angle for one.
    private const float DefaultRigReach = 0.5f;
    private const float MinRigReach = 0.01f;
    private const double SlowOpenMs = 50.0;
    private double _openMs;
    // Last seen shared-font atlas generation. New glyphs rasterize on demand and bump this; we re-render the
    // viewport when it changes so freshly-seen text isn't left blank under render-on-dirty. -xlinka
    private int _lastFontGeneration = -1;
    private bool _built;
    private bool _defaultScreensBuilt;

    public Dashboard? Dashboard => _dashboard;

    public RenderTextureProvider? RenderTextureSource => _renderTexture;

    // lasers are restricted to this slot while the dash is open on desktop
    public Slot? SurfaceSlot => _surfaceSlot != null && !_surfaceSlot.IsDestroyed ? _surfaceSlot : null;

    // null until built
    public static UserspaceDashboard? LocalInstance { get; private set; }

    public UserspaceDashboard()
    {
        IsOpen = new Sync<bool>(this, false);
        // Closer and taller than before (was 1.2 m / 0.85 m). The panel's angular size is what makes
        // text readable in a headset, and this puts it at ~60 degrees tall instead of ~40. Desktop
        // ignores both (it fits the window). -xlinka
        Distance = new Sync<float>(this, 0.85f);
        VerticalOffset = new Sync<float>(this, 0.05f);
        DisplayHeight = new Sync<float>(this, 1.0f);
        FollowViewWhileOpen = new Sync<bool>(this, true);
        Freeform = new Sync<bool>(this, false);
        Font = new AssetRef<FontSet>(this);
        FontSemibold = new AssetRef<FontSet>(this);
        FontBold = new AssetRef<FontSet>(this);
        FontMono = new AssetRef<FontSet>(this);
    }

    public override void OnStart()
    {
        base.OnStart();
        LocalInstance = this;
        // Shutting the dash switches its slot off, and the camera has to keep working with the dash shut (moving to
        // the focused world, the window feed, the streaming program), so the director gets a slot of its own. -xlinka
        var directorSlot = World?.RootSlot?.FindChildOrAdd("Camera Director") ?? Slot;
        if (directorSlot.GetComponent<CameraDirector>() == null)
            directorSlot.AttachComponent<CameraDirector>();
        if (directorSlot.GetComponent<SocialNotifications>() == null)
            directorSlot.AttachComponent<SocialNotifications>();
        EnsureBuilt();
        ApplyOpenState();
        ScheduleRigParkAfterWarmup();
    }

    // Parking the rig before the canvas has ever built means the first open shows
    // the UI assembling piece by piece (layout, then glyphs as the atlas rasterizes).
    // Let it warm up once at startup, then park while closed.
    private bool _rigWarmed;

    private void ScheduleRigParkAfterWarmup()
    {
        void Check()
        {
            if (IsDestroyed) return;
            var canvas = _canvasSlot?.GetComponent<Canvas>();
            if (canvas?.RootChunk != null)
            {
                // One more breath so glyph-atlas re-renders land before parking.
                World?.RunInUpdates(120, () =>
                {
                    if (IsDestroyed) return;
                    _rigWarmed = true;
                    ApplyOpenState();
                });
            }
            else
            {
                World?.RunInUpdates(30, Check);
            }
        }
        World?.RunInUpdates(30, Check);
    }

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        EnsureBuilt();
        ApplyOpenState();

        var input = Engine.Current?.InputInterface;
        if (input != null)
            input.IsDashboardOpen = IsOpen.Value;

        // One dash surface for both modes: curved world-space panel in VR,
        // flattened and projection-fitted to the window on desktop. The free
        // cursor steers the laser over it, so the laser dot is the one pointer.
        bool open = IsOpen.Value;
        bool vr = input?.VR_Active ?? false;
        if (_surfaceSlot != null)
            _surfaceSlot.ActiveSelf.Value = open;

        if (_displayMesh != null)
            _displayMesh.Curvature.Value = vr ? 0.5f : 0f;

        // First frame the dash becomes visible, force the current screen to re-render. Reactivating
        // the parked render rig doesn't dirty the canvas on its own, so without this the dash shows
        // blank until you switch tabs (which forces a rebuild). Does what a tab switch does, on open.
        if (open && !_wasOpen)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            _dashboard?.ForceRebuild();
            double took = _openMs + System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _openMs = 0.0;
            if (took > SlowOpenMs)
                Logging.Logger.Log($"UserspaceDashboard: opened in {took:0} ms");
        }
        _wasOpen = open;

        // Render the offscreen UI only when it actually changed. The canvas flags a render after each mesh
        // submit; we pulse the viewport once for it. A static dash keeps showing its last frame at zero GPU
        // cost instead of re-rendering the full-res 2x-supersampled capture every single frame. -xlinka
        if (open)
        {
            var canvas = _canvasSlot?.GetComponent<Canvas>();
            if (canvas != null && canvas.ConsumeRenderRequested())
                _renderTexture?.Asset?.RequestRender();

            // New glyphs rasterize into the shared font atlas on demand; the atlas GPU upload is deferred and
            // the glyph meshes already have correct UVs, so we just need ONE more viewport render after the
            // atlas texture has the new pixels - otherwise newly-seen text stays blank until an unrelated
            // re-dirty (the "text loads late / disappears" bug). The generation bumps at rasterize time and we
            // observe it a frame later, by which point the deferred upload has landed. It's the standard
            // atlas-update -> re-render pattern, minus any async pin/await (our rasterizer is synchronous, so the
            // UVs are already right and a re-render is enough - no re-mesh). -xlinka
            // Coherence gate: only pulse the viewport when no rebuild is in flight. This path fires DIRECTLY
            // (not via ConsumeRenderRequested), and mid-cycle the canvas shows old chunks + unmeshed new ones -
            // capturing that frame was the dash tab-switch/first-open FLICKER. Leave _lastFontGeneration
            // unconsumed on a gated frame so we retry next frame once the submit lands. -xlinka
            int fontGeneration = Font.Target?.Asset?.CacheGeneration ?? 0;
            if (fontGeneration != _lastFontGeneration && canvas != null && !canvas.IsRebuildInProgress)
            {
                _lastFontGeneration = fontGeneration;
                _renderTexture?.Asset?.RequestRender();
            }
        }

        // Freeform in VR: a grip anywhere on the dash picks it up and lets it go where you leave it. The grab
        // is found by walking up from the surface the laser is on, so it never needs to win the laser over
        // the canvas and clicks are untouched; in edit mode a grip lifts a widget instead. -xlinka
        if (_grabHandle != null)
        {
            bool movable = open && Freeform.Value && !WidgetPanel.EditMode
                && Engine.Current?.InputInterface?.IsVRActive == true;
            if (_grabHandle.AllowGrab.Value != movable)
                _grabHandle.AllowGrab.Value = movable;
            if (_grabHandle.InteractionPriority.Value != -1)
                _grabHandle.InteractionPriority.Value = -1;
        }

        // Positioning is intentionally NOT done here, for VR or desktop. OnCommonUpdate runs the
        // userspace world BEFORE the session world each frame, so the head pose read here is one
        // frame stale; while walking that trailing offset changes every frame and the dash visibly
        // bobs against the view. Both modes position in OnLateUpdate instead (after all worlds'
        // updates), from the same post-move head pose the camera rig is placed from. While closed,
        // OnLateUpdate doesn't run (slot inactive), so keep the freeform edge-tracker current here
        // so reopening doesn't see a stale transition. -xlinka
        if (!open)
            _lastFreeform = Freeform.Value;
    }

    public override void OnLateUpdate(float delta)
    {
        base.OnLateUpdate(delta);

        // Position the dashboard here, in late update, for BOTH modes. This runs AFTER every
        // world's common update, so the session world's locomotion/physics has already moved the
        // user root/head this frame; the camera rig is placed from that same post-move pose right
        // after the engine's late update, and our slot flush to the render tree happens after this
        // call in the same frame. Desktop reads the live head to stay screen-locked; VR pins the
        // panel to the same fresh head pose the tracking origin is aligned from. VR used to do this
        // in OnCommonUpdate, which reads a one-frame-stale head (userspace updates before the
        // session world) and made the dash trail/bob while walking. Pointer hit-testing is
        // unaffected: at cast time (next common update) the surface pose and the pointer rig pose
        // both come from this same late phase, so they stay consistent with each other. -xlinka
        if (!IsOpen.Value)
            return;

        var input = Engine.Current?.InputInterface;
        bool vr = input?.VR_Active ?? false;
        bool stepped = StepOrigin(out var shift, out var turn);

        if (!vr)
        {
            PositionDesktopProjection(input);
            return;
        }

        // The view stepped to another frame (another world focused, a root that came or went): the dash goes
        // with it and keeps its place around the viewer, Freeform included. With no head to place from, the view
        // sits at the origin the step carried it to, and the dash stays where that left it until a head turns up.
        // -xlinka
        if (stepped)
            CarryWithView(shift, turn);
        if (!TryGetFocusedViewPose(out _, out _))
            return;

        // Follow: carry locomotion immediately, easing only tracked head motion. -xlinka
        // Freeform: leave it where it was placed; snap it back in front once
        // at the moment freeform is switched on so it stays within reach.
        bool freeform = Freeform.Value;
        if (!freeform)
        {
            if (_lastFreeform)
                ResetFollowAnchor();
            else if (FollowViewWhileOpen.Value)
                FollowFocusedViewLazily(delta);
        }
        else if (!_lastFreeform)
        {
            PositionInFrontOfFocusedView();
        }
        _lastFreeform = freeform;
    }

    // Head position + yaw only. The pitch and roll of the head never reach the panel.
    private static bool TryGetFocusedViewAnchor(out float3 position, out floatQ yaw)
    {
        if (!TryGetFocusedViewPose(out position, out var headRotation))
        {
            yaw = floatQ.Identity;
            return false;
        }

        var forward = headRotation * float3.Backward;
        forward.y = 0f;
        yaw = forward.LengthSquared > 1e-6f
            ? floatQ.AxisAngleRad(float3.Up, MathF.Atan2(-forward.x, -forward.z))
            : floatQ.Identity;
        return true;
    }

    private static float AngleBetweenDegrees(floatQ a, floatQ b)
    {
        float dot = MathF.Min(1f, MathF.Abs(floatQ.Dot(a, b)));
        return 2f * MathF.Acos(dot) * (180f / MathF.PI);
    }

    private void FollowFocusedViewLazily(float delta)
    {
        if (!TryGetFocusedViewAnchor(out var headPosition, out var headYaw))
        {
            PositionInFrontOfFocusedView();
            return;
        }

        if (!_anchorValid)
        {
            _anchorPosition = _anchorTargetPosition = headPosition;
            _anchorRotation = _anchorTargetRotation = headYaw;
            _anchorValid = true;
            _followActivated = false;
        }

        float distance = (headPosition - _anchorPosition).Length;
        float angle = AngleBetweenDegrees(headYaw, _anchorRotation);
        if (distance >= FollowActivationDistance || angle >= FollowActivationAngle)
            _followActivated = true;
        if (distance <= FollowDeactivationDistance && angle <= FollowDeactivationAngle)
            _followActivated = false;
        if (_followActivated)
        {
            _anchorTargetPosition = headPosition;
            _anchorTargetRotation = headYaw;
        }

        float pt = MathF.Min(1f, delta * FollowPositionSpeed);
        float rt = MathF.Min(1f, delta * FollowRotationSpeed);
        _anchorPosition = float3.Lerp(_anchorPosition, _anchorTargetPosition, pt);
        _anchorRotation = floatQ.Slerp(_anchorRotation, _anchorTargetRotation, rt);

        PlaceRelativeToAnchor(_anchorPosition, _anchorRotation);
    }

    private void PlaceRelativeToAnchor(float3 anchorPosition, floatQ anchorRotation)
    {
        var localOffset = float3.Backward * Distance.Value + float3.Down * VerticalOffset.Value;
        Slot.GlobalPosition = anchorPosition + anchorRotation * localOffset;
        Slot.GlobalRotation = anchorRotation;
        Slot.LocalScale.Value = float3.One;
    }

    // Snap the anchor to the head right now: on open, and when freeform is switched off.
    private void ResetFollowAnchor()
    {
        if (TryGetFocusedViewAnchor(out var headPosition, out var headYaw))
        {
            _anchorPosition = _anchorTargetPosition = headPosition;
            _anchorRotation = _anchorTargetRotation = headYaw;
            _anchorValid = true;
            _followActivated = false;
            PlaceRelativeToAnchor(_anchorPosition, _anchorRotation);
            return;
        }
        _anchorValid = false;
        PositionInFrontOfFocusedView();
    }

    // Carry locomotion immediately; only movement within the tracking space uses lazy follow. Freeform
    // keeps its world position until the view changes to another world or tracking origin. -xlinka
    private bool StepOrigin(out float3 shift, out floatQ turn)
    {
        shift = float3.Zero;
        turn = floatQ.Identity;
        if (!PanelPlacement.TryOriginFrame(out var world, out var root, out var frame, out var position, out var rotation))
            return false;
        bool stepped = _originSeen && (!ReferenceEquals(world, _originWorld) || !ReferenceEquals(root, _originRoot)
            || !ReferenceEquals(frame, _originSlot)
            || (!Freeform.Value && FollowViewWhileOpen.Value
                && (position != _originPosition || rotation != _originRotation)));
        if (stepped)
            PanelPlacement.FrameShift(_originPosition, _originRotation, position, rotation, out shift, out turn);
        _originWorld = world;
        _originRoot = root;
        _originSlot = frame;
        _originPosition = position;
        _originRotation = rotation;
        _originSeen = true;
        return stepped;
    }

    // The dash and everything its follow keeps, into the view's new frame. The anchors stay level. A dash in
    // the hand rides the hand, which went with the view.
    private void CarryWithView(in float3 shift, in floatQ turn)
    {
        _anchorPosition = shift + turn * _anchorPosition;
        _anchorTargetPosition = shift + turn * _anchorTargetPosition;
        _anchorRotation = PanelPlacement.YawOf(turn * _anchorRotation);
        _anchorTargetRotation = PanelPlacement.YawOf(turn * _anchorTargetRotation);
        if (_grabHandle is { IsGrabbed: true })
            return;
        Slot.GlobalPosition = shift + turn * Slot.GlobalPosition;
        Slot.GlobalRotation = turn * Slot.GlobalRotation;
    }

    // Fit the flat surface to the window: place it ahead of the camera and scale
    // so it fills the viewport height (clamped by width), like a screen overlay.
    // Positions from the platform-pushed camera pose - the camera looks along
    // its local -Z, and the surface yaws 180 so its readable face points back
    // at the viewer with the same relative geometry as the VR placement.
    private void PositionDesktopProjection(InputInterface? input)
    {
        if (input == null || _displayMesh == null || !input.DesktopCameraPoseValid)
            return;

        // Anchor to the LIVE head pose this frame when the desktop camera is in plain first-person (the camera
        // follows the head, so this is EXACTLY the pose the view renders with). This is called from OnLateUpdate,
        // after the session world's locomotion/physics has moved the head, so it's the same post-move pose the
        // camera uses - zero lag, so walking/jumping no longer makes the panel bob. The old path read the
        // separately-sampled DesktopCameraPosition, which trails the view by a frame; during non-constant
        // locomotion that trailing offset changes every frame -> visible jitter. When a camera OVERRIDE is active
        // (3rd-person / free-cam) the view is NOT the head, so fall back to the sampled camera pose. -xlinka
        float3 camPos;
        floatQ camRot;
        if (!input.DesktopCameraHasOverride && TryGetFocusedViewPose(out var headPos, out var headRot))
        {
            camPos = headPos;
            camRot = headRot;
        }
        else
        {
            camPos = input.DesktopCameraPosition;
            camRot = input.DesktopCameraRotation;
        }

        var forward = camRot * new float3(0f, 0f, -1f);
        float distance = MathF.Max(Distance.Value, 0.2f);

        // Readable face of UI content is the surface's +Z side; with the camera
        // looking down -Z, camRot already points that face at the viewer.
        Slot.GlobalPosition = camPos + forward * distance;
        Slot.GlobalRotation = camRot;

        float viewportHeight = 2f * distance * MathF.Tan(input.DesktopCameraFovY * (MathF.PI / 180f) * 0.5f);

        // Shape the display mesh to the EXACT window aspect (the capture
        // resolution stays quantized for perf - the texture just samples across
        // this quad). With the mesh aspect matching the window, a uniform
        // height-fit fills the screen edge-to-edge with no letterbox bars.
        float exactWidth = DisplayHeight.Value * input.DesktopViewportAspect;
        if (MathF.Abs(_displayMesh.Size.Value.x - exactWidth) > 0.0005f)
            _displayMesh.Size.Value = new float2(exactWidth, DisplayHeight.Value);

        var meshSize = _displayMesh.Size.Value;
        if (meshSize.y <= 0.001f)
            return;

        Slot.LocalScale.Value = float3.One * (viewportHeight / meshSize.y);

        // Keep the capture aspect in sync with the window so content isn't
        // letterboxed inside the surface.
        SetAspect(input.DesktopViewportAspect);
    }

    public override void OnDestroy()
    {
        if (ReferenceEquals(LocalInstance, this))
            LocalInstance = null;

        var input = Engine.Current?.InputInterface;
        if (input != null)
            input.IsDashboardOpen = false;

        if (_renderRig != null && !_renderRig.IsDestroyed)
        {
            _renderRig.Destroy();
        }
        _renderRig = null;
        base.OnDestroy();
    }

    public void Open()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool wasOpen = IsOpen.Value;
        IsOpen.Value = true;
        SetDashboardOpenFlag(true);
        EnsureBuilt();
        ApplyOpenState();
        // Placed fresh in front of the view, so a step the view took while the dash was shut is not one to carry.
        _originSeen = false;
        StepOrigin(out _, out _);
        var input = Engine.Current?.InputInterface;
        if (input?.VR_Active == true)
        {
            ResetFollowAnchor();
            if (!wasOpen)
                Haptics.Pulse(OpeningHand(input), TouchHaptics.Light);
        }
        else
        {
            PositionInFrontOfFocusedView();
        }
        if (!wasOpen)
            _openMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    // The hand whose button brought the dash up, when one and only one of them is down. Opened from code,
    // from a keyboard or with a button down on each hand, nobody is told.
    private static Chirality OpeningHand(InputInterface input)
    {
        bool left = input.LeftController is { } l && (l.PrimaryButtonPressed || l.MenuHoldFired);
        bool right = input.RightController is { } r && (r.PrimaryButtonPressed || r.MenuHoldFired);
        if (left == right)
            return Chirality.None;
        return left ? Chirality.Left : Chirality.Right;
    }

    public void Close()
    {
        IsOpen.Value = false;
        SetDashboardOpenFlag(false);
        ApplyOpenState();
        _dashboard?.FlushLayout();
    }

    private static void SetDashboardOpenFlag(bool value)
    {
        var input = Engine.Current?.InputInterface;
        if (input != null)
            input.IsDashboardOpen = value;
    }

    public void Toggle()
    {
        if (IsOpen.Value) Close();
        else Open();
    }

    // freeform places the panel and leaves it; locked keeps it pinned in front of the view (VR only)
    public void SetFreeform(bool value) => Freeform.Value = value;

    public void ToggleFreeform() => Freeform.Value = !Freeform.Value;

    public void SetAspect(float aspect)
    {
        if (aspect <= 0.1f) return;
        // Quantize so near-16:9 windows resolve to the design size and small
        // window-size jitter can't trigger full canvas rebuilds.
        int w = DesignWidth;
        int h = DesignHeight;
        if (aspect >= (float)DesignWidth / DesignHeight)
            w = System.Math.Clamp((int)System.MathF.Round(DesignHeight * aspect / 64f) * 64, DesignWidth, MaxCaptureSide);
        else
            h = System.Math.Clamp((int)System.MathF.Round(DesignWidth / aspect / 64f) * 64, DesignHeight, MaxCaptureSide);
        if (w == _captureWidth && h == _captureHeight) return;

        _captureWidth = w;
        _captureHeight = h;
        if (_dashboard != null) _dashboard.Size.Value = new float2(w, h);
        if (_renderTexture != null)
        {
            _renderTexture.Size.Value = new int2(w * SupersampleScale, h * SupersampleScale);
            _renderTexture.OrthographicSize.Value = h * CanvasScale;
        }
        if (_displayMesh != null)
            _displayMesh.Size.Value = new float2(DisplayHeight.Value * ((float)w / h), DisplayHeight.Value);
    }

    public bool FeedAxis(float2 axis)
    {
        var canvas = _canvasSlot?.GetComponent<Canvas>();
        if (canvas == null) return false;
        // The dashboard hovers under a VR* source (the laser), so a fixed (Desktop,0) feed never matched
        // the hover - route the wheel to whichever pointer is actually over the canvas. -xlinka
        return canvas.ProcessAxisAnyPointer(in axis);
    }

    // Where typing goes: an app out in the room whose field is waiting for keys, while it is the last thing
    // pressed; otherwise the screen on the open dash. The headset keyboard and the hardware one both come in
    // here, so both reach a popped app the same way they reach the dash. -xlinka
    public DashboardScreen? KeyScreen
    {
        get
        {
            if (DashWindowPanel.TakesKeys)
                return DashWindowPanel.KeyFocus!.Screen;
            return IsOpen.Value ? _dashboard?.CurrentScreen : null;
        }
    }

    // Keys belong to a popped app's field even with the dash closed.
    public bool PanelTakesKeys => DashWindowPanel.TakesKeys;

    // Whether the screen on show actually wants keystrokes. The VR keyboard asks this to decide
    // whether to put itself up: a headset user typing into a dash field has no hardware keyboard to
    // reach for, and every screen that takes text says so by implementing the interface. -xlinka
    public bool CurrentScreenTakesKeys => KeyScreen is IDashboardKeyInput;

    public void FeedSearchChar(char c)
    {
        // Let the current screen grab the keystroke first (inline name fields etc.); only fall through to the
        // file-browser search buffer if nothing consumed it, and only on the dash. -xlinka
        var screen = KeyScreen;
        if (screen is IDashboardKeyInput k && k.ConsumeChar(c))
            return;
        if (PanelTakesKeys)
            return;
        if (screen is FileBrowserScreen browser)
            browser.SetSearch(browser.SearchText + c);
    }

    public void FeedSearchBackspace()
    {
        if (KeyScreen is IDashboardKeyInput k && k.ConsumeBackspace())
            return;
        if (!PanelTakesKeys && KeyScreen is FileBrowserScreen browser && browser.SearchText.Length > 0)
            browser.SetSearch(TextElements.RemoveLast(browser.SearchText));
    }

    public bool FeedEnter()
    {
        return KeyScreen is IDashboardKeyInput k && k.ConsumeEnter();
    }

    public bool FeedEscape()
    {
        return KeyScreen is IDashboardKeyInput k && k.ConsumeEscape();
    }

    public bool FeedKey(DashboardKey key)
    {
        if (KeyScreen is IDashboardKeyInput k && k.ConsumeKey(key))
            return true;
        if (key == DashboardKey.Copy && KeyScreen is IDashboardKeyInput { HasFieldFocus: true, CopyText: { } copied })
        {
            Engine.Current?.InputInterface?.ClipboardText?.SetText(copied);
            return true;
        }
        // A dash field has no paste of its own, so the clipboard goes in as typing through the same door
        // the keys use: the sign-in box took neither Ctrl+V nor the keyboard's Paste key. Only the first
        // line, so a copied password with a line break after it is not cut short or submitted. -xlinka
        if (key == DashboardKey.Paste && KeyScreen is IDashboardKeyInput { HasFieldFocus: true } field)
        {
            string text = Lumora.Core.Engine.Current?.InputInterface?.ClipboardText?.GetText() ?? string.Empty;
            int lineEnd = text.IndexOfAny(new[] { '\r', '\n' });
            if (lineEnd >= 0)
                text = text.Substring(0, lineEnd);
            foreach (char c in text)
            {
                if (!char.IsControl(c))
                    field.ConsumeChar(c);
            }
            return true;
        }
        return false;
    }

    public void ReleaseFieldFocus()
    {
        if (KeyScreen is IDashboardKeyInput k)
            k.ReleaseFieldFocus();
    }

    public void ClearSearch()
    {
        if (_dashboard?.CurrentScreen is FileBrowserScreen fb)
            fb.SetSearch(string.Empty);
    }

    // reach: how far the beam went to the surface, in metres.
    public void UpdateVrPointer(InteractionLaser laser, int pointerId, float2 normalized, bool pressed, float reach = 0f)
    {
        UpdateSurfacePointer(VrSource(laser), pointerId, normalized, pressed, reach);
    }

    // A fingertip on the display surface, under the touch source's own ids.
    public void UpdateTouchPointer(int pointerId, float2 normalized, bool pressed)
    {
        UpdateSurfacePointer(UIInteractionSource.Touch, pointerId, normalized, pressed, 0f);
    }

    public void ClearTouchPointer(int pointerId)
    {
        _canvasSlot?.GetComponent<Canvas>()?.ClearPointer(UIInteractionSource.Touch, pointerId, World?.LocalUser);
    }

    private void UpdateSurfacePointer(UIInteractionSource source, int pointerId, float2 normalized, bool pressed, float reach)
    {
        if (_canvasSlot == null) return;
        var canvas = _canvasSlot.GetComponent<Canvas>();
        if (canvas == null) return;

        float lx = (normalized.x - 0.5f) * _captureWidth;
        float ly = (0.5f - normalized.y) * _captureHeight;
        var worldPoint = _canvasSlot.LocalPointToGlobal(new float3(lx, ly, 0f));
        var forward = _canvasSlot.Forward;
        canvas.UpdatePointer(source, pointerId, worldPoint + forward * RigReach(reach), -forward, pressed, World?.LocalUser);
    }

    // The beam's reach to the surface as the same angle at the rig. The canvas sizes its hover hold and the
    // chrome its drag slop as angles at the pointer's distance, and the rig is drawn at another scale than the
    // surface in front of the viewer: a fixed reach there made them a fixed count of pixels at any reach.
    private float RigReach(float reach)
    {
        var surface = SurfaceSlot;
        if (reach <= 0f || !float.IsFinite(reach) || surface == null || _canvasSlot == null || _displayMesh == null || _captureHeight <= 0)
            return DefaultRigReach;
        float surfaceUnit = _displayMesh.Size.Value.y / _captureHeight * MathF.Abs(surface.GlobalScale.y);
        float rigUnit = MathF.Abs(_canvasSlot.GlobalScale.y);
        if (surfaceUnit <= 1e-9f || rigUnit <= 1e-9f)
            return DefaultRigReach;
        return MathF.Max(MinRigReach, reach * rigUnit / surfaceUnit);
    }

    public IGrabbable? TryGrabVrPointer(InteractionLaser laser, int pointerId)
        => _canvasSlot?.GetComponent<Canvas>()?.TryGrab(VrSource(laser), pointerId);

    public string? HoveredTip(InteractionLaser laser)
        => _canvasSlot?.GetComponent<Canvas>()?.HoveredTip(VrSource(laser), laser.PointerId);

    public bool TryReceiveVrPointer(InteractionLaser laser, int pointerId, IReadOnlyList<IGrabbable> items)
        => _canvasSlot?.GetComponent<Canvas>()?.TryReceive(items, VrSource(laser), pointerId) == true;

    // Where a canvas point sits on the display surface, plus the world size of one canvas pixel there.
    // The inverse of the portal's u/v mapping, taken flat: the curved VR surface is close enough for
    // posing a widget that has just been lifted off it. -xlinka
    public bool TryCanvasToSurface(in float2 canvasPoint, out float3 position, out floatQ rotation, out float metersPerPixel)
    {
        position = default;
        rotation = floatQ.Identity;
        metersPerPixel = 0f;
        var surface = SurfaceSlot;
        if (surface == null || _displayMesh == null || _displayMesh.IsDestroyed)
            return false;
        var size = _displayMesh.Size.Value;
        position = surface.LocalPointToGlobal(new float3(
            canvasPoint.x / _captureWidth * size.x,
            canvasPoint.y / _captureHeight * size.y,
            0f));
        rotation = surface.GlobalRotation;
        metersPerPixel = size.y / _captureHeight * surface.GlobalScale.y;
        return true;
    }

    public void ClearVrPointer(InteractionLaser laser, int pointerId)
    {
        var canvas = _canvasSlot?.GetComponent<Canvas>();
        canvas?.ClearPointer(VrSource(laser), pointerId, World?.LocalUser);
    }

    public bool ProcessVrAxis(InteractionLaser laser, int pointerId, float2 axis)
        => _canvasSlot?.GetComponent<Canvas>()?.ProcessAxis(VrSource(laser), pointerId, axis) == true;

    public bool WantsVrAxis(InteractionLaser laser, int pointerId)
        => _canvasSlot?.GetComponent<Canvas>()?.WantsAxis(VrSource(laser), pointerId) == true;

    public ScrollRect? ScrollUnderVrPointer(InteractionLaser laser, int pointerId)
        => _canvasSlot?.GetComponent<Canvas>()?.ScrollUnder(VrSource(laser), pointerId);

    private static UIInteractionSource VrSource(InteractionLaser laser)
    {
        return laser?.ControllerSide.Value == Chirality.Left
            ? UIInteractionSource.VRLeft
            : UIInteractionSource.VRRight;
    }

    private void EnsureBuilt()
    {
        EnsureFont();
        BuildRenderRig();
        BuildDisplaySurface();
        BuildDefaultScreens();
        EnsureGrabHandle();
    }

    // Freeform grab handle on the dash root: grabbing reparents this slot to the
    // hand (surface + portal ride along) and restores it on release, leaving the
    // panel where you let go. OnCommonUpdate gates AllowGrab to open+VR+freeform.
    private void EnsureGrabHandle()
    {
        if (_grabHandle != null && !_grabHandle.IsDestroyed)
            return;
        _grabHandle = Slot.GetComponent<Grabbable>() ?? Slot.AttachComponent<Grabbable>();
        _grabHandle.AllowGrab.Value = false;
        _grabHandle.Scalable.Value = false;
        _grabHandle.FollowRotation.Value = true;
        _grabHandle.Receivable.Value = false;
        _grabHandle.InteractionPriority.Value = -1;
    }

    // Lato for the dash. Fira Code stays as the fallback face so a missing font file degrades to
    // "monospace" rather than to nothing, and so old saves that still point at it keep rendering.
    private static readonly Uri DefaultFontUri = DashTheme.FontRegular;
    private static readonly Uri FallbackFontUri = DashTheme.FontMono;

    private void EnsureFont()
    {
        // Register the font URL so import dialogs can create their own
        // FontProviders in their own world (avoids cross-world AssetRef
        // rejection). Sharing a FontProvider component instance across
        // worlds doesn't work - SyncRef.Target rejects it. - xlinka
        ImportDialog.DefaultFontUrl ??= DefaultFontUri;

        if (Font.Target == null)
        {
            _fontProvider = EnsureFontProvider(_fontProvider, "UIFont", DefaultFontUri);
            Font.Target = _fontProvider;
        }
        if (FontSemibold.Target == null)
        {
            _fontProviderSemibold = EnsureFontProvider(_fontProviderSemibold, "UIFontSemibold", DashTheme.FontSemibold);
            FontSemibold.Target = _fontProviderSemibold;
        }
        if (FontBold.Target == null)
        {
            _fontProviderBold = EnsureFontProvider(_fontProviderBold, "UIFontBold", DashTheme.FontBold);
            FontBold.Target = _fontProviderBold;
        }
        if (FontMono.Target == null)
        {
            _fontProviderMono = EnsureFontProvider(_fontProviderMono, "UIFontMono", DashTheme.FontMono);
            FontMono.Target = _fontProviderMono;
        }
    }

    private FontProvider EnsureFontProvider(FontProvider? existing, string slotName, Uri url)
    {
        existing ??= Slot.FindChild(slotName, recursive: false)?.GetComponent<FontProvider>();
        if (existing != null && !existing.IsDestroyed)
            return existing;

        var provider = Slot.AddSlot(slotName).AttachComponent<FontProvider>();
        provider.URL.Value = url;
        provider.FallbackURLs.Add(FallbackFontUri);
        return provider;
    }

    private void BuildRenderRig()
    {
        if (_renderRig != null && !_renderRig.IsDestroyed) return;

        var parent = Slot.Parent ?? World.RootSlot;
        _renderRig = parent.AddSlot("DashRenderRig");
        _renderRig.Persistent.Value = false;
        _renderRig.GlobalPosition = RigWorldPosition;
        _renderRig.GlobalRotation = floatQ.Identity;

        var layerOverride = _renderRig.AttachComponent<RenderLayerOverride>();
        layerOverride.Layer.Value = RenderLayerOverride.HiddenLayer;

        _canvasSlot = _renderRig.AddSlot("DashCanvas");
        _canvasSlot.LocalScale.Value = float3.One * CanvasScale;

        _dashboard = _canvasSlot.AttachComponent<Dashboard>();
        _dashboard.Size.Value = new float2(_captureWidth, _captureHeight);
        _dashboard.Font.Target = Font.Target;
        _dashboard.FontSemibold.Target = FontSemibold.Target;
        _dashboard.FontBold.Target = FontBold.Target;
        _dashboard.FontMono.Target = FontMono.Target;

        _renderTexture = _renderRig.AttachComponent<RenderTextureProvider>();
        _renderTexture.Size.Value = new int2(_captureWidth * SupersampleScale, _captureHeight * SupersampleScale);
        // A clear capture: the UI blends over nothing and comes out with its colour multiplied by its own
        // coverage, which is what the composite on the surface (and on the desktop window) expects. A
        // straight-alpha read of it is what made an earlier clear capture look ghostly.
        _renderTexture.ClearColor.Value = color.Transparent;
        _renderTexture.CullMask.Value = RenderLayerOverride.HiddenLayer;
        _renderTexture.OrthographicSize.Value = _captureHeight * CanvasScale;
        _renderTexture.CameraPosition.Value = RigWorldPosition + new float3(0f, 0f, CaptureDistance);
        _renderTexture.CameraRotation.Value = floatQ.Identity;
    }

    private void BuildDisplaySurface()
    {
        if (_built) return;
        if (_renderTexture == null) return;
        _built = true;

        float aspect = (float)_captureWidth / _captureHeight;
        float height = DisplayHeight.Value;

        _surfaceSlot = Slot.AddSlot("Surface");
        _surfaceSlot.ActiveSelf.Value = false;

        _displayMesh = _surfaceSlot.AttachComponent<CurvedPlaneMesh>();
        _displayMesh.Size.Value = new float2(height * aspect, height);
        _displayMesh.Curvature.Value = 0.5f;
        _displayMesh.Segments.Value = 24;

        // Past the depth buffer, so the dash stays over session-world geometry. The capture is the dash's
        // isolation from the room's light: it is drawn unlit in its own pass, and what the room adds here is
        // only its picture, blurred and darkened behind the glass. - xlinka
        _displayMaterial = DashGlass.AttachComposite(_surfaceSlot, _renderTexture, 4002);

        var renderer = _surfaceSlot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = _displayMesh;
        renderer.Material.Target = _displayMaterial;
        // Draw order is SortingOffset = material RenderQueue + renderer SortingOrder (uncapped), NOT the
        // Godot-clamped render_priority. With SortingOrder 0 the dash sat at ~4002, BELOW every laser visual
        // (~4100), the context menu (~10000) and any other session overlay - so they drew over it. Put the
        // dash in a high reserved band (offset ~24002) so nothing in the session world covers it; the laser
        // cursor sits one band above (see InteractionLaser) so the pointer stays visible on the dash. -xlinka
        renderer.SortingOrder.Value = DashSurfaceSortingOrder;
        _displayRenderer = renderer;
        ApplyDisplayVisibility();

        _surfaceSlot.AttachComponent<DashSurfacePortal>();
    }

    // Desktop composites this capture straight onto the window instead of showing it on the world
    // panel, so while that is up the panel stops DRAWING. Everything else about the surface stays
    // exactly as it was: the slot keeps its active state and keeps getting posed and resized every
    // late update, because the pointer has no collider to hit - DashSurfacePortal projects the laser
    // ray analytically onto this slot's z=0 plane and divides by this mesh's Size to get u/v. Kill
    // the pose or the size and you kill the cursor, so only the renderer goes. VR never takes this
    // path; there the mesh IS the display. -xlinka
    public void SetExternalDisplay(bool external)
    {
        if (_externalDisplay == external)
            return;
        _externalDisplay = external;
        ApplyDisplayVisibility();
    }

    private void ApplyDisplayVisibility()
    {
        if (_displayRenderer == null || _displayRenderer.IsDestroyed)
            return;
        bool draw = !_externalDisplay;
        if (_displayRenderer.Enabled.Value != draw)
            _displayRenderer.Enabled.Value = draw;
    }

    private void BuildDefaultScreens()
    {
        if (_defaultScreensBuilt || _dashboard == null) return;
        _defaultScreensBuilt = true;

        // Home is the workspace itself; every other screen is an app with a window. Dock order: the places
        // and people (Worlds, Groups, Friends, Inventory, Session), then the tools (Desktop, Files,
        // Settings), then Exit on its own at the bottom.
        var dash = _dashboard!;
        var home = dash.AddScreen<HomeScreen>("Home", new color(0.94f, 0.81f, 0f, 1f));
        dash.AddScreen<WorldsScreen>("Worlds", new color(0.20f, 0.80f, 1f, 1f), icon: "worlds");
        dash.AddScreen<GroupsScreen>("Groups", new color(0.55f, 0.45f, 0.95f, 1f), icon: "groups");
        dash.AddScreen<PeopleScreen>("Friends", new color(0.30f, 1f, 0.80f, 1f), icon: "friends");
        dash.AddScreen<InventoryScreen>("Inventory", new color(0.85f, 0.60f, 0.22f, 1f), icon: "inventory");
        dash.AddScreen<SessionScreen>("Session", new color(0.25f, 0.55f, 1f, 1f), icon: "session");
        // Only where this machine's desktop can actually be reached (not under Wine, not without a display
        // server). -xlinka
        if (Lumora.Core.Desktop.DesktopPlatform.IsSupported)
            dash.AddScreen<DesktopScreen>("Desktop", new color(0.93f, 0.45f, 0.80f, 1f), icon: "desktop", dockGroup: 1);
        dash.AddScreen<FileBrowserScreen>("Files", new color(0.70f, 0.66f, 0.32f, 1f), icon: "files", dockGroup: 1);
        dash.AddScreen<SettingsScreen>("Settings", new color(1f, 0.22f, 0.28f, 1f), icon: "settings", dockGroup: 1);
        // Reached from the workspace's Debug card, not from the dock.
        dash.AddScreen<DebugScreen>("Debug", new color(0.45f, 0.78f, 0.55f, 1f), showInNav: false, icon: "debug");
        dash.AddScreen<ExitScreen>("Exit", new color(1f, 0.30f, 0.32f, 1f), icon: "exit", dockGroup: 2);

        // App windows and detached panels belong to this run only. Start on Home with all apps closed,
        // including when an older installation has a saved workspace. Home widget positions are preferences.
        dash.RestoreLayout(persist: false);
        home.RestoreDesk(persist: true);
    }

    private void ApplyOpenState()
    {
        bool open = IsOpen.Value;
        Slot.ActiveSelf.Value = open;

        // The render rig lives beside this slot, not under it, so closing the dash
        // doesn't park it for free. Deactivate it (stops canvas rebuilds, widget
        // drivers, mesh uploads) and pause the offscreen viewport render, otherwise
        // a closed dash keeps re-rendering at full supersampled resolution forever.
        // While warming up at startup the rig stays on regardless of open state.
        bool runRig = open || !_rigWarmed;
        if (_renderRig != null && !_renderRig.IsDestroyed)
            _renderRig.ActiveSelf.Value = runRig;
        _renderTexture?.Asset?.SetRenderEnabled(runRig);
    }

    private void PositionInFrontOfFocusedView()
    {
        if (!TryGetFocusedViewPose(out var headPosition, out var headRotation))
        {
            Slot.LocalPosition.Value = new float3(0f, 1.45f, -Distance.Value);
            Slot.LocalRotation.Value = floatQ.Identity;
            Slot.LocalScale.Value = float3.One;
            return;
        }

        // View forward is -Z, which is float3.BACKWARD in this engine's constants: Forward is (0,0,+1)
        // and the camera looks down (0,0,-1). VrKeyboard has it right one file over. With Forward here
        // the dash opened, built, went active at full scale, and sat 1.2 m directly BEHIND the head
        // every time; measured dot(view, toDash) = -1.00. That is the whole reason pressing the dash
        // key showed nothing but the keyboard. -xlinka
        var localOffset = float3.Backward * Distance.Value + float3.Down * VerticalOffset.Value;
        Slot.GlobalPosition = headPosition + headRotation * localOffset;
        Slot.GlobalRotation = headRotation;
        Slot.LocalScale.Value = float3.One;
    }

    private static bool TryGetFocusedViewPose(out float3 headPosition, out floatQ headRotation)
    {
        var root = Engine.Current?.WorldManager?.FocusedWorld?.LocalUser?.Root;
        if (root?.HeadSlot != null)
        {
            headPosition = root.HeadPosition;
            headRotation = root.HeadRotation;
            return true;
        }

        headPosition = new float3(0f, 1.6f, 0f);
        headRotation = floatQ.Identity;
        return false;
    }

    private static void Fill(RectTransform rect)
    {
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;
        rect.OffsetMin.Value = float2.Zero;
        rect.OffsetMax.Value = float2.Zero;
    }
}
