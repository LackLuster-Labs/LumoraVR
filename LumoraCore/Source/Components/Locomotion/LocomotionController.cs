// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Input;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

[ComponentCategory("Users")]
[DefaultUpdateOrder(-600000)]
public class LocomotionController : Component
{
    public override void OnStart()
    {
        base.OnStart();
        if (UpdateOrder == 0)
            UpdateOrder = -600000;
    }

    // PARAMETERS

    // DirectDelta is now normalized (1.0 = full screen height swipe).
    // Sensitivity of Pi means a full screen-height swipe = 180 degrees.
    public float MouseSensitivity { get; set; } = MathF.PI;
    public float MaxPitch { get; set; } = 89.0f;

    // Whether the user allows being scaled (grab-scale gestures, world zoom).
    // The context menu exposes it as a toggle while at default scale.
    public readonly Sync<bool> ScalingEnabled = new();

    // The module the owner has active. The choice is only made on the owner's machine, so the owner
    // writes it here for every other machine to read. -xlinka
    [NonPersistent]
    public readonly SyncRef<LocomotionModule> CurrentModule = new();

    public override void OnInit()
    {
        base.OnInit();
        ScalingEnabled.Value = true;
    }

    // STATE

    private CharacterController _characterController = null!;
    private UserRoot _userRoot = null!;
    private UserInputState _inputState = null!;
    private DesktopHeadDrive _headDrive = null!;
    private DesktopViewMode _viewMode = DesktopViewMode.FirstPerson;
    private Mouse _mouse = null!;
    private IKeyboardDriver _keyboardDriver = null!;
    private InputInterface _inputInterface = null!;
    private readonly System.Collections.Generic.List<LocomotionModule> _modules = new();
    private LocomotionModule _activeModule = null!;
    private bool _moduleSuspended;
    private LocomotionPermissions _permissions = null!;

    private float _pitch = 0.0f;
    private float _yaw = 0.0f;
    private float _crouchBlend = 1.0f;   // smoothed eye-height fraction (1 standing .. CrouchHeight/StandingHeight)
    private bool _mouseCaptured = false;
    private bool _userReleased;
    private bool _initialized = false;
    private bool _loggedMissingUserRoot = false;
    private bool _loggedActiveUserState = false;

    public UserInputState InputState => _inputState;
    public LocomotionModule ActiveModule => _activeModule;
    public System.Collections.Generic.IReadOnlyList<LocomotionModule> Modules => _modules;

    // Where installed locomotions live, beside the user's own: a slot of that name on the root.
    public const string ModulesRootName = "Locomotion Modules";
    private double _nextModuleLook;

    // Modules are kept in registration order (Walk, Noclip, ...); switching is
    // by list position, not priority (an ordered module list + active index).
    public void RegisterModule(LocomotionModule module)
    {
        if (module == null || _modules.Contains(module))
            return;
        _modules.Add(module);
    }

    public void UnregisterModule(LocomotionModule module)
    {
        if (module == null) return;
        if (_activeModule == module)
        {
            if (!_moduleSuspended)
                _activeModule.DeactivateInternal();
            _activeModule = null!;
        }
        _modules.Remove(module);
    }

    // INITIALIZATION

    public override void OnAwake()
    {
        base.OnAwake();

        // Don't run TryInitializeLocalUser() here: OnAwake executes inside the ReferenceController's
        // allocation soft-block, and the attaches it does (UserInputState + the locomotion modules) each
        // log a "RefID allocation during OnAwake" warning - dozens of lines per avatar. The first OnUpdate
        // calls it anyway (outside the block), one frame later, with no functional difference. Matches how
        // SessionJoinIndicator builds its children in OnInit rather than OnAwake. -xlinka
        LumoraLogger.Debug($"LocomotionController: OnAwake started");
    }

    private void CaptureMouse()
    {
        _mouseCaptured = true;
        _inputState?.SetMouseCaptureRequested(true);
    }

    // UPDATE

    internal void SuspendInput()
    {
        if (_moduleSuspended || _activeModule == null)
            return;
        using var bypass = World?.DataModelPermissions?.EnterSystemBypass();
        _activeModule.DeactivateInternal();
        _moduleSuspended = true;
        _characterController?.SetMovementDirection(float3.Zero);
        _characterController?.SetSimulationEnabled(false);
        _characterController?.SetCrouching(false);
        _characterController?.SetSprinting(false);
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Each open world has a local avatar, but only the focused one owns hardware input.
        // Suspend the module as well so a pending blink/turn cannot commit on returning here.
        if (World?.IsFocused != true)
        {
            SuspendInput();
            return;
        }

        // Late-bind InputInterface if it was not ready during Awake
        if (_inputInterface == null && Lumora.Core.Engine.Current?.InputInterface != null)
        {
            _inputInterface = Lumora.Core.Engine.Current.InputInterface;
            _mouse = _inputInterface.Mouse;
            _keyboardDriver = _inputInterface.GetKeyboardDriver();
        }

        if (!TryInitializeLocalUser())
            return;

        if (_characterController == null || !(_userRoot?.IsLocalUserRoot ?? false))
            return;

        // Everything below drives the LOCAL user's OWN avatar/root - head look, body turn, scale, and the
        // locomotion modules. That's engine movement of your own body, not a user edit, so bypass the data-model
        // permission gate: the root slot is host-allocated under the authority byte and ownership rests on the
        // User<->UserRoot link, which lags during join - without this the local writes are silently denied for a
        // window and you can't move/turn right after joining. -xlinka
        using var permBypass = World?.DataModelPermissions?.EnterSystemBypass();

        if (_moduleSuspended)
        {
            _moduleSuspended = false;
            ResumeCharacter();
            if (_activeModule is { IsDestroyed: false })
                _activeModule.ActivateInternal(this);
        }

        RefreshModules();
        EnsurePlatformModule();

        // Always handle mouse look (updates head rotation)
        HandleMouseLook(delta);

        // Apply head/body rotation before movement so basis uses latest look
        UpdateHead();

        HandleDesktopScaling();
        LocomotionInputHelper.UpdateSprintToggle(_inputInterface!, delta);
        UpdateSlowWalk();

        _activeModule?.OnModuleUpdate(delta);
        WatchHeldSticks(delta);
    }

    // Z toggles a slow walk at a quarter of the pace, and sprinting turns it back off. Kept here rather than
    // in a module so it carries over a switch between walking and flying.
    public const float SlowWalkPace = 0.25f;

    public bool SlowWalk { get; private set; }

    private bool _slowWalkHeld;

    private void UpdateSlowWalk()
    {
        bool held = LocomotionInputHelper.ReadSlowWalk(_inputInterface);
        if (held && !_slowWalkHeld)
            SlowWalk = !SlowWalk;
        _slowWalkHeld = held;
        if (SlowWalk && LocomotionInputHelper.ReadSprint(_inputInterface))
            SlowWalk = false;
    }

    // A stick pushed for a while into locomotion something is holding off: say who, once a hold. Something
    // that keeps its hold after it is gone then shows up by name instead of as "I can't turn". -xlinka
    private float _heldPush;
    private bool _heldReported;

    private void WatchHeldSticks(float delta)
    {
        var state = _inputState;
        if (state == null || _inputInterface == null || !(state.FreeCamActive || state.DesktopInputSuppressed))
        {
            _heldPush = 0f;
            _heldReported = false;
            return;
        }
        float turn = MathF.Abs(LocomotionInputHelper.ReadTurnAxis(_inputInterface));
        float move = LocomotionInputHelper.ReadMovementAxis(_inputInterface).Length;
        if (turn < 0.5f && move < 0.5f)
        {
            _heldPush = 0f;
            return;
        }
        _heldPush += delta;
        if (_heldPush < 1.5f || _heldReported)
            return;
        _heldReported = true;
        LumoraLogger.Log($"[Locomotion] stick pushed {_heldPush:0.0} s while locomotion is held off by: {state.DescribeHolds()}");
    }

    // Desktop user scaling: ctrl + scroll wheel. Crouch lives on C so ctrl
    // is free as the scale modifier.
    private const float MinUserScale = 0.05f;
    private const float MaxUserScale = 20f;

    // The engine's own sanity limits first, then the role's. Role bounds are only ever a NARROWING of
    // the engine range - a role configured wider than the engine allows does not get to widen it - so
    // the order matters: clamp to the engine range, hand that to the gate, and take what comes back.
    // An unbounded role is a no-op in there and gets the engine range unchanged. -xlinka
    public float ClampUserScale(float requested)
    {
        float scale = System.Math.Clamp(requested, MinUserScale, MaxUserScale);
        var permissions = World?.DataModelPermissions;
        return permissions == null ? scale : permissions.ClampScale(World!.LocalUser, scale);
    }

    private void HandleDesktopScaling()
    {
        if (_inputInterface == null || _inputInterface.IsVRActive)
            return;
        if (_inputInterface.IsDashboardOpen)
            return;
        if (!ScalingEnabled.Value)
            return;
        // A detached camera zooms on the same ctrl + wheel, and growing a body it is not looking from is no use.
        if (_inputState?.FreeCamActive == true)
            return;

        // The modifier is part of the binding now (stock: ctrl + wheel), so this is one read.
        float scroll = _inputInterface.Actions?.Locomotion.ScaleUser.Value ?? 0f;
        if (scroll == 0f)
            return;

        float factor = MathF.Pow(1.12f, scroll);
        // Feet held, so the scale and its undo land in the same spot.
        var feet = _userRoot.FeetPosition;
        _userRoot.GlobalScale = ClampUserScale(_userRoot.GlobalScale * factor);
        _userRoot.ShiftBy(feet - _userRoot.FeetPosition);
    }

    private bool TryInitializeLocalUser()
    {
        if (_initialized)
            return true;

        if (_userRoot == null)
        {
            _userRoot = Slot.GetComponent<UserRoot>();
            if (_userRoot == null)
            {
                if (!_loggedMissingUserRoot)
                {
                    LumoraLogger.Warn("LocomotionController: No UserRoot found!");
                    _loggedMissingUserRoot = true;
                }
                return false;
            }
        }

        if (!_loggedActiveUserState)
        {
            LumoraLogger.Debug($"LocomotionController: UserRoot.ActiveUser={_userRoot.ActiveUser?.UserName?.Value ?? "(null)"}, World.LocalUser={World?.LocalUser?.UserName?.Value ?? "(null)"}");
            _loggedActiveUserState = true;
        }

        if (!_userRoot.IsLocalUserRoot)
            return false;

        _characterController = Slot.GetComponent<CharacterController>();
        if (_characterController == null)
        {
            LumoraLogger.Warn("LocomotionController: No CharacterController found!");
            return false;
        }

        _inputState = Slot.GetComponent<UserInputState>() ?? Slot.AttachComponent<UserInputState>();
        _headDrive = Slot.GetComponent<DesktopHeadDrive>() ?? Slot.AttachComponent<DesktopHeadDrive>();

        _inputInterface = Lumora.Core.Engine.Current?.InputInterface!;
        if (_inputInterface != null)
        {
            _mouse = _inputInterface.Mouse;
            _keyboardDriver = _inputInterface.GetKeyboardDriver();
            LumoraLogger.Debug($"LocomotionController: Input bound - Mouse={_mouse != null}, Keyboard={_keyboardDriver != null}");
        }
        else
        {
            LumoraLogger.Warn("[LocomotionController] No InputInterface found - will try late binding");
        }

        EnsureDefaultModules();
        ActivateDefaultModule();

        if (!IsVRActive())
            _inputState.SetMouseCaptureRequested(true);

        LumoraLogger.Debug($"LocomotionController: Initialized for user '{_userRoot.ActiveUser?.UserName?.Value}' (VR={IsVRActive()}, Module={_activeModule?.DisplayName})");
        _initialized = true;
        return true;
    }

    // Make sure baseline modules are attached, then pull in anything else
    // already on the slot. PhysicalLocomotion is one module that handles both
    // stick and WASD via LocomotionInputHelper, so we no longer attach a
    // separate VR module or desktop module. - xlinka
    private void EnsureDefaultModules()
    {
        Slot.GetOrAttachComponent<PhysicalLocomotion>();
        // Attach order IS list order, and the default pick is the first usable entry - Walk has to stay
        // first or every user spawns into blink.
        Slot.GetOrAttachComponent<BlinkLocomotion>();
        Slot.GetOrAttachComponent<NoclipLocomotion>();
        Slot.GetOrAttachComponent<NullLocomotionModule>();

        _modules.Clear();
        CollectModules(_modules);
    }

    // The user's own locomotions, then every one installed under the modules slot, in order. Not the whole
    // root: an avatar hangs there too, and what it carries is installed or nothing.
    private void CollectModules(System.Collections.Generic.List<LocomotionModule> into)
    {
        foreach (var m in Slot.GetComponents<LocomotionModule>())
        {
            if (!m.IsDestroyed)
                into.Add(m);
        }
        if (Slot.FindChild(ModulesRootName) is { IsDestroyed: false } installed)
        {
            foreach (var m in installed.GetComponentsInChildren<LocomotionModule>())
            {
                if (!m.IsDestroyed && m.Enabled.Value)
                    into.Add(m);
            }
        }
    }

    // Installed ones come and go; looked for again once a second. The active one going falls back.
    private void RefreshModules()
    {
        double now = World?.Time.TotalTime ?? 0.0;
        if (now < _nextModuleLook)
            return;
        _nextModuleLook = now + 1.0;
        var found = new System.Collections.Generic.List<LocomotionModule>();
        CollectModules(found);
        if (found.Count == _modules.Count && found.TrueForAll(_modules.Contains))
            return;
        _modules.Clear();
        _modules.AddRange(found);
        if (_activeModule != null && (_activeModule.IsDestroyed || !_modules.Contains(_activeModule)))
        {
            _activeModule = null!;
            ActivateDefaultModule();
        }
    }

    // Copies a set of locomotions in under the modules slot, the old ones cleared first when asked, and says
    // how many came in. Only on the machine this user is at.
    public int Install(Slot templates, bool clearExisting)
    {
        var copy = InstallCopy(templates, clearExisting);
        int count = 0;
        if (copy != null)
        {
            foreach (var _ in copy.GetComponentsInChildren<LocomotionModule>())
                count++;
        }
        return count;
    }

    // The same, handing back the copy, so whoever installed it can take it away again.
    public Slot? InstallCopy(Slot templates, bool clearExisting)
    {
        if (templates == null || templates.IsDestroyed || !(_userRoot?.IsLocalUserRoot ?? false))
            return null;
        using var engine = World?.DataModelPermissions?.EnterSystemBypass();
        var root = Slot.FindChildOrAdd(ModulesRootName);
        if (clearExisting)
        {
            var old = new System.Collections.Generic.List<Slot>(root.Children);
            foreach (var child in old)
                child.Destroy();
        }
        var copy = templates.Duplicate(root);
        _nextModuleLook = 0;
        RefreshModules();
        return copy;
    }

    // Whether a module is both internally eligible (CanActivate) and permitted right now (world-mode
    // gate). Public so UI (the Settings locomotion picker) can grey out a denied module without
    // duplicating the permission check ActivateModule itself enforces.
    public bool IsModuleUsable(LocomotionModule module) => module != null && !module.IsDestroyed && module.CanActivate() && Permissions.CanUseLocomotion(module);

    // First module in list order that is usable. Mirrors the gate ActivateModule enforces, so a denied
    // module (e.g. noclip in a locked world) is never picked. -xlinka
    private LocomotionModule? FirstUsableModule()
    {
        for (int i = 0; i < _modules.Count; i++)
        {
            if (IsModuleUsable(_modules[i]))
                return _modules[i];
        }
        return null;
    }

    // A persisted preference (set from the Settings screen's locomotion picker) overrides the plain
    // first-usable pick when it still names a usable module by DisplayName - a stale name (renamed
    // module, or denied by the current world's permission gate) just falls through to the default so
    // spawn never strands the user on a mode it can't grant.
    private LocomotionModule? PreferredOrDefaultModule()
    {
        string preferred = EngineSettings.PreferredLocomotion;
        if (!string.IsNullOrEmpty(preferred))
        {
            for (int i = 0; i < _modules.Count; i++)
            {
                var module = _modules[i];
                if (string.Equals(module.DisplayName, preferred, StringComparison.OrdinalIgnoreCase) && IsModuleUsable(module))
                    return module;
            }
        }
        return FirstUsableModule();
    }

    // Spawn in the preferred module if one is set and still usable, otherwise the default
    // (first usable) module.
    private void ActivateDefaultModule()
    {
        var pick = PreferredOrDefaultModule();
        if (pick != null && pick != _activeModule)
            ActivateModule(pick);
    }

    // Keeps a usable module active: the selection is sticky, but if the current
    // module becomes ineligible (permission revoked, VR dropped) fall back to
    // the first usable one. Does not override an eligible user choice.
    private void EnsurePlatformModule()
    {
        // The active module is a sticky choice (menu selection / persisted),
        // never recomputed by priority. Only re-pick when the current module
        // becomes unusable (e.g. permission revoked), advancing to the first
        // usable one.
        if (_activeModule == null || !_activeModule.CanActivate())
        {
            var pick = FirstUsableModule();
            if (pick != null && pick != _activeModule)
                ActivateModule(pick);
        }

        if (!IsVRActive() && !_mouseCaptured && !_userReleased)
            CaptureMouse();
    }

    private bool IsVRActive()
    {
        if (_inputInterface == null)
            return false;

        // VR considered active when explicitly flagged and controllers/head tracked
        bool controllersTracked = _inputInterface.LeftController?.IsTracked == true || _inputInterface.RightController?.IsTracked == true;
        bool headTracked = _inputInterface.HeadDevice?.IsTracked == true;
        return _inputInterface.VR_Active && (controllersTracked || headTracked);
    }

    private void HandleMouseLook(float delta)
    {
        // Release/recapture the cursor. The action's own edge replaces the local was-pressed latch,
        // and because it lives in the locomotion set it stays quiet while the dashboard is up - the
        // same key closes the dash there instead of also unlocking the mouse behind it. With the dash
        // closed the default key opens it, so a press that also toggles the dash is the dash's. A
        // release stays until the same action takes the mouse back; the platform check above used to
        // recapture on the very next frame. -xlinka
        var actions = _inputInterface?.Actions;
        if (actions?.Locomotion.ToggleMouseCapture.Pressed == true && actions.Menu.ToggleDashboard.Pressed != true)
        {
            _mouseCaptured = !_mouseCaptured;
            _userReleased = !_mouseCaptured;
            _inputState?.SetMouseCaptureRequested(_mouseCaptured);
            Lumora.Core.Logging.Logger.Debug($"[LocomotionController] Mouse capture toggled: {_mouseCaptured}");
        }

        bool freeCam = _inputState?.FreeCamActive ?? false;
        bool lookSuppressed = _inputState?.MouseLookSuppressed ?? false;
        bool inputSuppressed = _inputState?.DesktopInputSuppressed ?? false;
        // Something pointing with the loose cursor (the context menu, the chat box) holds the view still.
        bool viewHeld = _inputState?.ViewHeld ?? false;

        if (freeCam || lookSuppressed || inputSuppressed || viewHeld)
            return;

        // The pad's look stick turns the view in the mouse's own units, under the same clamp, and whether or
        // not the mouse is captured: a stick has no cursor to set free. It reads zero in a headset.
        float2 look = float2.Zero;
        if (_mouse != null && _mouseCaptured)
            look += _mouse.DirectDelta.Value * MouseSensitivity;
        if (_inputInterface != null)
            look += _inputInterface.StickLook.Delta;
        if (look == float2.Zero)
            return;

        _yaw -= look.x;
        _pitch -= look.y;
        float maxPitchRad = MaxPitch * (float)System.Math.PI / 180f;
        _pitch = System.Math.Clamp(_pitch, -maxPitchRad, maxPitchRad);
    }

    // Genuinely in third person, which is NOT the same as "the mouse is not driving the head". The
    // context menu used to raise MouseLookSuppressed in first person too, and keying the third-person body
    // and head work off that flag turned the avatar to face the camera whenever a menu opened. Ask the
    // camera state instead: external camera, and not the free-cam. -xlinka
    private bool IsThirdPerson =>
        _inputState?.ExternalCameraActive == true && _inputState?.FreeCamActive != true;

    // THE HEAD
    //
    // The view and the head are two poses. First person: the head is the look. Third person: the mouse
    // drives the ORBIT and the head only follows it under the drive's neck rules, and the body heading
    // comes from the drive's face, which aligns to the orbit when you walk toward it and never while you
    // stand and look round. Free cam: the head holds still. The raw camera used to be written onto the
    // head here, which is how spinning the orbit snapped the avatar's neck through 180 degrees. -xlinka
    private void StepHeadDrive(float dt)
    {
        var drive = _headDrive;
        if (drive == null || drive.IsDestroyed)
        {
            drive = Slot.GetComponent<DesktopHeadDrive>() ?? Slot.AttachComponent<DesktopHeadDrive>();
            _headDrive = drive;
        }

        var mode = DesktopHeadDrive.ResolveMode(_inputState);
        if (mode != _viewMode)
        {
            // Leaving an external view: the head is the look again, so the body comes round under it
            // (the root rotate pins the head) and the mouse carries on from where the head was pointing
            // rather than where the body was.
            if (mode == DesktopViewMode.FirstPerson)
            {
                _yaw = drive.HeadYaw;
                _pitch = drive.HeadPitch;
            }
            _viewMode = mode;
        }
        else if (mode == DesktopViewMode.ThirdPerson && _yaw != drive.FaceYaw)
        {
            // Somebody else re-based the heading (a seat, a placement): the face starts from there.
            drive.Simulation.ResetFace(_yaw);
        }

        var frame = new DesktopHeadSimulation.Frame
        {
            Mode = mode,
            Delta = dt,
            LookYaw = _yaw,
            LookPitch = _pitch,
            BodyYaw = _yaw,
        };
        if (mode == DesktopViewMode.ThirdPerson)
        {
            var input = _inputInterface;
            if (input != null && input.DesktopCameraPoseValid)
                frame.ViewForward = input.DesktopCameraRotation * float3.Backward;
            frame.Movement = ReadWorldMovement(dt);
            frame.HandReady = TryGetHandAim(out frame.HandAimDirection);
        }
        drive.Step(frame);

        if (mode == DesktopViewMode.ThirdPerson)
        {
            _yaw = drive.FaceYaw;
            _pitch = drive.HeadPitch;
        }
    }

    // Metres the body is being pushed this frame, world space, from the same axis and basis the walk
    // module uses, so "moving toward the orbit" means what the feet are actually doing.
    private float3 ReadWorldMovement(float dt)
    {
        if (_inputInterface == null || _inputState == null)
            return float3.Zero;
        if (_inputState.FreeCamActive || _inputState.DesktopInputSuppressed || _inputState.MovementSuppressed)
            return float3.Zero;

        var axis = LocomotionInputHelper.ShapeMovement(LocomotionInputHelper.ReadMovementAxis(_inputInterface));
        if (axis.LengthSquared <= 1e-6f)
            return float3.Zero;

        GetMovementBasis(out var forward, out var right);
        float speed = (_characterController?.GetCurrentSpeed() ?? 0f) * (SlowWalk ? SlowWalkPace : 1f);
        return (forward * axis.y + right * axis.x) * (speed * dt);
    }

    // The pointing hand's flat direction from the head, while its laser is over something it can act on.
    private bool TryGetHandAim(out float3 direction)
    {
        direction = float3.Zero;
        var controller = ResolveControllerSlot(ref _rightControllerSlot, Input.BodyNode.RightController);
        var laser = ResolveRightLaser(controller);
        var head = _userRoot?.HeadSlot;
        if (laser == null || head == null || head.IsDestroyed || !laser.IsActive || !laser.HasAimPoint)
            return false;

        float3 toTarget = laser.AimPoint - head.GlobalPosition;
        toTarget.y = 0f;
        if (toTarget.LengthSquared <= 1e-6f)
            return false;
        direction = toTarget.Normalized;
        return true;
    }

    private const float POS_THRESHOLD_SQ = 0.0001f * 0.0001f;
    // Tiny - only skips a truly-still frame. The old 0.0001 gate was ~1.6 deg of rotation: the camera reads the
    // head slot, so any frame under the gate froze the look until the mouse motion piled up past it, then jumped.
    // That stepping IS the mouse-look jitter. Keep it microscopic so the local view tracks the mouse every frame. -xlinka
    private const float ROT_THRESHOLD = 0.0000001f;

    private TransformStreamDriver _rootStreamDriver = null!;

    // When a TransformStreamDriver shares the root slot, the Root stream is the transport for the root rotation,
    // so turn writes must be SILENT or the root would replicate over BOTH the stream and the delta channel.
    // Re-checks until found (the driver is attached during avatar build), then caches. -xlinka
    private bool RootIsStreamed()
    {
        if (_rootStreamDriver == null || _rootStreamDriver.IsDestroyed)
            _rootStreamDriver = Slot?.GetComponent<TransformStreamDriver>()!;
        return _rootStreamDriver != null && !_rootStreamDriver.IsDestroyed;
    }

    private void SetRootRotation(floatQ rotation)
    {
        if (RootIsStreamed())
            Slot.SetGlobalRotationSilently(rotation); // Root stream is the transport - don't also delta it.
        else
            Slot.GlobalRotation = rotation;
    }

    // Desktop look yaws the ROOT, and it has to pivot on the head exactly like a stick turn does - same
    // rotate, same anchor - so the head stays put and the hands and body come round with it. Writing the
    // rotation straight onto the root spins the rig about the root ORIGIN instead, which is only the same
    // point for as long as the head sits directly above it. -xlinka
    private void SetRootRotationAroundHead(floatQ rotation)
    {
        if (_userRoot == null)
        {
            SetRootRotation(rotation);
            return;
        }
        _userRoot.RotateAroundHead((rotation * Slot.GlobalRotation.Inverse).Normalized);
    }

    private void UpdateHead()
    {
        if (_userRoot?.HeadSlot == null)
            return;

        bool headTracked = _inputInterface?.HeadDevice?.IsTracked == true;
        if (headTracked)
            return;

        float userHeight = _inputInterface?.UserHeight ?? InputInterface.DEFAULT_USER_HEIGHT;
        // Crouch lowers the EYE POINT in root space (the capsule no longer sinks the root), which is
        // what makes the avatar actually squat: the head target drops while the floor stays put. -xlinka
        float crouchTarget = _characterController != null && _characterController.IsCrouching
            ? _characterController.CrouchHeight / System.MathF.Max(_characterController.StandingHeight, 0.1f)
            : 1f;
        float dt = World?.Time.Delta ?? (1f / 60f);
        _crouchBlend += (crouchTarget - _crouchBlend) * (1f - System.MathF.Exp(-10f * System.MathF.Max(dt, 1e-4f)));
        float headHeight = (userHeight - InputInterface.EYE_HEAD_OFFSET) * _crouchBlend;
        var newHeadPos = new float3(0, headHeight, 0);

        AdoptOutsideTurn();

        // The drive runs between mouse look and the body turn: it needs this frame's look, and in third
        // person the body turn below takes its heading from the drive's face.
        StepHeadDrive(dt);

        var newBodyRot = floatQ.FromEuler(new float3(0, _yaw, 0));
        var currentBodyRot = Slot.GlobalRotation;
        float bodyDot = floatQ.Dot(newBodyRot, currentBodyRot);
        if (1.0f - (bodyDot < 0 ? -bodyDot : bodyDot) > ROT_THRESHOLD)
            SetRootRotationAroundHead(newBodyRot);
        NoteRootTurn();

        // After the root has turned, so the head lands in the parent frame the root now has.
        _headDrive?.ApplyToHead(_userRoot.HeadSlot, newHeadPos);

        // Drive hand aim on the CONTROLLER slots (hands are grip-offset
        // children of controllers - writing the hand child would bypass the
        // tool/laser). The right hand pitches with the camera; both stay at
        // their resting height.
        //
        // The controller slot used to jump up to chest height whenever a context menu opened. Desktop arms
        // carry NO IK weight (there is no hand tracker to weigh), so the avatar's actual arm cannot follow
        // it - the hand and its laser slid ~30cm up the body on their own while the arm stayed hanging at
        // the side. That is the "hands moving up when laser selecting something" report. The hand stays
        // put now; aiming is TryAimHandAtLaserTarget's job, and it rotates in place.
        //
        // An equipped tool is the one case that DOES lift the hand, and it works because the missing half is
        // there now: HandTool drives the node to a hold pose in front of the head and AvatarIK weights the
        // arm to match, so the arm comes up with it instead of being left behind. That hand owns its node
        // outright while it holds, so hand the rest pose over rather than writing it - two components
        // writing one transform field alternate every frame and the hand jitters between them. -xlinka
        float restPitch = -MathF.PI / 2f;

        var rightController = ResolveControllerSlot(ref _rightControllerSlot, Input.BodyNode.RightController);
        if (rightController != null)
        {
            float aimPitch = MathF.Min(MathF.Max(restPitch + _pitch, -MathF.PI / 2f), 0f);
            var targetRot = floatQ.Euler(-MathF.PI / 2f, aimPitch, 0f);
            var restPos = new float3(0.25f, 1.0f, 0f);

            if (!OfferRestToHandTool(ref _rightHandTool, rightController, restPos, targetRot))
            {
                SetIfChanged(rightController.LocalPosition, restPos);
                if (!TryAimHandAtLaserTarget(rightController, dt))
                    SetIfChanged(rightController.LocalRotation, targetRot);
            }
        }

        var leftController = ResolveControllerSlot(ref _leftControllerSlot, Input.BodyNode.LeftController);
        if (leftController != null)
        {
            var restPos = new float3(-0.25f, 1.0f, 0f);
            var restRot = floatQ.Euler(MathF.PI / 2f, restPitch, 0f);

            if (!OfferRestToHandTool(ref _leftHandTool, leftController, restPos, restRot))
            {
                SetIfChanged(leftController.LocalPosition, restPos);
                SetIfChanged(leftController.LocalRotation, restRot);
            }
        }
    }

    private Interaction.HandTool? _leftHandTool;
    private Interaction.HandTool? _rightHandTool;

    // Give this controller's rest pose to the tool rig hanging off it. True means that rig is currently
    // driving the node (a tool held in view) and this component must keep its hands off it this frame.
    private static bool OfferRestToHandTool(ref Interaction.HandTool? cache, Slot controller,
        in float3 restPosition, in floatQ restRotation)
    {
        if (cache == null || cache.IsDestroyed || !ReferenceEquals(cache.Slot?.Parent, controller))
            cache = controller.GetComponentInChildren<Interaction.HandTool>(includeSelf: false);

        // A tool rig that has stopped updating cannot let go of the node on its own, and its last answer
        // would strand the hand wherever it was. Disabled means we take the node back. -xlinka
        if (cache == null || cache.IsDestroyed || !cache.Enabled.Value || cache.Slot?.IsActive != true)
            return false;

        return cache.OfferRestPose(restPosition, restRotation);
    }

    private Slot? _leftControllerSlot;
    private Slot? _rightControllerSlot;
    private Interaction.InteractionLaser? _rightLaserCache;

    // Rate the pointing hand eases onto the view's aim, in 1/s. Fast enough that the beam lands where you are
    // looking within a couple of frames, slow enough that a flick of the mouse doesn't snap the arm.
    private const float HandAimSmoothing = 10f;

    // Point the pointing hand at whatever the view is aimed at while an external camera is up (F5/F6). The
    // mouse drives the CAMERA in those modes and the head only follows it under the drive's neck rules, so
    // the mouse-pitch rest pose leaves the hand aimed where the head happens to be while the beam has to
    // leave it toward the crosshair's target. Take the rotation that carries the laser's own forward onto the hand->target direction and apply
    // it to the controller: the laser rides the controller rigidly, so that is exact in one step rather than a
    // frame-by-frame chase. Work in the parent's frame so body turn and walking don't read as aim error, then
    // ease in. Left hand keeps its rest pose, and VR never reaches here (UpdateHead bails on a tracked head).
    // -xlinka
    private bool TryAimHandAtLaserTarget(Slot controller, float delta)
    {
        if (_inputState?.ExternalCameraActive != true)
            return false;

        var parent = controller.Parent;
        if (parent == null)
            return false;

        var laser = ResolveRightLaser(controller);
        var laserSlot = laser?.Slot;
        if (laser == null || laserSlot == null || !laser.HasAimPoint)
            return false;

        float3 toTarget = laser.AimPoint - laserSlot.GlobalPosition;
        if (toTarget.LengthSquared <= 1e-6f)
            return false;

        float3 wanted = parent.GlobalDirectionToLocal(toTarget.Normalized);
        float3 current = parent.GlobalDirectionToLocal(laserSlot.GlobalRotation * float3.Backward);
        if (wanted.LengthSquared <= 1e-6f || current.LengthSquared <= 1e-6f)
            return false;

        floatQ aimed = Avatar.IK.FabrikSolver.FromToRotation(current.Normalized, wanted.Normalized)
            * controller.LocalRotation.Value;
        float t = System.Math.Clamp(1f - MathF.Exp(-HandAimSmoothing * MathF.Max(delta, 0f)), 0f, 1f);
        SetIfChanged(controller.LocalRotation, floatQ.Slerp(controller.LocalRotation.Value, aimed.Normalized, t));
        return true;
    }

    private Interaction.InteractionLaser? ResolveRightLaser(Slot? controller)
    {
        if (controller == null || controller.IsDestroyed)
            return null;
        if (_rightLaserCache == null || _rightLaserCache.IsDestroyed)
            _rightLaserCache = controller.GetComponentInChildren<Interaction.InteractionLaser>();
        return _rightLaserCache;
    }

    private Slot? ResolveControllerSlot(ref Slot? cache, Input.BodyNode node)
    {
        if (cache == null || cache.IsDestroyed)
            cache = _userRoot?.GetRegisteredComponent<TrackedDevicePositioner>(p => p.AutoBodyNode.Value == node)?.Slot;
        return cache;
    }

    private static void SetIfChanged(Sync<float3> field, in float3 value)
    {
        if ((field.Value - value).LengthSquared > POS_THRESHOLD_SQ)
            field.Value = value;
    }

    private static void SetIfChanged(Sync<floatQ> field, in floatQ value)
    {
        float dot = floatQ.Dot(field.Value, value);
        if (1.0f - (dot < 0 ? -dot : dot) > ROT_THRESHOLD)
            field.Value = value;
    }

    public void ApplySnapTurn(float deltaYaw)
    {
        _yaw += deltaYaw;

        if (_userRoot != null)
            _userRoot.RotateYawAroundHead(deltaYaw);
        else
            SetRootRotation((floatQ.AxisAngle(float3.Up, deltaYaw) * Slot.GlobalRotation).Normalized);
        NoteRootTurn();
    }

    // Take the heading the root is ACTUALLY carrying as the new accumulator value.
    //
    // Desktop look owns the root's yaw: UpdateHead writes _yaw onto it every frame. Anything that poses
    // the rig from outside - a seat turning you to face the way the chair faces, a scripted placement -
    // therefore lasts exactly one frame unless it lands here too, because the next update writes the old
    // yaw straight back over it. Snap turns keep composing on top afterwards, since they add to the same
    // accumulator. -xlinka
    public void SyncYawFromRoot()
    {
        _yaw = Slot.GlobalRotation.ToEuler().y;
        NoteRootTurn();
    }

    // A turn of the root that this controller did not write joins the look heading: the space a carrier hangs
    // the user from turning under them, a script or a seat facing them somewhere. Otherwise the next body write
    // would turn them straight back. Only the turn about up is taken; a screen user stays upright. -xlinka
    private floatQ _lastRootRotation = floatQ.Identity;
    private bool _rootTurnKnown;

    private void NoteRootTurn()
    {
        _lastRootRotation = Slot.GlobalRotation;
        _rootTurnKnown = true;
    }

    private void AdoptOutsideTurn()
    {
        var now = Slot.GlobalRotation;
        if (_rootTurnKnown)
        {
            float dot = floatQ.Dot(now, _lastRootRotation);
            if (1.0f - (dot < 0 ? -dot : dot) > ROT_THRESHOLD)
            {
                float turn = Heading(now) - Heading(_lastRootRotation);
                if (turn > MathF.PI)
                    turn -= 2f * MathF.PI;
                else if (turn < -MathF.PI)
                    turn += 2f * MathF.PI;
                if (float.IsFinite(turn))
                    _yaw += turn;
            }
        }
        _lastRootRotation = now;
        _rootTurnKnown = true;
    }

    // The angle about up of a rotation's forward (-Z), the same angle FromEuler takes for its yaw.
    private static float Heading(floatQ rotation)
    {
        var forward = rotation * float3.Backward;
        return MathF.Atan2(-forward.x, -forward.z);
    }

    // SPACES
    //
    // What the root hung from before each space it was carried into, the last entered last. A carrier hangs the
    // root from its space when the person steps in and asks for it back when they step out: the root goes back
    // to what it hung from before, or, when it has since moved somewhere else, that space is only forgotten, so
    // stepping off the stage while still in the cart keeps the cart. A space already held is not entered twice.
    // Local to this machine: only the owner moves the root, and its parent replicates like any other. -xlinka
    private readonly System.Collections.Generic.List<Slot> _spaces = new();

    public void EnterSpace(Slot space)
    {
        if (space == null || space.IsDestroyed || Slot == null || ReferenceEquals(Slot.Parent, space)
            || ReferenceEquals(space, Slot) || space.IsDescendantOf(Slot))
            return;
        for (int i = 0; i < _spaces.Count; i++)
        {
            if (ReferenceEquals(_spaces[i], space))
                return;
        }
        _spaces.Add(Slot.Parent ?? World.RootSlot);
        HangFrom(space);
    }

    public void LeaveSpace(Slot space)
    {
        if (space == null || Slot == null)
            return;
        if (!ReferenceEquals(Slot.Parent, space))
        {
            for (int i = _spaces.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_spaces[i], space))
                    _spaces.RemoveAt(i);
            }
            return;
        }
        Slot? back = null;
        while (back == null && _spaces.Count > 0)
        {
            var last = _spaces[_spaces.Count - 1];
            _spaces.RemoveAt(_spaces.Count - 1);
            if (last is { IsDestroyed: false })
                back = last;
        }
        HangFrom(back ?? World.RootSlot);
    }

    // The owner moving their own root, the way a step of the character does.
    private void HangFrom(Slot parent)
    {
        if (parent == null || parent.IsDestroyed)
            return;
        using (World?.DataModelPermissions?.EnterSystemBypass())
            Slot.SetParent(parent, preserveGlobalTransform: true);
    }

    public void GetMovementBasis(out float3 forward, out float3 right)
    {
        // Third person walks relative to the VIEW: W goes where the camera looks, and the body heading
        // (the drive's face) comes round to match on its own. The head follows the orbit only under neck
        // rules, so a basis off the head would walk you somewhere between where you look and where you
        // face. -xlinka
        if (IsThirdPerson && _inputInterface?.DesktopCameraPoseValid == true)
        {
            forward = _inputInterface.DesktopCameraRotation * float3.Backward;
        }
        else if (_userRoot != null && _userRoot.HeadSlot != null)
        {
            forward = _userRoot.HeadFacingDirection;
        }
        else if (_inputInterface?.GetBodyNode(Input.BodyNode.Head) is ITrackedDevice headDevice && headDevice.IsTracking)
        {
            forward = headDevice.Rotation * float3.Backward;
        }
        else
        {
            // Desktop fallback: use yaw
            floatQ yawRotation = floatQ.AxisAngle(float3.Up, _yaw);
            forward = yawRotation * float3.Backward;
        }

        // Flatten to horizontal plane (remove vertical component)
        forward.y = 0;
        if (forward.LengthSquared < 1e-4f)
            forward = new float3(0, 0, -1); // Default forward is -Z
        forward = forward.Normalized;

        // Right as perpendicular on horizontal plane (Forward x Up = Right in right-handed system)
        right = float3.Cross(forward, float3.Up);
        if (right.LengthSquared < 1e-4f)
            right = float3.Right;
        right = right.Normalized;

        // Set to follow the hand, the hand only steers forward. Strafe stays on the head, so a stick pushed
        // sideways still steps square to where you look whichever way the hand is turned. A hand pointing
        // at the floor has no heading left once flattened and gives the choice back to the head. -xlinka
        if (TryGetHandMoveDirection(out var handForward))
        {
            handForward.y = 0f;
            if (handForward.LengthSquared >= 1e-4f)
                forward = handForward.Normalized;
        }
    }

    // In VR with movement set to follow the hand: where the hand that drives Move points, not flattened, so
    // a flight can climb along it. The laser slot first (it rides the tool tip, unsmoothed), else the hand
    // slot. False leaves it to the head: desktop, third person, an untracked controller, or Move on no
    // hand's stick.
    public bool TryGetHandMoveDirection(out float3 direction)
    {
        direction = float3.Zero;
        if (EngineSettings.MovementDirection != MovementDirection.Hand || IsThirdPerson || !IsVRActive())
            return false;

        var side = ResolveMoveHand();
        if (side == Chirality.None)
            return false;

        var device = side == Chirality.Left ? _inputInterface.LeftController : _inputInterface.RightController;
        if (device?.IsTracked != true)
            return false;

        Slot? source = ResolveHandLaser(side)?.Slot;
        if (source == null || source.IsDestroyed)
            source = side == Chirality.Left ? _userRoot?.LeftHandSlot : _userRoot?.RightHandSlot;
        if (source == null || source.IsDestroyed)
            return false;

        direction = source.GlobalRotation * float3.Backward;
        if (direction.LengthSquared < 1e-6f)
            return false;
        direction = direction.Normalized;
        return true;
    }

    // The hand Move is bound to in VR, read off its bindings. Never picked by which stick is pushed hardest:
    // the right stick also turns and flies, and turning while walking must not hand the heading to the
    // other hand. Bound to both, it is the one whose raw stick reads what Move reads. -xlinka
    private Chirality ResolveMoveHand()
    {
        var actions = _inputInterface?.Actions;
        if (actions == null)
            return Chirality.None;

        var loco = actions.Locomotion;
        bool left = false;
        bool right = false;
        var bindings = loco.Move.Bindings;
        for (int i = 0; i < bindings.Count; i++)
        {
            var control = bindings[i].Control;
            if (control.Device != Input.Actions.InputDeviceKind.VRController)
                continue;
            if (control.Side == Chirality.Left)
                left = true;
            else if (control.Side == Chirality.Right)
                right = true;
        }

        if (left && right)
        {
            var move = loco.Move.Value;
            var fromLeft = loco.LeftStick.Value - move;
            var fromRight = loco.RightStick.Value - move;
            return fromLeft.LengthSquared <= fromRight.LengthSquared ? Chirality.Left : Chirality.Right;
        }
        return left ? Chirality.Left : right ? Chirality.Right : Chirality.None;
    }

    private Interaction.InteractionLaser? _leftLaserCache;

    private Interaction.InteractionLaser? ResolveHandLaser(Chirality side)
    {
        Interaction.InteractionLaser? laser;
        if (side == Chirality.Right)
        {
            laser = ResolveRightLaser(ResolveControllerSlot(ref _rightControllerSlot, Input.BodyNode.RightController));
        }
        else
        {
            var controller = ResolveControllerSlot(ref _leftControllerSlot, Input.BodyNode.LeftController);
            if (controller == null || controller.IsDestroyed)
                return null;
            if (_leftLaserCache == null || _leftLaserCache.IsDestroyed)
                _leftLaserCache = controller.GetComponentInChildren<Interaction.InteractionLaser>();
            laser = _leftLaserCache;
        }

        if (laser == null || laser.IsDestroyed || !laser.Enabled.Value || laser.ControllerSide.Value != side)
            return null;
        return laser;
    }

    // Locomotion permissions need the World (for the world's edit mode), which isn't set when the field
    // initializer runs - bind lazily the first time they're consulted. -xlinka
    private LocomotionPermissions Permissions => _permissions ??= new LocomotionPermissions(World);

    public void ActivateModule(LocomotionModule module)
    {
        if (module == null || !Permissions.CanUseLocomotion(module))
            return;
        if (ReferenceEquals(module, _activeModule))
            return;

        if (!_moduleSuspended)
            _activeModule?.DeactivateInternal();
        _activeModule = module;
        _moduleSuspended = World?.IsFocused != true;
        if (!_moduleSuspended)
            _activeModule.ActivateInternal(this);
        using (World?.DataModelPermissions?.EnterSystemBypass())
            CurrentModule.Target = module;
    }

    public CharacterController CharacterController => _characterController;

    // A seat parks the character on sit and only hands it back on release. Refocusing this world or a module
    // switching on must not put gravity back under a rig that is still parented to the chair: the user drops
    // out of the pose while the seat still counts them as seated. -xlinka
    public bool SeatHoldsCharacter => _inputState?.IsMovementSuppressedBy<Avatar.Seat>() == true;

    public void ResumeCharacter()
    {
        if (!SeatHoldsCharacter)
            _characterController?.SetSimulationEnabled(true);
    }

    // noclip flight bypasses the character controller and moves the rig directly
    public UserRoot UserRoot => _userRoot;

    // CLEANUP

    public override void OnDestroy()
    {
        if (_mouseCaptured)
        {
            _inputState?.SetMouseCaptureRequested(false);
            _mouseCaptured = false;
        }

        _inputState?.SetDesktopInputSuppressed(this, false);

        base.OnDestroy();
    }
}
