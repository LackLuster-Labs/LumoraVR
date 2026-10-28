// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;
using Lumora.Core;
using Lumora.Godot.Extensions;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.Bootstrap;

// Camera rendering for VR and screen modes: position/rotation, FOV, and view overrides.
public partial class HeadOutput : Node
{
    // Decides how the camera is positioned and rendered.
    public enum OutputType
    {
        // Headset, stereo, tracked.
        VR,

        // Standard screen, mono, user-controlled or first-person.
        Screen,

        // 360-degree equirectangular.
        Screen360,

        // Static camera, no movement.
        Static
    }

    // CONFIGURATION
    [Export] public OutputType Type { get; set; } = OutputType.Screen;
    // The screen camera follows the user's Field of View setting.
    private static float ScreenFov => Lumora.Core.EngineSettings.FieldOfView;

    // 5 mm at user scale 1, and 4 km. The near plane used to be 25 cm "so the head sphere does not clip
    // through", then 2 cm, and both were the near plane doing a job it is bad at: anything you lean into
    // gets sliced. Keeping your own head out of your own eyes belongs to LocalViewOverride (the local
    // avatar's head goes onto RenderHelper.LOCAL_HEAD_LAYER, or is collapsed on an eyes-only twin for a
    // skinned body), so the near plane can be what an eye actually wants. It scales with the user: a
    // 10x giant gets 5 cm and a 0.1x mouse gets half a millimetre, so leaning into a wall cuts at the
    // same apparent distance whatever size you are. The worn avatar can override either plane through
    // AvatarRenderSettings; the floors keep a bad value from producing a degenerate projection. 0.005 to
    // 4096 is a ~1:800000 depth ratio, comfortably inside the reversed-Z float depth buffer Forward+
    // and Mobile use; the GL path's 24-bit buffer gets coarser far out, which is the trade a 4 km far
    // plane makes there. -xlinka
    public const float DefaultNearClip = 0.005f;
    public const float DefaultFarClip = 4096f;
    private const float MinNearClip = 0.001f;
    private const float MinFarClip = 0.5f;

    // The floor above is on the avatar's setting, before the scale goes on. Applied after it, it held every
    // user under 0.2x at a millimetre of world: 1 cm of their own size at 0.1x and 2 cm at the smallest
    // allowed, so the smaller you were the more of what you leaned into got sliced. This one only keeps a
    // projection from collapsing. Measured on Godot 4.8: an XR eye's near plane is in world metres whatever
    // the origin's scale, so the planes need no other correction for a scaled tracking space. -xlinka
    private const float MinScaledNearClip = 0.0001f;

    // The planes last resolved from the local user's scale and avatar; read back by anything that wants
    // to know what the eyes are using.
    public float NearClip { get; private set; } = DefaultNearClip;
    public float FarClip { get; private set; } = DefaultFarClip;

    // CAMERA REFERENCES
    private Camera3D _camera = null!;
    private Camera3D _desktopCamera = null!;
    private XRCamera3D _vrCamera = null!;
    private XROrigin3D _xrOrigin = null!;

    // STATE
    private bool _isVRActive = false;
    private Vector3 _overridePosition = Vector3.Zero;
    private Quaternion _overrideRotation = Quaternion.Identity;
    private bool _hasPositionOverride = false;
    private bool _hasRotationOverride = false;

    // Which camera the clip planes and FOV were last written to, and with what. Every Camera3D
    // setter is a marshalled call that re-sends the projection to the rendering server, and these
    // three were rewritten every frame with values that never change. They are re-pushed when the
    // camera object changes hands (VR <-> screen), because the VR mirror overwrites the desktop
    // camera's planes and FOV while the headset is rendering. -xlinka
    private Camera3D? _settingsCamera;
    private float _appliedNear;
    private float _appliedFar;
    private float _appliedFov;

    // View transition. A camera mode switch (first person, orbit, free cam) used to put the camera at
    // the new mode's pose on the very next frame: 3.5 m in one step out of the head and back. The pose
    // the camera had when the switch happened is kept here and faded into whatever the current mode
    // wants over the same smoother-step window the head drive uses, so the two arrive together. The
    // old pose rides on the head's POSITION (walking during the blend carries it along) but keeps its
    // own rotation, because the root can turn under the head at the end of a blend and a root-relative
    // pose would swing 3 m sideways with it. -xlinka
    private const float ViewTransitionSeconds = Lumora.Core.Components.DesktopHeadSimulation.TransitionSeconds;
    private bool _viewTransitionActive;
    private float _viewTransition = 1f;
    private Vector3 _viewFromOffset;
    private Quaternion _viewFromRotation = Quaternion.Identity;
    private bool _viewFromAnchored;

    public bool IsViewTransitioning => _viewTransitionActive;

    // The slot something the local user holds or wears has put the view on (UserRoot.ViewOverride), as
    // last seen, and the world it was seen in: arriving on it and leaving it fade like a mode switch, but a
    // change of world snaps, since the old pose is in another space altogether.
    private Lumora.Core.Slot? _viewOverride;
    private World? _viewOverrideWorld;

    public Vector3 CameraPosition => _camera?.GlobalPosition ?? Vector3.Zero;

    public Quaternion CameraRotation => _camera?.GlobalTransform.Basis.GetRotationQuaternion() ?? Quaternion.Identity;

    public void Initialize(Camera3D camera)
    {
        _desktopCamera = camera;
        _camera = _desktopCamera;
        ConfigureCamera(_desktopCamera, setFov: true);

        var xrInterface = XRServer.FindInterface("OpenXR");
        _isVRActive = xrInterface != null && xrInterface.IsInitialized();

        if (_isVRActive)
        {
            SetupVRCamera();
            UseVRCamera();
            Type = OutputType.VR;
        }
        else
        {
            UseDesktopCamera();
            Type = OutputType.Screen;
        }

        LumoraLogger.Debug($"HeadOutput: Initialized with type={Type}, FOV={ScreenFov}, isVR={_isVRActive}");
    }

    // Locate the XROrigin3D / XRCamera3D defined in Bootstrap.tscn. They live inside the XR
    // SubViewport (see %XROrigin3D / %XRCamera3D) and are no longer created at runtime; the .tscn
    // ships them so the XR viewport stays valid for the whole process lifetime. Resolved via
    // CurrentScene rather than the calling node's owner-chain because HeadOutput is created at
    // runtime (Owner == null), which breaks the bare-percent unique-name lookup. - xlinka
    private void SetupVRCamera()
    {
        if (!IsInsideTree())
        {
            LumoraLogger.Debug("HeadOutput: Deferring VR setup until added to scene tree");
            CallDeferred(MethodName.SetupVRCamera);
            return;
        }

        var sceneRoot = GetTree()?.CurrentScene;
        _xrOrigin = sceneRoot?.GetNodeOrNull<XROrigin3D>("%XROrigin3D")!;
        _vrCamera = sceneRoot?.GetNodeOrNull<XRCamera3D>("%XRCamera3D")!;

        if (_xrOrigin == null || _vrCamera == null)
        {
            LumoraLogger.Error("HeadOutput: %XROrigin3D / %XRCamera3D not found in scene. " +
                "Bootstrap.tscn must contain the XR SubViewport sub-tree.");
            return;
        }

        ConfigureCamera(_vrCamera, setFov: false);

        LumoraLogger.Debug("HeadOutput: VR camera bound to %XRCamera3D");
    }

    private void UseDesktopCamera()
    {
        if (_desktopCamera == null || !GodotObject.IsInstanceValid(_desktopCamera))
        {
            LumoraLogger.Warn("HeadOutput: Cannot use desktop camera - camera is missing or invalid.");
            return;
        }

        _camera = _desktopCamera;

        if (_vrCamera != null && GodotObject.IsInstanceValid(_vrCamera) && CamerasShareViewport(_desktopCamera, _vrCamera))
            _vrCamera.Current = false;

        _desktopCamera.MakeCurrent();
    }

    private void UseVRCamera()
    {
        if (_vrCamera == null || !GodotObject.IsInstanceValid(_vrCamera))
            SetupVRCamera();

        if (_vrCamera == null || !GodotObject.IsInstanceValid(_vrCamera))
        {
            LumoraLogger.Warn("HeadOutput: Cannot use VR camera - XRCamera3D is missing or invalid.");
            return;
        }

        _camera = _vrCamera;

        if (_desktopCamera != null && GodotObject.IsInstanceValid(_desktopCamera) && CamerasShareViewport(_desktopCamera, _vrCamera))
            _desktopCamera.Current = false;

        _vrCamera.MakeCurrent();
    }

    private static bool CamerasShareViewport(Camera3D a, Camera3D b)
    {
        if (a == null || b == null || !GodotObject.IsInstanceValid(a) || !GodotObject.IsInstanceValid(b))
            return false;

        return a.GetViewport() == b.GetViewport();
    }

    private void ConfigureCamera(Camera3D camera, bool setFov)
    {
        if (camera == null)
            return;

        camera.PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off;
        camera.Near = NearClip;
        camera.Far = FarClip;

        if (setFov)
            camera.Fov = Mathf.Clamp(ScreenFov, 1f, 179f);
    }

    // Camera positioning from the focused world, once per frame after the engine update.
    public void UpdatePositioning(Lumora.Core.Engine? engine)
    {
        if (_camera == null || engine == null)
            return;

        var focusedWorld = engine.WorldManager?.FocusedWorld;
        if (focusedWorld == null)
            return;

        ResolveClipPlanes(focusedWorld);
        UpdateCameraSettings();

        if (Type == OutputType.VR)
        {
            UpdateVRPositioning(focusedWorld);
        }
        else if (Type == OutputType.Screen)
        {
            UpdateScreenPositioning(focusedWorld);
        }
        // Screen360 and Static have no positioning path yet.
    }

    // One read per frame: the local root's scale and whatever AvatarRenderSettings the worn avatar
    // registered on it. Both are cached datamodel values, so this costs a few null checks.
    private void ResolveClipPlanes(World world)
    {
        float userScale = 1f;
        var root = world.LocalUser?.Root;
        if (root != null && !root.IsDestroyed && root.Slot != null)
        {
            float scale = root.Slot.GlobalScale.x;
            if (scale > 0f && float.IsFinite(scale))
                userScale = scale;
        }

        var settings = world.LocalUserRenderSettings;
        float near = Mathf.Max(MinNearClip, settings?.NearClip ?? DefaultNearClip) * userScale;
        float far = settings?.FarClip ?? DefaultFarClip;

        NearClip = Mathf.Max(MinScaledNearClip, near);
        FarClip = Mathf.Max(MinFarClip, far);
        if (FarClip <= NearClip)
            FarClip = NearClip + 1f;
    }

    // Clip planes and FOV, written only when the value or the camera object changed.
    private void UpdateCameraSettings()
    {
        bool sameCamera = ReferenceEquals(_settingsCamera, _camera);

        if (!sameCamera || _appliedNear != NearClip)
        {
            _camera.Near = NearClip;
            _appliedNear = NearClip;
        }

        if (!sameCamera || _appliedFar != FarClip)
        {
            _camera.Far = FarClip;
            _appliedFar = FarClip;
        }

        if (Type == OutputType.Screen && (!sameCamera || _appliedFov != ScreenFov))
        {
            _camera.Fov = Mathf.Clamp(ScreenFov, 1f, 179f);
            _appliedFov = ScreenFov;
        }

        _settingsCamera = _camera;
    }

    // The XR camera and device poses share the avatar-adjusted tracking origin.
    private void UpdateVRPositioning(World world)
    {
        if (world.LocalUser == null)
            return;

        // A root override draws the headset's space from that slot instead; the head still turns the eyes.
        // A view override is a screen thing (see UserRoot.ViewOverride).
        var localRoot = world.LocalUser.Root;
        var userRootSlot = localRoot?.ActiveRootOverride ?? localRoot?.TrackingSpaceSlot;
        if (_xrOrigin != null)
        {
            if (userRootSlot != null)
            {
                var originPosition = userRootSlot.GlobalPosition.ToGodot();
                // The origin scales with the tracking space on purpose; a scale that has collapsed or is
                // not a number would squash the eye cameras flat and the renderer refuses the frame.
                var originScale = userRootSlot.GlobalScale.ToGodot();
                if (!originScale.IsFinite() || originScale.X <= 1e-5f || originScale.Y <= 1e-5f || originScale.Z <= 1e-5f)
                    originScale = Vector3.One;
                var originRotation = new Basis(userRootSlot.GlobalRotation.ToGodot()).Scaled(originScale);
                _xrOrigin.GlobalTransform = new Transform3D(originRotation, originPosition);
            }
            else
            {
                _xrOrigin.GlobalPosition = Vector3.Zero;
                _xrOrigin.GlobalRotation = Vector3.Zero;
            }
        }

        Lumora.Core.Engine.Current?.InputInterface?.SyncTrackingSpaceToFocusedLocalUser();
    }

    // Screen cameras are user-controlled or follow the first-person view.
    private void UpdateScreenPositioning(World world)
    {
        var userRoot = world.LocalUser?.Root;
        bool hasHead = userRoot != null && !userRoot.IsDestroyed && userRoot.Slot != null;
        Vector3 headPosition = hasHead ? userRoot!.HeadPosition.ToGodot() : new Vector3(0, 1.6f, 0);

        Vector3 targetPosition = _hasPositionOverride ? _overridePosition : headPosition;
        Quaternion targetRotation = _hasRotationOverride
            ? _overrideRotation
            : hasHead ? userRoot!.HeadRotation.ToGodot() : _camera.GlobalTransform.Basis.GetRotationQuaternion();

        // Something the user holds or wears asking for the view beats every camera mode, and the camera sits
        // exactly on it.
        var viewOverride = hasHead ? userRoot!.ActiveViewOverride : null;
        if (!ReferenceEquals(viewOverride, _viewOverride))
        {
            if (ReferenceEquals(world, _viewOverrideWorld))
                BeginViewTransition();
            _viewOverride = viewOverride;
        }
        _viewOverrideWorld = world;
        if (viewOverride != null)
        {
            targetPosition = viewOverride.GlobalPosition.ToGodot();
            targetRotation = viewOverride.GlobalRotation.ToGodot().Normalized();
        }

        if (_viewTransitionActive)
        {
            // A zero delta (node outside a tree) would park the camera off the head for good.
            float dt = (float)GetProcessDeltaTime();
            if (!(dt > 0f)) dt = 1f / 60f;
            _viewTransition = Mathf.Min(_viewTransition + dt / ViewTransitionSeconds, 1f);
            float blend = SmootherStep(_viewTransition);
            Vector3 fromPosition = _viewFromAnchored ? headPosition + _viewFromOffset : _viewFromOffset;
            targetPosition = fromPosition.Lerp(targetPosition, blend);
            targetRotation = _viewFromRotation.Slerp(targetRotation, blend).Normalized();
            if (_viewTransition >= 1f)
                _viewTransitionActive = false;
        }

        // Publish whether the camera is off the head (3rd-person / free-cam, or still flying back to it) so
        // the userspace dashboard knows it can't simply lock to the local head pose this frame. -xlinka
        Lumora.Core.Engine.Current?.InputInterface?.SetDesktopCameraOverride(
            _hasPositionOverride || _hasRotationOverride || _viewTransitionActive || viewOverride != null);

        // A pose that is not a number, or a rotation collapsed to zero, would leave the camera with a
        // NaN basis and the frame black. The previous pose is the better frame. -xlinka
        if (!targetPosition.IsFinite() || !IsUsableRotation(targetRotation))
            return;
        _camera.GlobalTransform = new Transform3D(new Basis(targetRotation), targetPosition);
    }

    // Start fading the camera from where it is now into whatever the current mode asks for. Called at
    // the moment of a mode switch, before the new mode has written its first override.
    private static bool IsUsableRotation(in Quaternion rotation)
        => float.IsFinite(rotation.X) && float.IsFinite(rotation.Y) && float.IsFinite(rotation.Z)
            && float.IsFinite(rotation.W) && rotation.LengthSquared() > 1e-8f;
    public void BeginViewTransition()
    {
        if (_camera == null || Type != OutputType.Screen)
            return;

        var fromPosition = _camera.GlobalPosition;
        _viewFromRotation = _camera.GlobalTransform.Basis.GetRotationQuaternion().Normalized();

        var userRoot = Lumora.Core.Engine.Current?.WorldManager?.FocusedWorld?.LocalUser?.Root;
        if (userRoot != null && !userRoot.IsDestroyed && userRoot.Slot != null)
        {
            _viewFromOffset = fromPosition - userRoot.HeadPosition.ToGodot();
            _viewFromAnchored = true;
        }
        else
        {
            _viewFromOffset = fromPosition;
            _viewFromAnchored = false;
        }

        _viewTransition = 0f;
        _viewTransitionActive = true;
    }

    public void CancelViewTransition()
    {
        _viewTransitionActive = false;
        _viewTransition = 1f;
    }

    private static float SmootherStep(float t)
    {
        t = Mathf.Clamp(t, 0f, 1f);
        return t * t * t * (t * (t * 6f - 15f) + 10f);
    }

    // Custom camera control (photo mode, cinematic cameras, third-person, free-cam).
    public void SetPositionOverride(Vector3 position)
    {
        _overridePosition = position;
        _hasPositionOverride = true;
    }

    public void ClearPositionOverride()
    {
        _hasPositionOverride = false;
    }

    public void SetRotationOverride(Quaternion rotation)
    {
        _overrideRotation = rotation;
        _hasRotationOverride = true;
    }

    public void ClearRotationOverride()
    {
        _hasRotationOverride = false;
    }

    // Called by XRModeManager when the VR active state changes at runtime.
    public void NotifyVRActiveChanged(bool isActive)
    {
        _isVRActive = isActive;

        if (isActive)
        {
            UseVRCamera();
        }
        else
        {
            UseDesktopCamera();
        }

        LumoraLogger.Debug($"HeadOutput: VR active state -> {isActive}");
    }

    // Switch output type (VR <-> Screen). When switching to VR call NotifyVRActiveChanged first so
    // _isVRActive is already up to date by the time this runs.
    public void SwitchOutputType(OutputType newType)
    {
        if (Type == newType)
            return;

        LumoraLogger.Debug($"HeadOutput: Switching from {Type} to {newType}");

        // Guard: only allow VR if the XR interface has been activated.
        if (newType == OutputType.VR && !_isVRActive)
        {
            LumoraLogger.Warn("HeadOutput: Cannot switch to VR - XR interface is not active. Staying in Screen mode.");
            return;
        }

        if (newType == OutputType.VR)
            UseVRCamera();
        else if (newType == OutputType.Screen)
            UseDesktopCamera();

        Type = newType;
    }

    public new void Dispose()
    {
        _camera = null!;
        _desktopCamera = null!;
        _vrCamera = null!;
        _xrOrigin = null!;
        _settingsCamera = null;
    }
}
