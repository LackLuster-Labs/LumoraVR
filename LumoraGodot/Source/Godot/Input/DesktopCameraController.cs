// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Godot;
using Lumora.Godot.Extensions;
using Lumora.Source.UI;
using Lumora.Source.Godot.UI;
using Lumora.Core.Components;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Input.Actions;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.Input;

// Desktop camera modes. F5 toggles a third-person orbit, F6 a free camera. Ctrl + F flies the free camera to
// the object under the pointer and keeps it framed (alt + F lets it go, and leaves the free camera if focusing
// opened it); ctrl + click squares the view on the panel under the pointer, and ctrl + click anywhere else or
// alt + F goes back to the view you had.
//
// There is ONE rendering camera (the HeadOutput screen camera); each mode just feeds it a pose through
// HeadOutput's override and never spawns its own, so the dashboard, cursor ray and laser follow the view in
// every mode. First person clears the override. Every switch starts HeadOutput's view fade from wherever the
// camera is, so the modes write their target pose straight away and never ease on their own.
//
// Third person orbits the head over the right shoulder on the mouse with the cursor locked, the crosshair being
// the pointer; the wheel sets the distance and ctrl + right drag moves the shoulder and the distance (two quick
// ctrl + right clicks put both back). While the hand has something to act on, the view slides over that shoulder so the body stops
// standing in front of it. The free camera and a focused panel point with a FREE cursor, as the dash does: the
// laser runs through the arrow. Holding ctrl + right locks it to look round, alt + right swings round what is
// focused (or what the arrow was on), and the wheel moves in and out while either is held. -xlinka
public partial class DesktopCameraController : Node
{
    public enum CameraMode { FirstPerson, ThirdPerson, FreeCam }
    public enum FocusKind { None, Object, Panel }

    // THIRD PERSON
    private const float TpDefaultDistance = 2f;
    private const float TpMinDistance = 0.8f;
    private const float TpMaxDistance = 6f;
    private const float TpZoomPerNotch = 0.35f;
    private const float TpFallbackEyeHeight = 1.6f; // only until the rig's head body node resolves
    private const float TpSmoothRate = 10f;          // per second, toward the scrolled distance and shoulder
    private const float TpMinUserScale = 0.1f;
    private const float TpMaxUserScale = 10f;
    // Pitch is the look's: above zero looks up, so the camera drops below the head. The default looks down from
    // just above it. It used to start at +12 degrees, under the chin looking up, with the range the wrong way
    // round so the camera could barely rise over the head and went straight into the floor. -xlinka
    private const float TpDefaultPitch = -0.21f;
    private const float TpMinPitch = -1.40f;
    private const float TpMaxPitch = 1.05f;
    // The sideways shoulder is a share of the distance. It rests a little to the right so the head is not
    // sitting on the crosshair, ctrl + right drag moves it, and a hand with something to act on pulls it out to
    // at least TpInteractShoulder on its side, quickly in and slowly back.
    private const float TpDefaultShoulder = 0.2f;
    private const float TpMaxShoulder = 0.85f;
    private const float TpInteractShoulder = 0.3f;
    private const float TpInteractInRate = 3f;
    private const float TpInteractOutRate = 0.6f;
    private const float TpInteractHoldSeconds = 2f;
    private const float TpPanDistancePerScreen = 3f;
    private const double TpResetTapSeconds = 0.3;

    // THIRD-PERSON CAMERA COLLISION
    // The probe starts 5 cm off the head (user-scaled) so it cannot begin inside the avatar's own head
    // geometry. The standoff is 1 cm, but never less than the render camera's near plane (read live off
    // HeadOutput below), because a wall closer to the lens than the near plane is not drawn and the view
    // looks straight through it - the pull-in would run and the wall would still vanish. -xlinka
    private const float TpCollisionRayStart = 0.05f;
    private const float TpCollisionStandoff = 0.01f;
    private const float TpCollisionReleaseRate = 8f;   // per second, easing back OUT once the way is clear

    // FREE CAM
    private const float FreeCamBaseSpeed = 5f;
    private const float FreeCamFastMult = 4f;
    // Out of first person the free camera starts this far behind the eyes and a little above, so the first
    // thing it shows is you.
    private const float FreeCamPullBack = 1.5f;
    private const float FreeCamLift = 0.2f;
    private const float FreeCamZoomShare = 0.15f;    // of the distance to the pivot, per notch
    private const float FreeCamZoomStep = 0.5f;      // metres per notch with nothing to zoom toward
    private const float OrbitFallbackDistance = 2f;
    private const float LookReferenceHeight = 1080f;
    private const uint FreeCamIndicatorLayer = (uint)Lumora.Godot.Helpers.RenderHelper.FREECAM_INDICATOR_LAYER;

    // PANEL FOCUS
    private const float PanelZoomPerNotch = 1.12f;

    // Touch drags arrive in pixels.
    private static float TouchSensitivity => (Mathf.Pi / LookReferenceHeight) * InterfaceSettings.MouseSensitivity;

    // REFERENCES
    private Lumora.Core.Engine _engine = null!;
    private Lumora.Source.Godot.Bootstrap.HeadOutput _headOutput = null!;
    private Node3D _freeCamIndicator = null!;
    private Label3D _freeCamLabel = null!;

    // STATE
    public static CameraMode ActiveMode { get; private set; } = CameraMode.FirstPerson;
    public static FocusKind ActiveFocus { get; private set; } = FocusKind.None;

    private CameraMode _mode = CameraMode.FirstPerson;
    private Vector2 _pendingTouch;

    // Third-person orbit (radians, metres)
    private float _tpDistance = TpDefaultDistance;
    private float _tpSmoothDistance = TpDefaultDistance;
    private float _tpShoulder = TpDefaultShoulder;
    private float _tpSmoothShoulder = TpDefaultShoulder;
    private bool _tpSeeded;
    private float _tpOrbitYaw;
    private float _tpOrbitPitch = TpDefaultPitch;
    private float _tpInteract;
    private float _tpInteractHold;
    private float _tpLastInteractShift;
    private double _tpLastDragPress = double.NegativeInfinity;

    // Orbit anchor, resolved from the focused world's local user root and re-resolved only when the
    // root changes or the rig is rebuilt. UserRoot.HeadSlot walks the registered-component table
    // behind a predicate closure, so calling it every frame at 144Hz is a per-frame allocation for a
    // slot that changes about once per world load. -xlinka
    private UserRoot? _tpAnchorRoot;
    private Lumora.Core.Slot? _tpHeadSlot;

    // Camera collision: the ABSOLUTE distance along the orbit ray the nearest obstruction allows
    // (+inf when the way is clear), kept apart from the orbit distance so scroll zoom is never
    // smoothed twice. It is a cap rather than an amount pulled in because an amount goes stale the
    // moment you scroll. The exclude list is one entry (the local user root) reused across frames;
    // the physics hook walks ancestors so the whole avatar is skipped off that one slot. -xlinka
    private float _tpObstructionLimit = float.PositiveInfinity;
    private readonly List<Lumora.Core.Slot> _rayExclude = new(4);

    // The right hand, found once per user root.
    private UserRoot? _handRoot;
    private HandTool? _rightHand;

    // The UserInputState the camera last wrote its flags on, and what it wrote. The flags live on a PER-WORLD
    // component (that world's local user root): walk through a portal in F5 and the old world would keep mouse
    // look off and the head shown forever if the flags were cleared on whichever world is focused at exit time.
    // Only changes are written, so a context menu holding mouse look off in first person is never undone. -xlinka
    private UserInputState? _flaggedState;
    private bool _wroteExternal, _wroteFreeCam, _wroteLookOff;

    // Set when a switch back to first person is waiting for the camera to land on the head. The flags
    // stay as they were until then: dropping the external flag the instant F5 is released hides the local
    // head while the camera is still behind it, and the head drive reads the same flags for its mode. -xlinka
    private bool _landing;
    private bool _landExternal, _landFreeCam, _landLookOff;

    // Free cam
    private Vector3 _freeCamPos;
    private float _freeCamYaw;
    private float _freeCamPitch;
    private CameraMode _freeCamReturn = CameraMode.FirstPerson;
    private bool _freeCamByFocus;
    private Vector3? _orbitPivot;
    private bool _orbitWasHeld;

    // Object focus (free cam only): the camera follows the object's moves, not its turns, and stops following
    // while a hand carries it so dragging the thing about does not drag the view with it.
    private Lumora.Core.Slot? _focusTarget;
    private Vector3 _focusCenter;
    private Vector3 _focusLast;

    // Panel focus, over whatever mode was on (it comes back on leaving).
    private DesktopViewFraming.PanelFrame? _panel;
    private float _panelDistance;
    private Vector2 _panelPan;

    // Free cursor
    private bool _cursorFreed;
    private Vector2? _cursorRestore;
    private Vector2? _cursorWarp;

    // INIT

    public void Initialize(Lumora.Core.Engine engine, Lumora.Source.Godot.Bootstrap.HeadOutput headOutput)
    {
        _engine = engine;
        _headOutput = headOutput;
    }

    public override void _Ready()
    {
        CreateFreeCamIndicator();
    }

    // Without this, the camera could stay stuck in an override pose and every F8 cycle would leak
    // an indicator into the scene tree. - xlinka
    public override void _ExitTree()
    {
        _headOutput?.ClearPositionOverride();
        _headOutput?.ClearRotationOverride();
        _headOutput?.CancelViewTransition();
        _mode = CameraMode.FirstPerson;
        _panel = null;
        _focusTarget = null;
        _landing = false;
        ActiveMode = CameraMode.FirstPerson;
        ActiveFocus = FocusKind.None;
        SetCursorFree(false);
        var input = Lumora.Core.Engine.Current?.InputInterface;
        if (input != null)
        {
            input.DesktopViewOnPanel = false;
            input.DesktopFreeCamera = false;
        }
        ClearFlags();

        if (_freeCamIndicator != null && GodotObject.IsInstanceValid(_freeCamIndicator))
            _freeCamIndicator.QueueFree();
        _freeCamIndicator = null!;
        _freeCamLabel = null!;

        base._ExitTree();
    }

    private void CreateFreeCamIndicator()
    {
        try
        {
            _freeCamIndicator = new Node3D { Name = "FreeCamIndicator" };
            _freeCamIndicator.Visible = false;

            var mesh = new MeshInstance3D();
            mesh.Layers = FreeCamIndicatorLayer;
            mesh.Mesh = new SphereMesh { Radius = 0.18f, Height = 0.36f };
            var mat = new StandardMaterial3D
            {
                AlbedoColor     = new Color(0.55f, 0.55f, 1.0f),
                EmissionEnabled = true,
                Emission        = new Color(0.25f, 0.25f, 0.75f),
            };
            mesh.MaterialOverride = mat;
            _freeCamIndicator.AddChild(mesh);

            _freeCamLabel = new Label3D
            {
                Name        = "UsernameLabel",
                Text        = "freecam",
                Billboard   = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                PixelSize   = 0.004f,
                FontSize    = 28,
                Layers      = FreeCamIndicatorLayer,
                Position    = new Vector3(0f, 0.38f, 0f),
            };
            _freeCamIndicator.AddChild(_freeCamLabel);

            // Add directly to the scene-tree root so it has a world-space transform
            GetTree().Root.CallDeferred(Node.MethodName.AddChild, _freeCamIndicator);
        }
        catch (System.Exception ex)
        {
            LumoraLogger.Warn($"DesktopCameraController: FreeCam indicator disabled ({ex.Message})");
            _freeCamIndicator?.QueueFree();
            _freeCamIndicator = null!;
            _freeCamLabel = null!;
        }
    }

    // GODOT CALLBACKS

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        var input = Lumora.Core.Engine.Current?.InputInterface;
        var camera = input?.Actions?.Camera;

        if (_cursorWarp is { } warp && global::Godot.Input.MouseMode == global::Godot.Input.MouseModeEnum.Visible)
        {
            _cursorWarp = null;
            if (DisplayServer.WindowIsFocused())
                GetViewport().WarpMouse(warp);
        }

        if (_landing && (_headOutput == null || !_headOutput.IsViewTransitioning))
            _landing = false;

        if (input != null && !input.IsVRActive && camera != null)
            HandleKeys(input, camera);

        bool dash = DashboardToggle.IsDashboardVisible;
        bool held = UserInputState.FocusedDesktopInputSuppressed || UserInputState.FocusedViewHeld;
        bool frozen = dash || held;
        bool padHint = input?.Actions?.HintDevice == InputDeviceKind.Gamepad;
        bool drag = camera?.Drag.Held == true && !frozen;
        bool orbit = camera?.Orbit.Held == true && !frozen;

        // The mouse in radians, as first-person look reads it, plus any touch drag; nothing while the dash or a
        // menu has the pointer.
        var mouseDelta = Vector2.Zero;
        if (!frozen && input?.Mouse != null)
        {
            var d = input.Mouse.DirectDelta.Value;
            mouseDelta = new Vector2(d.x, d.y);
        }
        var look = frozen ? Vector2.Zero : mouseDelta * Mathf.Pi + _pendingTouch * TouchSensitivity;
        _pendingTouch = Vector2.Zero;
        var stick = frozen ? Vector2.Zero : StickLook();

        if (_panel != null)
            UpdatePanelFocus(input, camera, mouseDelta, drag, frozen);
        else if (_mode == CameraMode.ThirdPerson)
            UpdateThirdPerson(dt, camera, mouseDelta, look, stick, drag, frozen);
        else if (_mode == CameraMode.FreeCam)
            UpdateFreeCam(dt, camera, look, stick, drag, orbit, frozen, padHint);

        bool pointsWithCursor = _panel != null || _mode == CameraMode.FreeCam;
        SetCursorFree(pointsWithCursor && !drag && !orbit && !padHint && input?.IsVRActive != true,
            heldLock: pointsWithCursor && (drag || orbit));

        if (input != null)
        {
            input.DesktopViewOnPanel = _panel != null;
            input.DesktopFreeCamera = _mode == CameraMode.FreeCam;
        }
        ActiveMode = _mode;
        ActiveFocus = _panel != null ? FocusKind.Panel
            : _mode == CameraMode.FreeCam && _focusTarget != null ? FocusKind.Object
            : FocusKind.None;
        if (_freeCamIndicator != null)
            _freeCamIndicator.Visible = _mode == CameraMode.FreeCam && _panel == null;
        SyncFlags();
    }

    public void AddTouchMotion(Vector2 motion)
    {
        if (DashboardToggle.IsDashboardVisible || UserInputState.FocusedDesktopInputSuppressed || UserInputState.FocusedViewHeld) return;
        if (_mode != CameraMode.FirstPerson || _panel != null) _pendingTouch += motion;
    }

    // KEYS

    private void HandleKeys(Lumora.Core.Input.InputInterface input, CameraActions camera)
    {
        if (camera.ThirdPerson.Pressed)
        {
            LeavePanel(transition: false);
            SwitchMode(_mode == CameraMode.ThirdPerson ? CameraMode.FirstPerson : CameraMode.ThirdPerson);
        }

        if (camera.FreeCam.Pressed)
        {
            LeavePanel(transition: false);
            if (_mode == CameraMode.FreeCam)
            {
                SwitchMode(_freeCamReturn);
            }
            else
            {
                _freeCamReturn = _mode;
                _freeCamByFocus = false;
                SwitchMode(CameraMode.FreeCam);
            }
        }

        // The dash owns the pointer while it is up; what the world laser last rested on is stale there.
        if (DashboardToggle.IsDashboardVisible)
            return;

        if (camera.Focus.Pressed)
            FocusObject();
        if (camera.Unfocus.Pressed)
            Unfocus();
        if (camera.FocusPanel.Pressed)
            FocusPanel(input);
    }

    // MODE SWITCHING

    private void SwitchMode(CameraMode newMode)
    {
        var prev = _mode;
        if (newMode == prev)
            return;

        // The view fades from where the camera is now into the new mode's pose; the head drive fades the
        // head over the same window.
        _headOutput?.BeginViewTransition();

        if (newMode == CameraMode.FirstPerson)
        {
            // The flags stay as they were until the camera lands on the head (see _landing).
            _landing = true;
            _landExternal = _wroteExternal;
            _landFreeCam = _wroteFreeCam;
            _landLookOff = _wroteLookOff;
        }
        else
        {
            _landing = false;
        }

        if (prev == CameraMode.FreeCam)
        {
            _focusTarget = null;
            _orbitPivot = null;
        }

        _mode = newMode;
        ActiveMode = newMode;

        switch (newMode)
        {
            case CameraMode.FirstPerson:
                _headOutput?.ClearPositionOverride();
                _headOutput?.ClearRotationOverride();
                LumoraLogger.Debug("[DesktopCameraController] First-person");
                break;

            case CameraMode.ThirdPerson:
                _tpOrbitYaw = GetCharacterBodyYaw();
                _tpOrbitPitch = TpDefaultPitch;
                // Snap the distance on the first frame instead of easing in from the last session's
                // value, and drop the cached rig slots so a world change can't pivot on a dead head.
                _tpSeeded = false;
                _tpAnchorRoot = null;
                _tpHeadSlot = null;
                _tpObstructionLimit = float.PositiveInfinity;
                _tpInteract = 0f;
                _tpInteractHold = 0f;
                LumoraLogger.Debug("[DesktopCameraController] Third-person (mouse orbits, wheel = distance, ctrl + right drag = shoulder)");
                break;

            case CameraMode.FreeCam:
                SeedFreeCamFromActiveCamera(pullBack: prev == CameraMode.FirstPerson && !_freeCamByFocus);
                RefreshFreeCamLabel();
                LumoraLogger.Debug("[DesktopCameraController] Free-cam (free cursor; ctrl + right looks, alt + right orbits, WASD flies)");
                break;
        }
    }

    // FLAGS

    private void SyncFlags()
    {
        bool external, freeCam, lookOff;
        if (_landing)
        {
            external = _landExternal;
            freeCam = _landFreeCam;
            lookOff = _landLookOff;
        }
        else
        {
            external = _mode != CameraMode.FirstPerson || _panel != null;
            // A focused panel holds the body still the way the free camera does.
            freeCam = _mode == CameraMode.FreeCam || _panel != null;
            lookOff = _mode == CameraMode.ThirdPerson && _panel == null;
        }

        var state = UserInputState.ForFocusedLocalUser;
        if (!ReferenceEquals(state, _flaggedState))
        {
            ClearFlags();
            _flaggedState = state;
        }
        if (!IsAlive(state))
            return;

        if (external != _wroteExternal)
        {
            state.SetExternalCameraActive(external);
            _wroteExternal = external;
        }
        if (freeCam != _wroteFreeCam)
        {
            state.SetFreeCamActive(freeCam);
            _wroteFreeCam = freeCam;
        }
        if (lookOff != _wroteLookOff)
        {
            state.SetMouseLookSuppressed(lookOff);
            _wroteLookOff = lookOff;
        }
    }

    // Take every flag this controller raised back off the state it raised them on.
    private void ClearFlags()
    {
        var state = IsAlive(_flaggedState) ? _flaggedState : null;
        if (state != null)
        {
            if (_wroteExternal) state.SetExternalCameraActive(false);
            if (_wroteFreeCam) state.SetFreeCamActive(false);
            if (_wroteLookOff) state.SetMouseLookSuppressed(false);
        }
        _flaggedState = null;
        _wroteExternal = _wroteFreeCam = _wroteLookOff = false;
    }

    private static bool IsAlive([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] UserInputState? state)
        => state != null && !state.IsDestroyed;

    // FREE CURSOR

    // The arrow comes back where it was when a held look let go of it, rather than in the middle where the
    // lock left it. The warp waits a frame for the platform to show the cursor again. Leaving a mode that
    // points with the cursor forgets the spot.
    private void SetCursorFree(bool free, bool heldLock = false)
    {
        if (free == _cursorFreed)
            return;
        _cursorFreed = free;
        var input = Lumora.Core.Engine.Current?.InputInterface;
        input?.SetCursorFree(this, free);
        if (!IsInsideTree())
            return;
        if (!free)
        {
            _cursorRestore = heldLock ? GetViewport().GetMousePosition() : null;
            _cursorWarp = null;
        }
        else if (_cursorRestore is { } at)
        {
            _cursorWarp = at;
            _cursorRestore = null;
        }
    }

    // FOCUS

    private HandTool? RightHand()
    {
        var root = (_engine ?? Lumora.Core.Engine.Current)?.WorldManager?.FocusedWorld?.LocalUser?.Root;
        if (root == null || root.IsDestroyed || root.Slot == null)
            return null;
        if (!ReferenceEquals(root, _handRoot) || _rightHand == null || _rightHand.IsDestroyed)
        {
            _handRoot = root;
            _rightHand = null;
            foreach (var hand in root.Slot.GetComponentsInChildren<HandTool>())
            {
                if (hand.Side.Value == Lumora.Core.Input.Chirality.Right)
                {
                    _rightHand = hand;
                    break;
                }
            }
        }
        return _rightHand;
    }

    private Lumora.Core.Slot? PointerHit()
    {
        var laser = RightHand()?.Laser;
        if (laser == null || laser.IsDestroyed)
            return null;
        return laser.CurrentHitSlot ?? laser.CurrentColliderHitSlot;
    }

    private Vector3 ViewPosition() => _headOutput?.CameraPosition ?? Vector3.Zero;

    private void FocusObject()
    {
        var hit = PointerHit();
        if (hit == null || hit.IsUnderLocalUser)
            return;
        if (!DesktopViewFraming.TryFrameObject(hit, ViewPosition().ToLumora(), out var frame))
            return;

        LeavePanel(transition: false);
        if (_mode != CameraMode.FreeCam)
        {
            _freeCamReturn = _mode;
            _freeCamByFocus = true;
            SwitchMode(CameraMode.FreeCam);
        }
        else
        {
            _headOutput?.BeginViewTransition();
        }

        _focusTarget = frame.Target;
        _focusCenter = frame.Center.ToGodot();
        _focusLast = frame.Target.GlobalPosition.ToGodot();
        _orbitPivot = null;
        _freeCamPos = frame.ViewPosition.ToGodot();
        LookAtPoint(_focusCenter);
        LumoraLogger.Debug($"[DesktopCameraController] Focus on {frame.Target.Name} (radius {frame.Radius:0.00} m)");
    }

    private void Unfocus()
    {
        if (_panel != null)
        {
            LeavePanel(transition: true);
            return;
        }
        if (_mode != CameraMode.FreeCam)
            return;
        _focusTarget = null;
        if (_freeCamByFocus)
            SwitchMode(_freeCamReturn);
    }

    private void FocusPanel(Lumora.Core.Input.InputInterface input)
    {
        var hit = PointerHit();
        if (hit != null && DesktopViewFraming.TryFramePanel(hit, input.DesktopCameraFovY, input.DesktopViewportAspect, UserScale(), out var frame))
        {
            _headOutput?.BeginViewTransition();
            _panel = frame;
            _panelDistance = frame.Distance;
            _panelPan = Vector2.Zero;
            LumoraLogger.Debug($"[DesktopCameraController] Panel focus ({frame.Size.x:0.00} x {frame.Size.y:0.00} m at {frame.Distance:0.00} m)");
            return;
        }
        if (_panel != null)
            LeavePanel(transition: true);
    }

    private void LeavePanel(bool transition)
    {
        if (_panel == null)
            return;
        _panel = null;
        if (transition || _mode == CameraMode.FirstPerson)
            _headOutput?.BeginViewTransition();
        if (_mode == CameraMode.FirstPerson)
        {
            _headOutput?.ClearPositionOverride();
            _headOutput?.ClearRotationOverride();
            // Hold the external flags until the view is back behind the eyes, as any return to first person does.
            _landing = true;
            _landExternal = _wroteExternal;
            _landFreeCam = _wroteFreeCam;
            _landLookOff = _wroteLookOff;
        }
        else if (_mode == CameraMode.ThirdPerson)
        {
            _tpObstructionLimit = float.PositiveInfinity;
        }
    }

    // PANEL FOCUS

    private void UpdatePanelFocus(Lumora.Core.Input.InputInterface? input, CameraActions? camera, Vector2 mouseDelta, bool drag, bool frozen)
    {
        if (_headOutput == null || _panel == null)
            return;
        var frame = _panel.Value;
        if (frame.Space == null || frame.Space.IsDestroyed)
        {
            LeavePanel(transition: true);
            return;
        }

        float userScale = UserScale();
        if (!frozen && camera != null && !UserInputState.FocusedScrollWheelCaptured)
        {
            float zoom = camera.Zoom.Value;
            if (zoom != 0f)
            {
                _panelDistance = Mathf.Clamp(_panelDistance * Mathf.Pow(PanelZoomPerNotch, -zoom),
                    DesktopViewFraming.PanelZoomMin * userScale, DesktopViewFraming.PanelZoomMax * userScale);
            }
        }

        DesktopViewFraming.PanelPose(frame, _panelDistance, out var _, out var centerL, out var facingL, out var upL);
        var center = centerL.ToGodot();
        var facing = facingL.ToGodot();
        var up = upL.ToGodot();
        var basis = Basis.LookingAt(-facing, up);

        // Ctrl + right drag moves the view across the panel, a whole view's height for a whole screen's drag.
        if (drag)
        {
            float fov = input?.DesktopCameraFovY ?? 70f;
            float viewHeight = 2f * _panelDistance * Mathf.Tan(Mathf.DegToRad(fov) * 0.5f);
            _panelPan += new Vector2(mouseDelta.X, -mouseDelta.Y) * viewHeight;
        }

        var position = center + basis.X * _panelPan.X + basis.Y * _panelPan.Y + facing * _panelDistance;
        _headOutput.SetPositionOverride(position);
        _headOutput.SetRotationOverride(basis.GetRotationQuaternion());
    }

    // THIRD-PERSON

    // Orbit distance. The wheel is shared with the held-object distance control, so a hand carrying
    // something on its laser claims it and the camera leaves that notch alone - otherwise reeling an
    // object in would zoom the view at the same time. -xlinka
    private void UpdateOrbitZoom(CameraActions? camera)
    {
        if (camera == null)
            return;
        if (DashboardToggle.IsDashboardVisible || UserInputState.FocusedScrollWheelCaptured)
            return;

        float zoom = camera.OrbitZoom.Value;
        if (zoom != 0f)
            _tpDistance = Mathf.Clamp(_tpDistance - zoom * TpZoomPerNotch, TpMinDistance, TpMaxDistance);
    }

    // The orbit pivot is re-read from the FOCUSED world's local user root every frame, so walking carries
    // the camera with it. A static of the local player's character body (written by every world's local-user
    // hook, so owned by whichever built last) once pivoted it on a body that never moved. -xlinka
    private void UpdateThirdPerson(float dt, CameraActions? camera, Vector2 mouseDelta, Vector2 look, Vector2 stick, bool drag, bool frozen)
    {
        if (_headOutput == null) return;

        if (!frozen)
        {
            if (camera != null && camera.Drag.Pressed)
            {
                double now = Time.GetTicksMsec() / 1000.0;
                if (now - _tpLastDragPress < TpResetTapSeconds)
                {
                    _tpShoulder = TpDefaultShoulder;
                    _tpDistance = TpDefaultDistance;
                }
                _tpLastDragPress = now;
            }

            if (drag)
            {
                // Ctrl + right drag: sideways moves the shoulder, up and down the distance.
                _tpShoulder = Mathf.Clamp(_tpShoulder + mouseDelta.X, -TpMaxShoulder, TpMaxShoulder);
                _tpDistance = Mathf.Clamp(_tpDistance + mouseDelta.Y * TpPanDistancePerScreen, TpMinDistance, TpMaxDistance);
            }
            else
            {
                _tpOrbitYaw -= look.X;
                _tpOrbitPitch -= look.Y;
            }
            _tpOrbitYaw -= stick.X;
            _tpOrbitPitch -= stick.Y;
            _tpOrbitPitch = Mathf.Clamp(_tpOrbitPitch, TpMinPitch, TpMaxPitch);
            UpdateOrbitZoom(camera);

            var hand = RightHand();
            bool ready = hand != null && (hand.IsHoldingObjects || hand.Laser?.IsActive == true);
            if (ready)
            {
                _tpInteractHold = TpInteractHoldSeconds;
                _tpInteract = Mathf.Min(_tpInteract + dt * TpInteractInRate, 1f);
            }
            else if (_tpInteractHold > 0f)
            {
                _tpInteractHold -= dt;
            }
            else
            {
                _tpInteract = Mathf.Max(_tpInteract - dt * TpInteractOutRate, 0f);
            }
        }

        // No rig yet (world still loading, avatar mid-rebuild): hold the pose we already published.
        // Writing a fallback here would snap the view to world origin for those frames.
        if (!TryGetOrbitPivot(out Vector3 pivot, out float userScale))
            return;

        float ease = Mathf.Clamp(dt * TpSmoothRate, 0f, 1f);
        if (!_tpSeeded)
        {
            _tpSmoothDistance = _tpDistance;
            _tpSmoothShoulder = _tpShoulder;
            _tpSeeded = true;
        }
        else
        {
            _tpSmoothDistance = Mathf.Lerp(_tpSmoothDistance, _tpDistance, ease);
            _tpSmoothShoulder = Mathf.Lerp(_tpSmoothShoulder, _tpShoulder, ease);
        }

        float distance = _tpSmoothDistance * userScale;
        float lateral = _tpSmoothShoulder * distance;
        float interactSide = TpInteractShoulder * distance;
        float userLateral = lateral;
        if (Mathf.Abs(lateral) < interactSide)
        {
            float side = Mathf.Abs(_tpSmoothShoulder) > 0.02f ? Mathf.Sign(_tpSmoothShoulder) : 1f;
            lateral = Mathf.Lerp(lateral, side * interactSide, SmootherStep(_tpInteract));
        }
        float shift = lateral - userLateral;
        if (!drag && Mathf.Abs(shift - _tpLastInteractShift) > 1e-5f)
            HoldAimThroughShift(pivot, lateral - (shift - _tpLastInteractShift), lateral);
        _tpLastInteractShift = shift;

        var yawQ = Quaternion.FromEuler(new Vector3(0f, _tpOrbitYaw, 0f));
        var pitchQ = Quaternion.FromEuler(new Vector3(_tpOrbitPitch, 0f, 0f));
        var orbit = yawQ * pitchQ;
        Vector3 offset = orbit * (Vector3.Back * distance + Vector3.Right * lateral);
        float length = offset.Length();
        if (length < 1e-4f)
            return;
        Vector3 rayDir = offset / length;
        float reach = ResolveCameraDistance(pivot, rayDir, length, userScale, dt);

        // Look along the orbit, not at the pivot: with the shoulder out the head sits to one side of the view
        // and the crosshair goes past it.
        _headOutput.SetPositionOverride(pivot + rayDir * reach);
        _headOutput.SetRotationOverride(orbit);
    }

    // Sliding over the shoulder must not slide the aim. The crosshair rides a ray parallel to the orbit, so a
    // sideways step of half a metre would walk it off whatever it was on, the hand would lose it, the view would
    // slide back and find it again. Turn the orbit just enough that the point under the crosshair stays under
    // it: seen from above, the crosshair's line runs straight ahead at the shoulder's offset, so the yaw that
    // puts the point back at that offset is solved exactly (the pitch is left alone). -xlinka
    private void HoldAimThroughShift(Vector3 pivot, float lateralBefore, float lateralAfter)
    {
        var laser = RightHand()?.Laser;
        if (laser == null || laser.IsDestroyed || laser.CurrentTarget == null)
            return;
        var v = laser.CurrentHitPoint.ToGodot() - pivot;
        float r = Mathf.Sqrt(v.X * v.X + v.Z * v.Z);
        if (r < 1e-3f)
            return;
        float beta = Mathf.Atan2(v.Z, v.X);
        // Where the point sits across the view now, against where the crosshair's line runs.
        float across = v.X * Mathf.Cos(_tpOrbitYaw) - v.Z * Mathf.Sin(_tpOrbitYaw);
        float want = across - lateralBefore + lateralAfter;
        if (Mathf.Abs(want) >= r * 0.98f)
            return;
        float a = Mathf.Acos(want / r);
        float first = Mathf.Wrap(a - beta - _tpOrbitYaw, -Mathf.Pi, Mathf.Pi);
        float second = Mathf.Wrap(-a - beta - _tpOrbitYaw, -Mathf.Pi, Mathf.Pi);
        _tpOrbitYaw += Mathf.Abs(first) < Mathf.Abs(second) ? first : second;
    }

    // Pull the camera in front of whatever the ray from the head toward the wanted view point crosses: probe
    // against this world's solid bodies with the local user's own avatar excluded, and park the camera at
    // hit.Point + normal * standoff projected back onto the ray. Triggers are not probed and a prop that can be
    // picked up is looked past, so neither tugs the view.
    //
    // Snapping IN is immediate: a frame spent behind a wall is a frame of the world's backface soup.
    // Snapping OUT is what makes a camera jam and pop in doorways and at corners, where the ray
    // flickers between hit and miss on consecutive frames, so the release eases back over a few frames
    // instead. Worlds with no query hook (Raycast returns false) keep the free distance. -xlinka
    private float ResolveCameraDistance(Vector3 pivot, Vector3 rayDir, float freeDistance, float userScale, float delta)
    {
        float rayStart = TpCollisionRayStart * userScale;
        float limit = float.PositiveInfinity;

        if (TryProbe(pivot + rayDir * rayStart, rayDir, freeDistance - rayStart, out var point, out var normal))
        {
            float standoff = Mathf.Max(TpCollisionStandoff, _headOutput?.NearClip ?? 0f);
            Vector3 parked = point + normal * standoff;
            limit = Mathf.Max((parked - pivot).Dot(rayDir), rayStart);
        }

        if (limit <= _tpObstructionLimit || _tpObstructionLimit >= freeDistance)
        {
            // Closer obstruction, or the old cap no longer binds at this zoom: take the new one as is.
            _tpObstructionLimit = limit;
        }
        else
        {
            // The obstruction receded or cleared: ease the cap out toward where it now sits (the free
            // distance if it is gone) and drop it the moment it stops binding.
            float target = Mathf.Min(limit, freeDistance);
            _tpObstructionLimit = Mathf.Lerp(_tpObstructionLimit, target, Mathf.Clamp(delta * TpCollisionReleaseRate, 0f, 1f));
            if (_tpObstructionLimit >= freeDistance - 0.001f)
                _tpObstructionLimit = limit;
        }

        return Mathf.Min(freeDistance, _tpObstructionLimit);
    }

    // One probe against the focused world's solid bodies, past the local user's own rig and past props that can
    // be picked up or images hung on things. The physics hook already skips bodies it cannot resolve to a slot
    // in this world, which keeps every user's character body out.
    private bool TryProbe(Vector3 origin, Vector3 direction, float length, out Vector3 point, out Vector3 normal)
    {
        point = default;
        normal = default;
        var root = (_engine ?? Lumora.Core.Engine.Current)?.WorldManager?.FocusedWorld?.LocalUser?.Root;
        var physics = root?.World?.Physics;
        var rootSlot = root?.Slot;
        if (physics == null || rootSlot == null || length <= 0f)
            return false;

        _rayExclude.Clear();
        _rayExclude.Add(rootSlot);
        var from = origin.ToLumora();
        var dir = direction.ToLumora();
        for (int pass = 0; pass < 4; pass++)
        {
            if (!physics.Raycast(from, dir, length, _rayExclude, out var hit))
                return false;
            if (hit.Slot != null && (hit.Slot.GetComponent<Grabbable>() != null
                || hit.Slot.GetComponent<Lumora.Core.Assets.ImageProvider>() != null))
            {
                _rayExclude.Add(hit.Slot);
                continue;
            }
            point = hit.Point.ToGodot();
            normal = hit.Normal.ToGodot();
            return true;
        }
        return false;
    }

    // Pivot on the head body node, not on the root: the root sits at the feet, and orbiting the actual
    // head position is what makes pitch 0 look level with the player's eyes rather than up at them.
    // Distance rides the root's global scale because a shrunk or grown user needs a proportional
    // camera, not a fixed distance. -xlinka
    private bool TryGetOrbitPivot(out Vector3 pivot, out float userScale)
    {
        pivot = Vector3.Zero;
        userScale = 1f;

        // Engine.Current as a backstop: an unset _engine would silently return "no rig" forever and
        // leave the view frozen with no error, which is the failure this method exists to end.
        var engine = _engine ?? Lumora.Core.Engine.Current;
        var userRoot = engine?.WorldManager?.FocusedWorld?.LocalUser?.Root;
        if (userRoot == null || userRoot.IsDestroyed || userRoot.Slot == null)
        {
            _tpAnchorRoot = null;
            _tpHeadSlot = null;
            return false;
        }

        if (!ReferenceEquals(userRoot, _tpAnchorRoot) || _tpHeadSlot == null || _tpHeadSlot.IsDestroyed)
        {
            _tpAnchorRoot = userRoot;
            _tpHeadSlot = userRoot.HeadSlot;
        }

        var head = userRoot.GetRegisteredComponent<Lumora.Core.Components.Avatar.AvatarEquipManager>()?.AvatarHead ?? _tpHeadSlot;
        var pivotPos = (head != null && !head.IsDestroyed)
            ? head.GlobalPosition
            : userRoot.Slot.GlobalPosition + new Lumora.Core.Math.float3(0f, TpFallbackEyeHeight, 0f);

        pivot = pivotPos.ToGodot();
        userScale = UserScale();
        return true;
    }

    private float UserScale()
    {
        var root = (_engine ?? Lumora.Core.Engine.Current)?.WorldManager?.FocusedWorld?.LocalUser?.Root;
        float scale = root?.Slot?.LocalScaleToGlobal(1f) ?? 1f;
        return float.IsFinite(scale) ? Mathf.Clamp(scale, TpMinUserScale, TpMaxUserScale) : 1f;
    }

    private float GetCharacterBodyYaw()
    {
        var slot = (_engine ?? Lumora.Core.Engine.Current)?.WorldManager?.FocusedWorld?.LocalUser?.Root?.Slot;
        if (slot == null) return 0f;
        var rot = slot.GlobalRotation;
        return new Quaternion(rot.x, rot.y, rot.z, rot.w).GetEuler().Y;
    }

    // FREE CAM

    private void SeedFreeCamFromActiveCamera(bool pullBack)
    {
        var active = Lumora.Source.Godot.Bootstrap.XRModeManager.Instance?.CurrentCamera;
        if (active == null)
            return;
        _freeCamPos = active.GlobalPosition;
        var euler = active.GlobalTransform.Basis.GetEuler();
        _freeCamPitch = euler.X;
        _freeCamYaw = euler.Y;
        if (!pullBack)
            return;

        // Back out behind the eyes, short of any wall that is in the way.
        var basis = active.GlobalTransform.Basis;
        Vector3 back = basis.Z.Normalized() * FreeCamPullBack * UserScale() + Vector3.Up * FreeCamLift * UserScale();
        float length = back.Length();
        if (length < 1e-4f)
            return;
        Vector3 dir = back / length;
        if (TryProbe(_freeCamPos, dir, length, out var point, out var normal))
            back = dir * Mathf.Max((point + normal * 0.1f - _freeCamPos).Dot(dir), 0f);
        _freeCamPos += back;
    }

    private void RefreshFreeCamLabel()
    {
        if (_freeCamLabel == null) return;
        var name = _engine?.WorldManager?.FocusedWorld?.LocalUser?.UserName?.Value;
        _freeCamLabel.Text = string.IsNullOrEmpty(name) ? "[freecam]" : $"{name}\n[freecam]";
    }

    // The pad's look stick, radians this frame in the mouse's sense; nothing while something holds desktop input
    // or the view.
    private static Vector2 StickLook()
    {
        if (UserInputState.FocusedDesktopInputSuppressed || UserInputState.FocusedViewHeld)
            return Vector2.Zero;
        var look = Lumora.Core.Engine.Current?.InputInterface?.StickLook.Delta ?? default;
        return new Vector2(look.x, look.y);
    }

    private Quaternion FreeCamRotation()
        => Quaternion.FromEuler(new Vector3(0f, _freeCamYaw, 0f)) * Quaternion.FromEuler(new Vector3(_freeCamPitch, 0f, 0f));

    private void LookAtPoint(Vector3 point)
    {
        var dir = point - _freeCamPos;
        if (dir.LengthSquared() < 1e-8f)
            return;
        dir = dir.Normalized();
        _freeCamYaw = Mathf.Atan2(-dir.X, -dir.Z);
        _freeCamPitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(dir.Y, -1f, 1f)), -Mathf.Pi * 0.499f, Mathf.Pi * 0.499f);
    }

    private void UpdateFreeCam(float dt, CameraActions? camera, Vector2 look, Vector2 stick, bool drag, bool orbit, bool frozen, bool padHint)
    {
        if (_headOutput == null) return;

        // Ride along with the focused object, unless a hand is carrying it about.
        if (_focusTarget != null)
        {
            if (_focusTarget.IsDestroyed)
            {
                _focusTarget = null;
            }
            else
            {
                var now = _focusTarget.GlobalPosition.ToGodot();
                if (!IsCarried(_focusTarget))
                {
                    var moved = now - _focusLast;
                    _freeCamPos += moved;
                    _focusCenter += moved;
                }
                _focusLast = now;
            }
        }

        if (!frozen)
        {
            var oldRotation = FreeCamRotation();
            if (orbit && !_orbitWasHeld)
                _orbitPivot = _focusTarget != null ? _focusCenter : OrbitPivotUnderPointer(oldRotation);

            // The mouse looks only while it is locked: held ctrl + right, or no free cursor at all (a pad user).
            bool mouseLooks = drag || orbit || padHint;
            if (mouseLooks)
            {
                _freeCamYaw -= look.X;
                _freeCamPitch -= look.Y;
            }
            _freeCamYaw -= stick.X;
            _freeCamPitch -= stick.Y;
            _freeCamPitch = Mathf.Clamp(_freeCamPitch, -Mathf.Pi * 0.499f, Mathf.Pi * 0.499f);

            // Swinging round a pivot turns the camera's position by exactly the turn its view made.
            if (orbit && _orbitPivot is { } pivot)
            {
                var turn = FreeCamRotation() * oldRotation.Inverse();
                _freeCamPos = pivot + turn * (_freeCamPos - pivot);
            }

            if ((drag || orbit) && camera != null && !UserInputState.FocusedScrollWheelCaptured)
            {
                float zoom = camera.Zoom.Value + camera.OrbitZoom.Value;
                if (zoom != 0f)
                {
                    Vector3? toward = orbit ? _orbitPivot : _focusTarget != null ? _focusCenter : null;
                    if (toward is { } point)
                    {
                        var away = _freeCamPos - point;
                        float scale = Mathf.Pow(1f - FreeCamZoomShare, zoom);
                        _freeCamPos = point + away * scale;
                    }
                    else
                    {
                        _freeCamPos += FreeCamRotation() * Vector3.Forward * zoom * FreeCamZoomStep;
                    }
                }
            }

            float speed = camera?.FlyFast.Held == true ? FreeCamBaseSpeed * FreeCamFastMult : FreeCamBaseSpeed;
            var move = Vector3.Zero;
            if (camera != null)
            {
                // The move action is +Y forward; the camera's local forward is -Z.
                var plane = camera.FlyMove.Value;
                move.X = plane.x;
                move.Z = -plane.y;
                move.Y = camera.FlyVertical.Value;
            }
            // Keys give whole steps and are evened out on a diagonal; a stick pushed halfway flies at half pace.
            if (move.LengthSquared() > 0.001f)
                _freeCamPos += (FreeCamRotation() * (move.LengthSquared() > 1f ? move.Normalized() : move)) * speed * dt;
        }
        _orbitWasHeld = orbit;

        _headOutput.SetPositionOverride(_freeCamPos);
        _headOutput.SetRotationOverride(FreeCamRotation());

        if (_freeCamIndicator != null && _freeCamIndicator.IsInsideTree())
            _freeCamIndicator.GlobalPosition = _freeCamPos;
    }

    // What alt + right swings round when nothing is focused: whatever the arrow is on, else a couple of metres
    // ahead of the view.
    private Vector3 OrbitPivotUnderPointer(Quaternion rotation)
    {
        var laser = RightHand()?.Laser;
        if (laser != null && !laser.IsDestroyed && (laser.CurrentHitSlot != null || laser.CurrentColliderHitSlot != null))
            return (laser.CurrentHitSlot != null ? laser.CurrentHitPoint : laser.CurrentColliderHitPoint).ToGodot();
        return _freeCamPos + rotation * Vector3.Forward * OrbitFallbackDistance * UserScale();
    }

    private static bool IsCarried(Lumora.Core.Slot target)
    {
        foreach (var grabbable in target.GetComponentsInChildren<Grabbable>())
        {
            if (grabbable.IsGrabbed)
                return true;
        }
        return false;
    }

    private static float SmootherStep(float t)
    {
        t = Mathf.Clamp(t, 0f, 1f);
        return t * t * t * (t * (t * 6f - 15f) + 10f);
    }
}
