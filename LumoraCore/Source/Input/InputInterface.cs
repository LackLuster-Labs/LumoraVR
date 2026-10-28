// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Input.Actions;
using Lumora.Core.Logging;
using Lumora.Core.Math;

namespace Lumora.Core.Input;

public class InputInterface : IDisposable
{
    private class UpdateBucket
    {
        public readonly List<IInputDriver> InputDrivers = new List<IInputDriver>();
        public readonly int Order;

        public UpdateBucket(int order)
        {
            Order = order;
        }
    }

    public const float DEFAULT_USER_HEIGHT = 1.75f;
    public const float EYE_HEAD_OFFSET = 0.125f;

    private Lumora.Core.Engine _engine = null!;
    private List<IInputDriver> _inputDrivers = new List<IInputDriver>();
    private List<UpdateBucket> _inputDriverUpdateBuckets = new List<UpdateBucket>();
    private List<IInputDevice> _inputDevices = new List<IInputDevice>();
    private bool _initialized = false;

    private ITrackedDevice[] _bodyNodes = null!;

    // The inert stand-ins a node falls back to when nothing tracks it.
    private ITrackedDevice[] _bodyNodePlaceholders = null!;

    private List<IInputUpdateReceiver> _inputReceivers = new List<IInputUpdateReceiver>();
    // Membership beside the ordered list. Every dispatch used to ask List.Contains under the lock for
    // every receiver, twice a frame: square in the number of particle systems, bone chains, solvers and
    // pose drivers a world holds. The removal count lets a pass skip the question entirely unless
    // something actually left since its snapshot. -xlinka
    private readonly HashSet<IInputUpdateReceiver> _receiverSet = new(ReferenceEqualityComparer.Instance);
    private int _receiverRemovals;

    public TrackingSpace GlobalTrackingSpace { get; private set; } = new TrackingSpace();

    public Mouse Mouse { get; private set; } = null!;
    public Keyboard Keyboard { get; private set; } = null!;

    // Present whether or not a pad is plugged in; IsConnected says whether one actually is, so
    // bindings on it are inert rather than absent.
    public Gamepad Gamepad { get; private set; } = null!;

    // Everything above the driver layer reads THIS, never a key or a button: which control fires an action is a
    // user setting, not a fact about the code.
    public InputBindingMap Actions { get; private set; } = null!;

    // The pad's look stick as view turn for this step, and the cursor a pad moves over the dash. Both are
    // stepped right after the action pass and both stand still in a headset.
    public StickLook StickLook { get; } = new StickLook();
    public PadCursor PadCursor { get; } = new PadCursor();

    // VR devices (legacy compatibility)
    public VRController LeftController { get; private set; } = null!;
    public VRController RightController { get; private set; } = null!;
    public HeadDevice HeadDevice { get; private set; } = null!;
    // Face/lip tracking. A hardware hook fills this; null-object semantics until then (untracked, all zero).
    public MouthDevice MouthDevice { get; private set; } = null!;

    // The finger hands, registered by whichever driver has them (null until one does), and the one finger
    // pose source over both, in the hand space IHandPoseSource specifies.
    public HandDevice? LeftHand { get; private set; }
    public HandDevice? RightHand { get; private set; }
    public HandDevicePoseSource HandPoseSource { get; } = new HandDevicePoseSource();

    public HandDevice? GetHand(Chirality side)
        => side == Chirality.Left ? LeftHand : side == Chirality.Right ? RightHand : null;

    // VR state - synced from VR drivers automatically
    public bool VR_Active => IsVRActive;
    public float UserHeight { get; set; } = DEFAULT_USER_HEIGHT;

    public bool IsDashboardOpen { get; set; }

    // The platform's clipboard text service, assigned by the runner alongside the other platform
    // services. Null on a platform that has no clipboard (or before the runner has wired one), and
    // every caller has to cope with that rather than assume - the VR keyboard greys its Paste key out
    // on exactly this check. -xlinka
    public IClipboardText? ClipboardText { get; set; }

    // Null on a platform with no native file dialogs; see IFileDialogs.
    public IFileDialogs? FileDialogs { get; set; }

    // The platform's "what colour is the view showing there" service, assigned by the runner alongside
    // the other platform services. Null on a platform with no view (headless), which is why the
    // eyedropper treats a failed read as "no answer" rather than a bug. -xlinka
    public IViewColorSampler? ViewColorSampler { get; set; }

    // The platform's "give me a picture of what the view is showing" service, assigned by the runner
    // alongside the other platform services. Null on a platform with no view (headless), so a session
    // thumbnail or a saved world's sidecar is simply not written rather than the save failing. -xlinka
    public IViewCapture? ViewCapture { get; set; }

    // The last picture taken with nothing drawn over the world. On desktop the dash composites onto
    // the same viewport the capture reads, so a capture taken while it is open is a picture of the
    // dash, and both pictures people actually want (the session thumbnail, the sidecar beside a save)
    // used to come out that way because saving happens FROM the dash. Callers go through this: a fresh
    // read while the dash is closed, otherwise the newest clean one. Size is whatever the last clean
    // read asked for; both callers ask for the same 256x144. -xlinka
    private byte[]? _lastWorldView;
    private World? _lastWorldViewOwner;

    public bool TryCaptureWorldView(int width, int height, out byte[] jpeg)
    {
        jpeg = System.Array.Empty<byte>();
        var world = Engine.Current?.WorldManager?.FocusedWorld;
        if (!ReferenceEquals(world, _lastWorldViewOwner))
        {
            _lastWorldViewOwner = world;
            _lastWorldView = null;
        }
        var capture = ViewCapture;
        if (capture == null)
            return false;
        if (!IsDashboardOpen && capture.TryCapture(width, height, out var fresh) && fresh != null && fresh.Length > 0)
        {
            _lastWorldView = fresh;
            jpeg = fresh;
            return true;
        }
        if (_lastWorldView == null || _lastWorldView.Length == 0)
            return false;
        jpeg = _lastWorldView;
        return true;
    }

    // Desktop free-cursor ray, pushed by the platform layer each frame while the
    // OS cursor is unlocked (dash open). World space. The interaction laser uses
    // it as its cast ray so the in-world laser cursor and the mouse are one
    // pointer. Only the local platform layer may write it.
    public bool DesktopCursorRayValid { get; private set; }
    public float3 DesktopCursorRayOrigin { get; private set; }
    public float3 DesktopCursorRayDirection { get; private set; }

    public void SetDesktopCursorRay(bool valid, float3 origin, float3 direction)
    {
        DesktopCursorRayValid = valid;
        DesktopCursorRayOrigin = origin;
        DesktopCursorRayDirection = direction;
    }

    // Whatever wants the desktop cursor loose over the view instead of locked to its middle: an open context
    // menu, a camera that points with the cursor. Each owner puts itself in and takes itself out, and the cursor
    // is free while any is in. The platform shows the OS cursor and pushes the ray through it, and the lasers
    // cast along that ray. Owners write from their own world's thread, hence the lock. -xlinka
    private readonly HashSet<object> _cursorFreeOwners = new();

    public bool CursorFree
    {
        get
        {
            lock (_cursorFreeOwners)
            {
                if (_cursorFreeOwners.Count == 0)
                    return false;
            }
            return !IsVRActive;
        }
    }

    public void SetCursorFree(object owner, bool free)
    {
        lock (_cursorFreeOwners)
        {
            if (free)
                _cursorFreeOwners.Add(owner);
            else
                _cursorFreeOwners.Remove(owner);
        }
    }

    // Userspace-laser ownership, per controller side. The userspace dash pointer lives in the overlay
    // world and pushes its state here every frame. Each side is decided on its own: the userspace
    // pointer owns a hand while its laser is on something in userspace, and the world laser of that
    // hand then goes quiet, drops its hover and stops its beam at the userspace hit. A hand whose beam
    // misses the dash keeps its world laser.
    // Indexed by (int)Chirality (None=0, Left=1, Right=2). -xlinka
    private readonly bool[] _userspaceLaserActive = new bool[3];
    private readonly bool[] _userspaceLaserHasTarget = new bool[3];
    private readonly float[] _userspaceLaserHitDistance = { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };

    public void SetUserspaceLaserActive(Chirality side, bool active, bool hasTarget)
    {
        int i = (int)side;
        if (i < 0 || i >= _userspaceLaserActive.Length) return;
        _userspaceLaserActive[i] = active;
        _userspaceLaserHasTarget[i] = hasTarget;
    }

    // Distance along the hand ray to what the userspace laser hit this frame, +inf for nothing.
    public void SetUserspaceLaserHit(Chirality side, float distance)
    {
        int i = (int)side;
        if (i < 0 || i >= _userspaceLaserHitDistance.Length) return;
        _userspaceLaserHitDistance[i] = SaneDistance(distance);
    }

    private static float SaneDistance(float distance)
    {
        if (float.IsNaN(distance) || distance < 0f)
            return float.PositiveInfinity;
        return distance;
    }

    // A hand something has taken for its own buttons for a while, a viewfinder's or a camera's shutter: the
    // hand's tool and beam take no trigger or secondary until it lets go; the grip still holds. One claim per
    // hand; the owner that took it gives it back.
    private readonly object?[] _handClaims = new object?[3];

    public void ClaimHand(Chirality side, object owner)
    {
        int i = (int)side;
        if (i >= 0 && i < _handClaims.Length)
            _handClaims[i] = owner;
    }

    public void ReleaseHand(Chirality side, object owner)
    {
        int i = (int)side;
        if (i >= 0 && i < _handClaims.Length && ReferenceEquals(_handClaims[i], owner))
            _handClaims[i] = null;
    }

    public bool IsHandClaimed(Chirality side)
    {
        int i = (int)side;
        return i >= 0 && i < _handClaims.Length && _handClaims[i] != null;
    }

    public bool IsUserspaceLaserActive(Chirality side)
    {
        int i = (int)side;
        return i >= 0 && i < _userspaceLaserActive.Length && _userspaceLaserActive[i];
    }

    public bool UserspaceLaserHasTarget(Chirality side)
    {
        int i = (int)side;
        return i >= 0 && i < _userspaceLaserHasTarget.Length && _userspaceLaserHasTarget[i];
    }

    public float UserspaceLaserHitDistance(Chirality side)
    {
        int i = (int)side;
        return i >= 0 && i < _userspaceLaserHitDistance.Length ? _userspaceLaserHitDistance[i] : float.PositiveInfinity;
    }

    // True when the dash owns the pointer outright, for every hand: the desktop case, where the one
    // mouse cursor drives the dash and the in-world tools have nothing left to point with. In VR each
    // hand is arbitrated on its own through IsUserspaceLaserActive(side) inside the lasers, so this
    // stays false there and the hand tools keep running. -xlinka
    public bool IsAnyUserspaceLaserActive => !IsVRActive && (_userspaceLaserActive[1] || _userspaceLaserActive[2]);

    // Desktop camera projection info (vertical FOV in degrees, viewport aspect,
    // world-space camera pose), pushed alongside the cursor ray. The dash uses
    // it to fit its projected surface to the window. The camera looks along its
    // local -Z.
    public float DesktopCameraFovY { get; private set; } = 70f;
    public float DesktopViewportAspect { get; private set; } = 16f / 9f;
    public float3 DesktopCameraPosition { get; private set; }
    public floatQ DesktopCameraRotation { get; private set; } = floatQ.Identity;
    public bool DesktopCameraPoseValid { get; private set; }

    public void SetDesktopViewInfo(float fovYDegrees, float aspect, float3 cameraPosition, floatQ cameraRotation)
    {
        if (fovYDegrees > 1f)
            DesktopCameraFovY = fovYDegrees;
        if (aspect > 0.1f)
            DesktopViewportAspect = aspect;
        DesktopCameraPosition = cameraPosition;
        DesktopCameraRotation = cameraRotation;
        DesktopCameraPoseValid = true;
    }

    // True when the desktop camera is off the local user's head: driven by an OVERRIDE (3rd-person / free-cam)
    // or still fading back onto the head after one. The dashboard uses this to decide whether it may lock to
    // the live head pose (no override -> exact, zero-lag screen lock) or must fall back to the sampled camera
    // pose. -xlinka
    public bool DesktopCameraHasOverride { get; private set; }

    public void SetDesktopCameraOverride(bool hasOverride) => DesktopCameraHasOverride = hasOverride;

    // True while the desktop view is flown by a camera of its own (third-person orbit, free-cam, or on its
    // way back to the head after one) instead of riding the local head. Anything that aims with the view has
    // to come off the CAMERA in that state: the head is the avatar's simulated head there, which follows the
    // orbit only under neck rules and holds still in free cam. -xlinka
    public bool DesktopExternalCameraAim => !IsVRActive && DesktopCameraHasOverride && DesktopCameraPoseValid;

    // The desktop view is squared on a panel and points with the free cursor, as the dash does: the arrow is the
    // pointer there, and a beam drawn from the hand behind the view would only cut across the panel.
    public bool DesktopViewOnPanel { get; set; }

    // The free camera measures a carry from its lens instead of from the avatar's hand. -xlinka
    public bool DesktopFreeCamera { get; set; }

    // World-space aim ray through a point on the desktop view, built from the camera pose + projection the
    // platform pushes each frame - so it follows whichever camera is actually rendering.
    public bool TryGetDesktopViewRay(in float2 viewCoord, out float3 origin, out float3 direction)
    {
        origin = DesktopCameraPosition;
        direction = float3.Backward;
        if (!DesktopCameraPoseValid)
            return false;

        // Camera looks along its local -Z, so the image plane sits one unit down -Z and the half-extents are
        // the FOV tangent (vertical) and that times the aspect (horizontal).
        float tanHalfFov = MathF.Tan(DesktopCameraFovY * 0.5f * (MathF.PI / 180f));
        var local = new float3(
            viewCoord.x * tanHalfFov * DesktopViewportAspect,
            viewCoord.y * tanHalfFov,
            -1f);

        float3 world = DesktopCameraRotation * local;
        float length = world.Length;
        if (length <= 0.0001f)
            return false;

        direction = world / length;
        return true;
    }

    public float3 GlobalTrackingOffset { get; set; } = float3.Zero;
    public float3 CustomTrackingOffset { get; set; } = float3.Zero;

    // The window and screen sizes, as the platform layer reports them.
    public DisplayMetrics Display { get; } = new DisplayMetrics();

    // The VR driver whose runtime is running, for what it says about the hardware; null without VR.
    public IVRDriver? ActiveVRDriver => _vrDrivers.FirstOrDefault(d => d.IsVRActive);

    private IKeyboardDriver _keyboardDriver = null!;
    private IMouseDriver _mouseDriver = null!;
    private IGamepadDriver _gamepadDriver = null!;
    private List<IVRDriver> _vrDrivers = new List<IVRDriver>();
    private HashSet<string> _loggedBodyNodeAssignments = new HashSet<string>();

    public int InputDeviceCount => _inputDevices.Count;

    public bool IsVRActive => _vrDrivers.Any(d => d.IsVRActive);

    // Whether the headset has the XR session's focus, which a runtime takes away when the headset comes off
    // or its own menu covers the view. Reported by whatever runs the XR session; a runtime that never says
    // counts as on.
    public bool IsHeadsetFocused { get; private set; } = true;

    public void ReportHeadsetFocus(bool focused) => IsHeadsetFocused = focused;

    // Whether our own window holds the desktop's keyboard focus. Reported by the platform layer; one that
    // never says counts as focused, the safe answer for anything that types into other windows.
    public bool IsWindowFocused { get; private set; } = true;

    public void ReportWindowFocus(bool focused) => IsWindowFocused = focused;

    public HeadOutputDevice CurrentHeadOutputDevice
    {
        get
        {
            if (_vrDrivers.Any(d => d.IsVRActive))
                return HeadOutputDevice.VR;
            return HeadOutputDevice.Screen;  // Desktop mode
        }
    }

    // Bind devices to the focused user's tracking origin, including the equipped avatar's view offset.
    // The root moves during the frame (turn, walk, teleport) and every world-space device pose
    // read after that has to come out of where the rig actually is, not where it was at the frame boundary.
    // Still called at the frame edges so a world switch re-points it. -xlinka
    public bool SyncTrackingSpaceToFocusedLocalUser()
    {
        UserRoot root = _engine?.WorldManager?.FocusedWorld?.LocalUser?.Root!;
        if (root == null || root.IsDestroyed || root.Slot == null)
            return false;

        var space = root.TrackingSpaceSlot;
        if (!ReferenceEquals(GlobalTrackingSpace.Space, space))
            GlobalTrackingSpace.Space = space;
        return true;
    }

    public InputInterface()
    {
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;

        _engine = Engine.Current;

        InitializeBodyNodes();

        Keyboard = new Keyboard();
        RegisterInputDevice(Keyboard, "Keyboard");

        LeftController = new VRController(VRControllerSide.Left);
        RightController = new VRController(VRControllerSide.Right);
        HeadDevice = new HeadDevice();
        MouthDevice = new MouthDevice();

        // The gamepad device exists from the start. A pad plugged in mid-session flips IsConnected
        // and its bindings come alive; nothing has to be built or rebound at that moment.
        Gamepad = new Gamepad();
        RegisterInputDevice(Gamepad, "Gamepad");

        Actions = new InputBindingMap(this);
        LoadBindingOverrides();

        _initialized = true;

        await Task.CompletedTask;
        Logger.Log("InputInterface: Initialized successfully");
    }

    private void InitializeBodyNodes()
    {
        _bodyNodes = new ITrackedDevice[(int)BodyNode.END];
        _bodyNodePlaceholders = new ITrackedDevice[(int)BodyNode.END];

        foreach (BodyNode bodyNode in Enum.GetValues(typeof(BodyNode)))
        {
            if (bodyNode == BodyNode.NONE || bodyNode == BodyNode.END)
                continue;

            int index = (int)bodyNode;
            if (index >= 0 && index < _bodyNodes.Length)
            {
                var trackedObject = new TrackedObject();
                trackedObject.Initialize(this, index, bodyNode.ToString());
                trackedObject.CorrespondingBodyNode = bodyNode;
                trackedObject.IsDeviceActive = false;
                _bodyNodes[index] = trackedObject;
                _bodyNodePlaceholders[index] = trackedObject;
            }
        }
    }

    #region Body Node Access

    public ITrackedDevice GetBodyNode(BodyNode node)
    {
        int index = (int)node;
        if (index < 0 || index >= _bodyNodes.Length)
            return null!;
        return _bodyNodes[index];
    }

    // Only the placeholders get zeroed here. A driver's device says what its driver last wrote, and a
    // node whose device stopped tracking falls back to its placeholder instead of holding a dead
    // reference: zeroing whatever sat in the table used to switch every finger and palm device off one
    // frame after it was assigned, so no finger ever read as tracking past its first frame. -xlinka
    private void UpdateBodyNodeAssignments()
    {
        for (int index = 1; index < _bodyNodes.Length; index++)
        {
            var placeholder = _bodyNodePlaceholders[index];
            if (placeholder is TrackedObject inert)
            {
                inert.IsDeviceActive = false;
                inert.IsTracking = false;
            }

            var current = _bodyNodes[index];
            // A remapped or disabled puck that is still tracking must not keep the node it left.
            if (!ReferenceEquals(current, placeholder) &&
                (current == null || !current.IsDeviceActive || !current.IsTracking
                 || (int)current.CorrespondingBodyNode != index))
            {
                _bodyNodes[index] = placeholder;
            }
        }

        foreach (var device in _inputDevices)
        {
            if (device is ITrackedDevice trackedDevice &&
                trackedDevice.CorrespondingBodyNode != BodyNode.NONE &&
                trackedDevice.IsDeviceActive &&
                trackedDevice.IsTracking)
            {
                int index = (int)trackedDevice.CorrespondingBodyNode;
                if (index >= 0 && index < _bodyNodes.Length)
                {
                    var existing = _bodyNodes[index];
                    if (existing == null ||
                        !existing.IsDeviceActive ||
                        !existing.IsTracking ||
                        existing.Priority < trackedDevice.Priority)
                    {
                        _bodyNodes[index] = trackedDevice;
                        var logKey = $"{device.Name}_{trackedDevice.CorrespondingBodyNode}";
                        if (!_loggedBodyNodeAssignments.Contains(logKey))
                        {
                            _loggedBodyNodeAssignments.Add(logKey);
                            Logger.Debug($"InputInterface: Assigned device '{device.Name}' to body node {trackedDevice.CorrespondingBodyNode}");
                        }
                    }
                }
            }
        }

        UpdateBodyNodeFromVRDevice(HeadDevice, BodyNode.Head);
        UpdateBodyNodeFromVRDevice(LeftController, BodyNode.LeftController);
        UpdateBodyNodeFromVRDevice(RightController, BodyNode.RightController);
    }

    private void UpdateBodyNodeFromVRDevice(IInputDevice device, BodyNode node)
    {
        if (device == null || !device.IsDeviceActive)
            return;

        int index = (int)node;
        if (index < 0 || index >= _bodyNodes.Length)
            return;

        if (_bodyNodes[index] is TrackedObject trackedObj)
        {
            if (device is HeadDevice head)
            {
                trackedObj.RawPosition = new float3(head.Position.X, head.Position.Y, head.Position.Z);
                trackedObj.RawRotation = new floatQ(head.Rotation.X, head.Rotation.Y, head.Rotation.Z, head.Rotation.W);
                trackedObj.IsTracking = head.IsTracked;
                trackedObj.IsDeviceActive = true;
                trackedObj.TrackingSpace = GlobalTrackingSpace;
            }
            else if (device is VRController controller)
            {
                trackedObj.RawPosition = new float3(controller.Position.X, controller.Position.Y, controller.Position.Z);
                trackedObj.RawRotation = new floatQ(controller.Rotation.X, controller.Rotation.Y, controller.Rotation.Z, controller.Rotation.W);
                trackedObj.IsTracking = controller.IsTracked;
                trackedObj.IsDeviceActive = true;
                trackedObj.TrackingSpace = GlobalTrackingSpace;

                var handNode = node == BodyNode.LeftController ? BodyNode.LeftHand : BodyNode.RightHand;
                int handIndex = (int)handNode;
                if (handIndex >= 0 && handIndex < _bodyNodes.Length && _bodyNodes[handIndex] is TrackedObject handObj)
                {
                    handObj.RawPosition = trackedObj.RawPosition;
                    handObj.RawRotation = trackedObj.RawRotation;
                    handObj.IsTracking = trackedObj.IsTracking;
                    handObj.IsDeviceActive = true;
                    handObj.TrackingSpace = GlobalTrackingSpace;
                }
            }
        }
    }

    #endregion

    #region Input Update Receivers

    public void RegisterInputEventReceiver(IInputUpdateReceiver receiver)
    {
        lock (_pendingLock)
        {
            if (_receiverSet.Add(receiver))
                _inputReceivers.Add(receiver);
        }
    }

    public void UnregisterInputEventReceiver(IInputUpdateReceiver receiver)
    {
        lock (_pendingLock)
        {
            if (_receiverSet.Remove(receiver))
            {
                _inputReceivers.Remove(receiver);
                _receiverRemovals++;
            }
            ForgetPending(receiver);
        }
    }

    // Still registered? Only asked under the lock once a removal has happened since the caller looked.
    private bool StillRegistered(IInputUpdateReceiver receiver, int removalsSeen)
    {
        if (System.Threading.Volatile.Read(ref _receiverRemovals) == removalsSeen)
            return true;
        lock (_pendingLock)
            return _receiverSet.Contains(receiver);
    }

    #endregion

    #region Driver Registration

    public void RegisterInputDriver(IInputDriver driver)
    {
        _inputDrivers.Add(driver);
        driver.RegisterInputs(this);

        UpdateBucket bucket = _inputDriverUpdateBuckets.FirstOrDefault(b => b.Order == driver.UpdateOrder)!;
        if (bucket == null)
        {
            bucket = new UpdateBucket(driver.UpdateOrder);
            _inputDriverUpdateBuckets.Add(bucket);
            _inputDriverUpdateBuckets.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
        bucket.InputDrivers.Add(driver);

        Logger.Debug($"InputInterface: Registered driver with UpdateOrder {driver.UpdateOrder}");
    }

    public void RegisterKeyboardDriver(IKeyboardDriver keyboardDriver)
    {
        if (_keyboardDriver != null)
            throw new InvalidOperationException("Keyboard Driver is already registered");

        _keyboardDriver = keyboardDriver;
        Logger.Debug("InputInterface: Keyboard driver registered");
    }

    public void RegisterMouseDriver(IMouseDriver mouseDriver)
    {
        if (_mouseDriver != null)
            throw new InvalidOperationException("Mouse Driver already registered!");

        Mouse = new Mouse();
        RegisterInputDevice(Mouse, "Mouse");
        _mouseDriver = mouseDriver;

        Logger.Debug("InputInterface: Mouse driver registered");
    }

    // Optional: with no driver the Gamepad device never connects and every pad binding stays inert.
    public void RegisterGamepadDriver(IGamepadDriver gamepadDriver)
    {
        if (_gamepadDriver != null)
            throw new InvalidOperationException("Gamepad Driver already registered!");

        _gamepadDriver = gamepadDriver;
        Logger.Debug("InputInterface: Gamepad driver registered");
    }

    public IGamepadDriver GetGamepadDriver() => _gamepadDriver;

    // Raised after a device is registered and initialized. A tracker arriving is the case this exists
    // for: its saved mapping lives in local settings, so something has to put the two together at the
    // moment the hardware shows up rather than only when somebody re-calibrates. -xlinka
    public event Action<IInputDevice>? InputDeviceAdded;

    public void RegisterInputDevice(IInputDevice device, string name)
    {
        int deviceIndex = _inputDevices.Count;
        _inputDevices.Add(device);
        device.Initialize(this, deviceIndex, name);

        Logger.Debug($"InputInterface: Registered device '{name}' (index {deviceIndex})");

        // A tracker without its saved mapping is a puck that has forgotten it is a foot. Applied here so
        // it happens on EVERY connect, not just the one where the user happened to calibrate: the store
        // keeps mappings for absent trackers precisely so this can re-apply them.
        if (device is ITracker tracker)
        {
            try
            {
                if (TrackerMappingStore.TryApply(tracker))
                    Logger.Debug($"InputInterface: applied saved mapping to tracker '{name}'");
            }
            catch (Exception ex)
            {
                Logger.Error($"InputInterface: failed applying saved tracker mapping for '{name}': {ex.Message}");
            }
        }

        if (device is HandDevice hand)
        {
            if (hand.Side == Chirality.Left)
                LeftHand = hand;
            else if (hand.Side == Chirality.Right)
                RightHand = hand;
            HandPoseSource.Attach(hand);
        }

        InputDeviceAdded?.Invoke(device);
    }

    public T CreateDevice<T>(string name) where T : IInputDevice, new()
    {
        var device = new T();
        RegisterInputDevice(device, name);
        return device;
    }

    public void RegisterVRDriver(IVRDriver vrDriver)
    {
        if (!_vrDrivers.Contains(vrDriver))
        {
            _vrDrivers.Add(vrDriver);

            if (!_inputDevices.Contains(LeftController))
            {
                RegisterInputDevice(LeftController, "LeftController");
                RegisterInputDevice(RightController, "RightController");
                RegisterInputDevice(HeadDevice, "HeadDevice");
            }

            // If the VR driver also implements IInputDriver, register it to create TrackedObjects
            if (vrDriver is IInputDriver inputDriver)
            {
                RegisterInputDriver(inputDriver);
                Logger.Debug($"InputInterface: VR driver '{vrDriver.VRSystemName}' registered as IInputDriver");
            }
            else
            {
                Logger.Debug($"InputInterface: VR driver '{vrDriver.VRSystemName}' registered (legacy only)");
            }
        }
    }

    #endregion

    #region Update Loop

    public void ProcessInput(double deltaTime)
    {
        if (!_initialized)
            return;

        UpdateInputs((float)deltaTime);
    }

    // Per-receiver cost, keyed by component type.
    //
    // Every IInputUpdateReceiver - the particle sim, cloth, dynamic bones, both IK solvers, the pose
    // drivers - is dispatched from here and then QUEUED onto the world via RunSynchronously, which never
    // runs inline. So all of it actually executes inside World.Update's synchronous drain, landing in the
    // phase labelled "sync (transport poll, queued actions)". A world full of particles therefore showed
    // up in the profiler as a networking cost, and nothing anywhere broke it down by type. That is the
    // single biggest thing the frame profiler could not see. -xlinka
    private readonly Dictionary<Type, ReceiverCost> _receiverCosts = new();
    private readonly object _receiverCostLock = new();

    public static bool ProfileReceivers;

    private sealed class ReceiverCost
    {
        public double Ms;
        public int Count;
    }

    public readonly struct ReceiverProfileEntry
    {
        public readonly string Name;
        public readonly double Ms;
        public readonly int Count;
        public ReceiverProfileEntry(string name, double ms, int count) { Name = name; Ms = ms; Count = count; }
    }

    public void CollectReceiverProfile(List<ReceiverProfileEntry> into)
    {
        lock (_receiverCostLock)
        {
            foreach (var pair in _receiverCosts)
            {
                if (pair.Value.Count > 0)
                    into.Add(new ReceiverProfileEntry(pair.Key.Name + " (input)", pair.Value.Ms, pair.Value.Count));
            }
        }
    }

    // Zeroed when the callbacks are QUEUED and read after the world has drained them, which is the same
    // frame: Engine.Update runs ProcessInput before WorldManager.Update. The buckets stay, so a profiled
    // frame allocates nothing once every receiver type has been seen.
    private void ResetReceiverProfile()
    {
        lock (_receiverCostLock)
        {
            foreach (var bucket in _receiverCosts.Values)
            {
                bucket.Ms = 0;
                bucket.Count = 0;
            }
        }
    }

    private void RecordReceiverCost(IInputUpdateReceiver receiver, long ticks)
    {
        var type = receiver.GetType();
        double ms = ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        lock (_receiverCostLock)
        {
            if (!_receiverCosts.TryGetValue(type, out var bucket))
            {
                bucket = new ReceiverCost();
                _receiverCosts[type] = bucket;
            }
            bucket.Ms += ms;
            bucket.Count++;
        }
    }

    public void UpdateInputs(float deltaTime)
    {
        if (ProfileReceivers)
            ResetReceiverProfile();

        SyncTrackingSpaceToFocusedLocalUser();

        foreach (var bucket in _inputDriverUpdateBuckets)
        {
            foreach (var driver in bucket.InputDrivers)
            {
                driver.UpdateInputs(deltaTime);
            }
        }

        if (_mouseDriver != null && Mouse != null)
        {
            _mouseDriver.UpdateMouse(Mouse);
        }

        if (_keyboardDriver != null && Keyboard != null)
        {
            _keyboardDriver.UpdateKeyboard(Keyboard);
        }

        foreach (var vrDriver in _vrDrivers)
        {
            vrDriver.UpdateVRDevices(LeftController, RightController, HeadDevice);
        }

        if (_gamepadDriver != null && Gamepad != null)
        {
            _gamepadDriver.UpdateGamepad(Gamepad, deltaTime);
        }

        // Grip and trigger hysteresis, the trigger's deep edges and the menu charge step here, once,
        // after every driver has written raw values for the frame. The VR driver is registered on two
        // interfaces and writes twice a frame, so the edges cannot live in it. -xlinka
        LeftController?.Advance(deltaTime, RightController);
        RightController?.Advance(deltaTime, LeftController);

        UpdateBodyNodeAssignments();

        // Actions resolve AFTER every device has this frame's hardware and BEFORE anything reads
        // them, so a receiver and a component update in the same frame agree on what was pressed.
        UpdateActionGates();
        Actions?.Evaluate(deltaTime);
        Lumora.Core.Components.Interaction.DesktopAutoscroll.Step(this, deltaTime);
        StepPadLook(deltaTime);
        RespawnOnKey();

        // Call BeforeInputUpdate on all receivers (TrackedDevicePositioner updates slots here)
        DispatchInputReceivers(before: true);

        DispatchInputReceivers(before: false);
    }

    // The respawn key asks for the same fresh body the session screen's button does, in the world in focus. It
    // lives here and not on the body because a broken body is exactly when it is wanted. -xlinka
    private void RespawnOnKey()
    {
        if (Actions?.Locomotion.Respawn.Pressed != true)
            return;
        var world = Engine.Current?.WorldManager?.FocusedWorld;
        if (world == null || world.IsDestroyed || world.IsUserspace || world.LocalUser == null)
            return;
        world.RunSynchronously(world.RequestLocalRespawn);
    }

    // Decide which action sets are live this frame.
    // The dashboard asserting the menu set is the whole of "the dash stops you walking": locomotion
    // sits below it in priority, so it stops evaluating and every module underneath reads a resting
    // stick without knowing a panel is up.
    //
    // The per-user suppression requesters (free-cam, a tool holding the cursor, the radial menu)
    // stay where they are rather than becoming gates. They are finer-grained than a whole set -
    // the radial menu freezes MOUSE LOOK but deliberately leaves walking alive - and folding them
    // in here would flatten that distinction. -xlinka
    private void UpdateActionGates()
    {
        var actions = Actions;
        if (actions == null)
            return;

        // A focused text field owns the keyboard outright, and so does a field in an app out in the room.
        // Only the keyboard: a controller still works while somebody types.
        actions.TextFocusHeld = Helio.UI.TextInput.Focused != null || Lumora.Core.Components.UI.DashWindowPanel.TakesKeys
            || Lumora.Core.Components.UI.UserspaceDashboard.LocalInstance?.KeyScreen is Lumora.Core.Components.UI.IDashboardKeyInput { HasFieldFocus: true };

        actions.Menu.Set.Asserting = IsDashboardOpen;
    }

    private void StepPadLook(float deltaTime)
    {
        var actions = Actions;
        if (actions == null)
            return;
        bool screen = !IsVRActive;
        StickLook.Step(screen ? actions.Locomotion.Look.Value : float2.Zero, deltaTime, EngineSettings.StickLookSpeed, EngineSettings.InvertStickLook);

        var mouse = Mouse;
        bool moved = mouse != null && mouse.Position.Delta != float2.Zero;
        var at = new float2(0.5f, 0.5f);
        if (mouse != null && Display.Known)
        {
            var window = Display.Window;
            var origin = Display.WindowPosition;
            at = new float2((mouse.Position.Value.x - origin.x) / window.x, (mouse.Position.Value.y - origin.y) / window.y);
        }
        PadCursor.Step(screen && IsDashboardOpen, actions.Menu.Point.Value, deltaTime, DesktopViewportAspect, moved, at);
    }

    private void LoadBindingOverrides()
    {
        try
        {
            // A saved override moved on to this map's keys goes back to the store at once, so the old one
            // is read once and never again.
            if (InputBindingStore.Apply(Actions, Settings.ReadValue<string>(InputBindingStore.SettingsKey, string.Empty)))
                SaveBindingOverrides();
        }
        catch (Exception ex)
        {
            Logger.Warn($"InputInterface: failed to load control bindings, using defaults: {ex.Message}");
        }
    }

    // The store's bindings again from scratch, for when the stored overrides were replaced underneath the
    // map (a settings file brought back in). Stock first, so an action the new set leaves alone is not left
    // holding the old override.
    public void ReloadBindingOverrides()
    {
        if (Actions == null)
            return;
        Actions.ResetAllToDefaults();
        LoadBindingOverrides();
    }

    // Write the current bindings to disk now.
    // Bindings save on change rather than on exit like the rest of the settings. A rebind you cannot
    // undo because you rebound the key that reaches the menu is a trap, and "preview until you quit"
    // is the wrong model for the thing you press to quit. -xlinka
    public void SaveBindingOverrides()
    {
        try
        {
            if (Actions != null)
                Settings.WriteValue(InputBindingStore.SettingsKey, InputBindingStore.Serialize(Actions));
        }
        catch (Exception ex)
        {
            Logger.Warn($"InputInterface: failed to save control bindings: {ex.Message}");
        }
    }

    private void DispatchInputReceivers(bool before)
    {
        // Receivers can unregister or be destroyed as part of world teardown.
        // Iterate a snapshot and prune stale world elements before invoking them.
        //
        // Dispatch in component update order, not registration order: device
        // positioners (-1000000) must write body-node slots before pose nodes
        // (-7500) consume them, and nested pose nodes rely on parent-before-
        // child ordering (EnsureCorrectUpdateOrder) - registration order made
        // both a matter of luck, costing a frame of lag per misordered link.
        var snapshot = _dispatching ? new List<IInputUpdateReceiver>() : _dispatchSnapshot;
        bool outermost = !_dispatching;
        _dispatching = true;
        try
        {
            int removals;
            lock (_pendingLock)
            {
                snapshot.Clear();
                snapshot.AddRange(_inputReceivers);
                removals = _receiverRemovals;
            }
            snapshot.Sort(ByUpdateOrder);
            foreach (var receiver in snapshot)
            {
                if (!StillRegistered(receiver, removals))
                    continue;
                if (ShouldPruneInputReceiver(receiver))
                {
                    UnregisterInputEventReceiver(receiver);
                    continue;
                }

                InvokeInputReceiver(receiver, before);

                if (ShouldPruneInputReceiver(receiver))
                {
                    UnregisterInputEventReceiver(receiver);
                }
            }
            QueueFilledBatches();
        }
        finally
        {
            if (outermost)
            {
                snapshot.Clear();
                _dispatching = false;
            }
        }
    }

    private static readonly Comparison<IInputUpdateReceiver> ByUpdateOrder = (a, b) =>
    {
        int orderA = (a as Component)?.UpdateOrder ?? 0;
        int orderB = (b as Component)?.UpdateOrder ?? 0;
        return orderA.CompareTo(orderB);
    };

    private readonly List<IInputUpdateReceiver> _dispatchSnapshot = new();
    private bool _dispatching;

    private static bool ShouldPruneInputReceiver(IInputUpdateReceiver receiver)
    {
        if (receiver == null)
            return true;

        if (receiver is IWorldElement worldElement)
        {
            var world = worldElement.World;
            return worldElement.IsDestroyed ||
                   world == null ||
                   world.IsDisposed ||
                   world.IsDestroyed ||
                   world.ReferenceController == null;
        }

        return false;
    }

    // The engine sampling local hardware to pose the LOCAL user's OWN avatar/body is engine maintenance,
    // not a permission-gated edit - a user always controls their own body. On a freshly-joined client the
    // user's allocation + UserRoot ownership isn't wired the instant the body is built during state
    // replay, so without this the own-body writes (Head/hand transforms, body-node slots) get denied
    // every single frame and flood the log. Bypass perms for the duration of the local input callback;
    // remote users' bodies arrive over the network and are validated separately on the host. -xlinka
    private void InvokeNow(IInputUpdateReceiver receiver, bool before)
    {
        var permissions = (receiver as IWorldElement)?.World?.DataModelPermissions;
        using var bypass = permissions?.EnterSystemBypass();
        InvokeCallback(receiver, before);
    }

    private void InvokeCallback(IInputUpdateReceiver receiver, bool before)
    {
        try
        {
            if (ProfileReceivers)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                if (before)
                    receiver.BeforeInputUpdate();
                else
                    receiver.AfterInputUpdate();
                RecordReceiverCost(receiver, System.Diagnostics.Stopwatch.GetTimestamp() - start);
            }
            else if (before)
            {
                receiver.BeforeInputUpdate();
            }
            else
            {
                receiver.AfterInputUpdate();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"InputInterface: Error in {(before ? "BeforeInputUpdate" : "AfterInputUpdate")}: {ex.Message}");
        }
    }

    private void InvokeInputReceiver(IInputUpdateReceiver receiver, bool before)
    {
        // World components modify Sync state in these callbacks.
        // Route through world sync queue while running so writes happen under the world's
        // implementer lock, avoiding cross-thread lock violations.
        if (receiver is IWorldElement worldElement &&
            worldElement.World != null &&
            !worldElement.World.IsDisposed &&
            !worldElement.World.IsDestroyed &&
            worldElement.World.State == World.WorldState.Running)
        {
            var world = worldElement.World;
            var batches = before ? _beforeBatches : _afterBatches;
            lock (_pendingLock)
            {
                if (!batches.TryGetValue(world, out var batch))
                {
                    batch = new ReceiverBatch(this, world, before);
                    batches[world] = batch;
                }
                // Still waiting from an earlier frame: it runs once, with what it already holds.
                if (batch.Queued)
                    return;
                if (!batch.Filled)
                {
                    batch.Filled = true;
                    _filledBatches.Add(batch);
                }
                if (batch.Members.Add(receiver))
                    batch.Receivers.Add(receiver);
            }
            return;
        }

        InvokeNow(receiver, before);
    }

    // One queued call per world and phase, running that world's receivers in update order under one
    // permission bypass. A world that updates less often than input runs, a background world at 10 Hz,
    // otherwise had every frame's call waiting and ran them all in one update: a soft body in a world nobody
    // was in stepped eleven times at once, and two open Scratch Spaces halved the frame rate. A closure per
    // receiver per phase was also three allocations each, sixty-odd a frame in an ordinary world. -xlinka
    private sealed class ReceiverBatch
    {
        public readonly InputInterface Owner;
        public readonly World World;
        public readonly bool Before;
        public readonly Action Run;
        public List<IInputUpdateReceiver> Receivers = new();
        public List<IInputUpdateReceiver> Running = new();
        public readonly HashSet<IInputUpdateReceiver> Members = new(ReferenceEqualityComparer.Instance);
        public bool Filled;
        public bool Queued;

        public ReceiverBatch(InputInterface owner, World world, bool before)
        {
            Owner = owner;
            World = world;
            Before = before;
            Run = Execute;
        }

        private void Execute() => Owner.RunBatch(this);
    }

    private readonly Dictionary<World, ReceiverBatch> _beforeBatches = new();
    private readonly Dictionary<World, ReceiverBatch> _afterBatches = new();
    private readonly List<ReceiverBatch> _filledBatches = new();
    private readonly List<ReceiverBatch> _queueing = new();
    private readonly List<World> _goneWorlds = new();
    private readonly object _pendingLock = new();

    private void QueueFilledBatches()
    {
        lock (_pendingLock)
        {
            foreach (var batch in _filledBatches)
            {
                batch.Filled = false;
                batch.Queued = true;
            }
            _queueing.AddRange(_filledBatches);
            _filledBatches.Clear();
            DropGoneWorlds(_beforeBatches);
            DropGoneWorlds(_afterBatches);
        }
        foreach (var batch in _queueing)
            batch.World.RunSynchronously(batch.Run);
        _queueing.Clear();
    }

    // A world torn down with its batch still queued never drains it.
    private void DropGoneWorlds(Dictionary<World, ReceiverBatch> batches)
    {
        foreach (var world in batches.Keys)
        {
            if (world.IsDisposed || world.IsDestroyed)
                _goneWorlds.Add(world);
        }
        foreach (var world in _goneWorlds)
            batches.Remove(world);
        _goneWorlds.Clear();
    }

    private void RunBatch(ReceiverBatch batch)
    {
        // Swapped out under the lock, so the next dispatch fills a list nothing is reading.
        int removals;
        lock (_pendingLock)
        {
            batch.Queued = false;
            (batch.Running, batch.Receivers) = (batch.Receivers, batch.Running);
            batch.Members.Clear();
            removals = _receiverRemovals;
        }
        try
        {
            if (batch.World.IsDisposed || batch.World.IsDestroyed)
                return;
            using var bypass = batch.World.DataModelPermissions?.EnterSystemBypass();
            foreach (var receiver in batch.Running)
            {
                // Unregistering during an earlier callback also cancels the rest of this batch. Never
                // hold the registry lock while executing world code.
                if (!StillRegistered(receiver, removals) || ShouldPruneInputReceiver(receiver))
                    continue;
                InvokeCallback(receiver, batch.Before);
            }
        }
        finally
        {
            batch.Running.Clear();
        }
    }

    // A receiver going away is left out of any batch that has not started running.
    private void ForgetPending(IInputUpdateReceiver receiver)
    {
        lock (_pendingLock)
        {
            foreach (var batch in _beforeBatches.Values)
            {
                if (batch.Members.Remove(receiver))
                    batch.Receivers.Remove(receiver);
            }
            foreach (var batch in _afterBatches.Values)
            {
                if (batch.Members.Remove(receiver))
                    batch.Receivers.Remove(receiver);
            }
        }
    }

    #endregion

    #region Device Access

    public IInputDevice GetDevice(int index)
    {
        if (index < 0 || index >= _inputDevices.Count)
            return null!;
        return _inputDevices[index];
    }

    public T GetDevice<T>(string name) where T : class, IInputDevice
    {
        return (_inputDevices.FirstOrDefault(d => d.Name == name) as T) ?? null!;
    }

    public T GetDevice<T>(Predicate<T> predicate = null!) where T : class, IInputDevice
    {
        foreach (var device in _inputDevices)
        {
            if (device is T typedDevice && (predicate == null || predicate(typedDevice)))
            {
                return typedDevice;
            }
        }
        return null!;
    }

    public void GetDevices<T>(List<T> list, Predicate<T> predicate = null!) where T : class, IInputDevice
    {
        foreach (var device in _inputDevices)
        {
            if (device is T typedDevice && (predicate == null || predicate(typedDevice)))
            {
                list.Add(typedDevice);
            }
        }
    }

    public IKeyboardDriver GetKeyboardDriver() => _keyboardDriver;
    public IMouseDriver GetMouseDriver() => _mouseDriver;

    #endregion

    #region Disposal

    public void Dispose()
    {
        if (!_initialized)
            return;

        lock (_pendingLock)
        {
            _inputReceivers.Clear();
            _receiverSet.Clear();
            _receiverRemovals++;
            _beforeBatches.Clear();
            _afterBatches.Clear();
            _filledBatches.Clear();
        }

        _inputDrivers.Clear();
        _inputDriverUpdateBuckets.Clear();
        _vrDrivers.Clear();

        _inputDevices.Clear();

        _bodyNodes = null!;
        _bodyNodePlaceholders = null!;
        LeftHand = null;
        RightHand = null;

        _keyboardDriver = null!;
        _mouseDriver = null!;
        _gamepadDriver = null!;
        ClipboardText = null;
        FileDialogs = null;
        ViewColorSampler = null;
        ViewCapture = null;
        Actions = null!;
        Mouse = null!;
        Keyboard = null!;
        Gamepad = null!;
        LeftController = null!;
        RightController = null!;
        HeadDevice = null!;
        MouthDevice = null!;

        _initialized = false;
        Logger.Log("InputInterface: Disposed");
    }

    #endregion
}
