using Godot;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Aquamarine.Source.Input;
using System.Numerics;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;
using Vector2 = System.Numerics.Vector2;

namespace Aquamarine.Source.Godot.Input.Drivers;

/// <summary>
/// Godot VR driver for passing VR tracking data to Lumora engine.
/// Handles head-mounted display and controller tracking using OpenXR/SteamVR.
/// Implements VR input handling with body node system.
/// Uses XRController3D/XRCamera3D nodes for proper Godot 4.x OpenXR support.
/// </summary>
public class GodotVRDriver : IVRDriver, IInputDriver
{
    public int UpdateOrder => 0;

    // IVRDriver interface properties
    public bool IsVRActive => _xrInterface != null && _xrInterface.IsInitialized();
    public string VRSystemName => _xrInterface?.GetName() ?? "None";

    private XRInterface _xrInterface;
    private InputInterface _inputInterface;

    // Godot XR nodes for tracking (proper Godot 4.x pattern)
    private XRCamera3D _xrCamera;
    private XRController3D _leftController;
    private XRController3D _rightController;
    private XROrigin3D _xrOrigin;

    // Tracked devices for body node system
    private TrackedObject _headTrackedObject;
    private TrackedObject _leftControllerTrackedObject;
    private TrackedObject _rightControllerTrackedObject;
    private TrackedObject _leftHandTrackedObject;
    private TrackedObject _rightHandTrackedObject;

    /// <summary>
    /// Initialize the VR system.
    /// </summary>
    public void InitializeVR()
    {
        // Get XR interface (OpenXR, OpenVR, etc.)
        _xrInterface = XRServer.PrimaryInterface;

        if (_xrInterface != null && _xrInterface.IsInitialized())
        {
            global::Godot.GD.Print($"GodotVRDriver: VR initialized with interface: {_xrInterface.GetName()}");
        }
        else
        {
            global::Godot.GD.Print("GodotVRDriver: No VR interface available or not initialized");
        }
    }

    /// <summary>
    /// Find or create XR nodes in the scene tree for proper Godot 4.x tracking.
    /// XRController3D nodes MUST exist for Godot to track controllers via OpenXR.
    /// </summary>
    public void FindXRNodes(Node sceneRoot)
    {
        if (sceneRoot == null) return;

        _xrOrigin = FindNodeOfType<XROrigin3D>(sceneRoot);
        _xrCamera = FindNodeOfType<XRCamera3D>(sceneRoot);

        // Find controllers by tracker name
        foreach (var node in GetAllNodes(sceneRoot))
        {
            if (node is XRController3D controller)
            {
                var trackerStr = controller.Tracker.ToString();
                global::Godot.GD.Print($"GodotVRDriver: Found XRController3D '{controller.Name}' with tracker '{trackerStr}'");

                if (trackerStr.Contains("left") || trackerStr == "left_hand")
                    _leftController = controller;
                else if (trackerStr.Contains("right") || trackerStr == "right_hand")
                    _rightController = controller;
            }
        }

        // If XR nodes not found, create them - required for OpenXR controller tracking
        if (_xrOrigin == null || _leftController == null || _rightController == null)
        {
            global::Godot.GD.Print("GodotVRDriver: XR nodes not found, creating them...");
            CreateXRNodes(sceneRoot);
        }

        global::Godot.GD.Print($"GodotVRDriver: XR nodes ready - Origin:{_xrOrigin != null}, Camera:{_xrCamera != null}, Left:{_leftController != null}, Right:{_rightController != null}");
    }

    /// <summary>
    /// Create required XR nodes for OpenXR controller tracking.
    /// </summary>
    private void CreateXRNodes(Node sceneRoot)
    {
        // Create XROrigin3D if not found
        if (_xrOrigin == null)
        {
            _xrOrigin = new XROrigin3D();
            _xrOrigin.Name = "XROrigin3D";
            sceneRoot.AddChild(_xrOrigin);
            global::Godot.GD.Print("GodotVRDriver: Created XROrigin3D");
        }

        // Create XRCamera3D if not found
        if (_xrCamera == null)
        {
            _xrCamera = new XRCamera3D();
            _xrCamera.Name = "XRCamera3D";
            _xrOrigin.AddChild(_xrCamera);
            global::Godot.GD.Print("GodotVRDriver: Created XRCamera3D");
        }

        // Create left controller if not found
        if (_leftController == null)
        {
            _leftController = new XRController3D();
            _leftController.Name = "LeftController";
            _leftController.Tracker = new StringName("left_hand");
            _xrOrigin.AddChild(_leftController);
            global::Godot.GD.Print("GodotVRDriver: Created LeftController (left_hand)");
        }

        // Create right controller if not found
        if (_rightController == null)
        {
            _rightController = new XRController3D();
            _rightController.Name = "RightController";
            _rightController.Tracker = new StringName("right_hand");
            _xrOrigin.AddChild(_rightController);
            global::Godot.GD.Print("GodotVRDriver: Created RightController (right_hand)");
        }
    }

    private T FindNodeOfType<T>(Node node) where T : Node
    {
        if (node is T result)
            return result;
        foreach (var child in node.GetChildren())
        {
            var found = FindNodeOfType<T>(child);
            if (found != null)
                return found;
        }
        return null;
    }

    private System.Collections.Generic.IEnumerable<Node> GetAllNodes(Node node)
    {
        yield return node;
        foreach (var child in node.GetChildren())
        {
            foreach (var descendant in GetAllNodes(child))
                yield return descendant;
        }
    }

    /// <summary>
    /// Register inputs with the InputInterface.
    /// Creates TrackedObjects for each body node we track.
    /// </summary>
    public void RegisterInputs(InputInterface inputInterface)
    {
        _inputInterface = inputInterface;

        // Create TrackedObjects for VR devices
        _headTrackedObject = inputInterface.CreateDevice<TrackedObject>("VR_Head");
        _headTrackedObject.CorrespondingBodyNode = BodyNode.Head;
        _headTrackedObject.Priority = 100; // High priority for actual VR tracking

        _leftControllerTrackedObject = inputInterface.CreateDevice<TrackedObject>("VR_LeftController");
        _leftControllerTrackedObject.CorrespondingBodyNode = BodyNode.LeftController;
        _leftControllerTrackedObject.Priority = 100;

        _rightControllerTrackedObject = inputInterface.CreateDevice<TrackedObject>("VR_RightController");
        _rightControllerTrackedObject.CorrespondingBodyNode = BodyNode.RightController;
        _rightControllerTrackedObject.Priority = 100;

        // Create TrackedObjects for hands (same tracking as controllers for now)
        _leftHandTrackedObject = inputInterface.CreateDevice<TrackedObject>("VR_LeftHand");
        _leftHandTrackedObject.CorrespondingBodyNode = BodyNode.LeftHand;
        _leftHandTrackedObject.Priority = 50; // Lower priority than controller

        _rightHandTrackedObject = inputInterface.CreateDevice<TrackedObject>("VR_RightHand");
        _rightHandTrackedObject.CorrespondingBodyNode = BodyNode.RightHand;
        _rightHandTrackedObject.Priority = 50;

        Lumora.Core.Logging.Logger.Log($"GodotVRDriver: Registered VR tracked devices - Head:{_headTrackedObject?.Name}, LeftController:{_leftControllerTrackedObject?.Name}, RightController:{_rightControllerTrackedObject?.Name}");
    }

    private int _debugLogCounter = 0;

    /// <summary>
    /// Update inputs each frame.
    /// </summary>
    public void UpdateInputs(float deltaTime)
    {
        if (_inputInterface == null)
            return;

        // Update head tracking
        UpdateHeadTracking();

        // Update controller tracking (positions)
        UpdateControllerTracking(_leftControllerTrackedObject, _leftHandTrackedObject, Chirality.Left);
        UpdateControllerTracking(_rightControllerTrackedObject, _rightHandTrackedObject, Chirality.Right);

        // IMPORTANT: Update VRController inputs (thumbsticks, buttons, triggers)
        // This is separate from TrackedObject tracking
        if (_inputInterface.LeftController != null)
            UpdateControllerInputs(_inputInterface.LeftController);
        if (_inputInterface.RightController != null)
            UpdateControllerInputs(_inputInterface.RightController);

        // Debug log every 60 frames
        _debugLogCounter++;
        if (_debugLogCounter >= 60)
        {
            _debugLogCounter = 0;
            var leftPos = _leftControllerTrackedObject?.RawPosition ?? Lumora.Core.Math.float3.Zero;
            var rightPos = _rightControllerTrackedObject?.RawPosition ?? Lumora.Core.Math.float3.Zero;
            var leftTrack = _leftControllerTrackedObject?.IsTracking ?? false;
            var rightTrack = _rightControllerTrackedObject?.IsTracking ?? false;
            var leftThumb = _inputInterface.LeftController?.ThumbstickPosition ?? default;
            // global::Godot.GD.Print($"[VR] L:{leftPos} track:{leftTrack} | R:{rightPos} track:{rightTrack} | Thumb:({leftThumb.X:F2},{leftThumb.Y:F2}) | XRNodes L:{_leftController != null} R:{_rightController != null}");
        }
    }

    /// <summary>
    /// Update head tracking from HMD.
    /// Uses XRCamera3D if available, falls back to XRServer.GetHmdTransform().
    /// </summary>
    private void UpdateHeadTracking()
    {
        if (_headTrackedObject == null)
            return;

        bool isTracking = false;
        float3 position = new float3(0, 1.7f, 0);
        floatQ rotation = floatQ.Identity;

        // Try XRCamera3D first (proper Godot 4.x pattern)
        // Only use XR tracking if VR interface is actually active
        if (IsVRActive && _xrCamera != null && GodotObject.IsInstanceValid(_xrCamera))
        {
            // Get position relative to XROrigin (playspace-relative)
            var pos = _xrCamera.Position;
            var quat = _xrCamera.Quaternion;

            position = new float3(pos.X, pos.Y, pos.Z);
            rotation = new floatQ(quat.X, quat.Y, quat.Z, quat.W);
            isTracking = true;
        }
        // Fall back to XRServer
        else if (_xrInterface != null && _xrInterface.IsInitialized())
        {
            Transform3D headTransform = XRServer.GetHmdTransform();

            if (headTransform != Transform3D.Identity)
            {
                var pos = headTransform.Origin;
                position = new float3(pos.X, pos.Y, pos.Z);

                var basis = headTransform.Basis;
                var quat = basis.GetRotationQuaternion();
                rotation = new floatQ(quat.X, quat.Y, quat.Z, quat.W);
                isTracking = true;
            }
        }

        _headTrackedObject.RawPosition = position;
        _headTrackedObject.RawRotation = rotation;
        _headTrackedObject.IsTracking = isTracking;
        _headTrackedObject.IsDeviceActive = isTracking;
        _headTrackedObject.TrackingSpace = _inputInterface?.GlobalTrackingSpace;
    }

    /// <summary>
    /// Update controller tracking.
    /// Uses XRController3D if available, falls back to XRPositionalTracker.
    /// </summary>
    private void UpdateControllerTracking(TrackedObject controllerObj, TrackedObject handObj, Chirality side)
    {
        if (controllerObj == null)
            return;

        bool isTracking = false;
        float3 position = float3.Zero;
        floatQ rotation = floatQ.Identity;

        // Get the appropriate XRController3D
        var xrController = side == Chirality.Left ? _leftController : _rightController;

        // Try XRController3D first (proper Godot 4.x pattern)
        if (xrController != null && GodotObject.IsInstanceValid(xrController))
        {
            // Get position relative to XROrigin (playspace-relative)
            var pos = xrController.Position;
            var quat = xrController.Quaternion;

            position = new float3(pos.X, pos.Y, pos.Z);
            rotation = new floatQ(quat.X, quat.Y, quat.Z, quat.W);
            isTracking = xrController.GetIsActive();
        }
        // Fall back to XRPositionalTracker
        else if (_xrInterface != null && _xrInterface.IsInitialized())
        {
            string sideName = side == Chirality.Left ? "left" : "right";
            var trackerName = new StringName($"/user/hand/{sideName}");
            var tracker = XRServer.GetTracker(trackerName);

            if (tracker != null && tracker is XRPositionalTracker positionalTracker)
            {
                // Prefer grip pose for hand placement
                var gripPose = positionalTracker.GetPose(new StringName("grip"));
                var aimPose = positionalTracker.GetPose(new StringName("aim"));
                var defaultPose = positionalTracker.GetPose(new StringName("default"));
                var pose = gripPose.HasTrackingData ? gripPose : (aimPose.HasTrackingData ? aimPose : defaultPose);

                if (pose.HasTrackingData && pose.Transform != Transform3D.Identity)
                {
                    var transform = pose.Transform;
                    position = new float3(transform.Origin.X, transform.Origin.Y, transform.Origin.Z);

                    var basis = transform.Basis;
                    var quat = basis.GetRotationQuaternion();
                    rotation = new floatQ(quat.X, quat.Y, quat.Z, quat.W);
                    isTracking = true;
                }
            }
        }

        // Update controller tracked object
        controllerObj.RawPosition = position;
        controllerObj.RawRotation = rotation;
        controllerObj.IsTracking = isTracking;
        controllerObj.IsDeviceActive = isTracking;
        controllerObj.TrackingSpace = _inputInterface?.GlobalTrackingSpace;

        // Update hand tracked object (same position as controller for now)
        if (handObj != null)
        {
            handObj.RawPosition = position;
            handObj.RawRotation = rotation;
            handObj.IsTracking = isTracking;
            handObj.IsDeviceActive = isTracking;
            handObj.TrackingSpace = _inputInterface?.GlobalTrackingSpace;
        }
    }

    /// <summary>
    /// Update VR devices (legacy compatibility with old IVRDriver interface).
    /// </summary>
    public void UpdateVRDevices(VRController leftController, VRController rightController, HeadDevice headDevice)
    {
        // Update legacy devices for compatibility
        UpdateHeadDevice(headDevice);
        UpdateController(leftController, Chirality.Left);
        UpdateController(rightController, Chirality.Right);
    }

    private void UpdateHeadDevice(HeadDevice headDevice)
    {
        if (headDevice == null)
            return;

        if (_headTrackedObject != null)
        {
            headDevice.Position = new Vector3(_headTrackedObject.RawPosition.x, _headTrackedObject.RawPosition.y, _headTrackedObject.RawPosition.z);
            headDevice.Rotation = new Quaternion(_headTrackedObject.RawRotation.x, _headTrackedObject.RawRotation.y, _headTrackedObject.RawRotation.z, _headTrackedObject.RawRotation.w);
            headDevice.IsTracked = _headTrackedObject.IsTracking;
            headDevice.IsWorn = _headTrackedObject.IsTracking;
            headDevice.TrackingConfidence = _headTrackedObject.IsTracking ? 1.0f : 0f;
            headDevice.IsDeviceActive = _headTrackedObject.IsDeviceActive;
        }
    }

    private void UpdateController(VRController controller, Chirality side)
    {
        if (controller == null)
            return;

        var trackedObj = side == Chirality.Left ? _leftControllerTrackedObject : _rightControllerTrackedObject;
        if (trackedObj != null)
        {
            controller.Position = new Vector3(trackedObj.RawPosition.x, trackedObj.RawPosition.y, trackedObj.RawPosition.z);
            controller.Rotation = new Quaternion(trackedObj.RawRotation.x, trackedObj.RawRotation.y, trackedObj.RawRotation.z, trackedObj.RawRotation.w);
            controller.IsTracked = trackedObj.IsTracking;
            controller.IsDeviceActive = trackedObj.IsDeviceActive;
        }

        // Update controller inputs
        UpdateControllerInputs(controller);
    }

    private void UpdateControllerInputs(VRController controller)
    {
        // Get the appropriate XRController3D
        var xrController = controller.Side == VRControllerSide.Left ? _leftController : _rightController;
        string sideName = controller.Side == VRControllerSide.Left ? "left" : "right";

        global::Godot.Vector2 thumbstick = global::Godot.Vector2.Zero;
        float trigger = 0f;
        float grip = 0f;
        bool primaryButton = false;
        bool secondaryButton = false;

        // Method 1: Try XRController3D if available (requires action map bindings to work)
        if (xrController != null && GodotObject.IsInstanceValid(xrController))
        {
            thumbstick = xrController.GetVector2("primary");
            trigger = xrController.GetFloat("trigger");
            grip = xrController.GetFloat("grip");

            var axButton = xrController.Get("ax_button");
            primaryButton = axButton.VariantType == Variant.Type.Bool ? axButton.AsBool() :
                axButton.VariantType == Variant.Type.Float && axButton.AsSingle() > 0.5f;

            var byButton = xrController.Get("by_button");
            secondaryButton = byButton.VariantType == Variant.Type.Bool ? byButton.AsBool() :
                byButton.VariantType == Variant.Type.Float && byButton.AsSingle() > 0.5f;
        }

        // Method 2: Use Godot Input joypad mapping as fallback
        // OpenXR maps to joypad index 0 (left) and 1 (right)
        if (thumbstick == global::Godot.Vector2.Zero)
        {
            int joyId = controller.Side == VRControllerSide.Left ? 0 : 1;

            // Try to read from joypad
            float joyX = global::Godot.Input.GetJoyAxis(joyId, JoyAxis.LeftX);
            float joyY = global::Godot.Input.GetJoyAxis(joyId, JoyAxis.LeftY);
            if (joyX != 0 || joyY != 0)
                thumbstick = new global::Godot.Vector2(joyX, joyY); // No inversion - raw values

            if (trigger == 0f)
                trigger = global::Godot.Input.GetJoyAxis(joyId, JoyAxis.TriggerRight);
            if (grip == 0f)
                grip = global::Godot.Input.GetJoyAxis(joyId, JoyAxis.TriggerLeft);

            if (!primaryButton)
                primaryButton = global::Godot.Input.IsJoyButtonPressed(joyId, JoyButton.A);
            if (!secondaryButton)
                secondaryButton = global::Godot.Input.IsJoyButtonPressed(joyId, JoyButton.B);
        }

        // Apply values to controller
        controller.ThumbstickPosition = new Vector2(thumbstick.X, thumbstick.Y);
        controller.TriggerValue = trigger;
        controller.TriggerPressed = trigger > 0.5f;
        controller.GripValue = grip;
        controller.GripPressed = grip > 0.5f;
        controller.PrimaryButtonPressed = primaryButton;
        controller.SecondaryButtonPressed = secondaryButton;
    }

    /// <summary>
    /// Shutdown VR system.
    /// </summary>
    public void ShutdownVR()
    {
        if (_xrInterface != null)
        {
            _xrInterface = null;
        }

        global::Godot.GD.Print("GodotVRDriver: VR shutdown");
    }
}
