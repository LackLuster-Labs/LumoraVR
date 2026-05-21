// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Source.Input;
using System.Numerics;
using System;
using System.Collections.Generic;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;
using Vector2 = System.Numerics.Vector2;

namespace Lumora.Source.Godot.Input.Drivers;

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
    public VRPlatform Platform => DetectPlatform();
    public string RuntimeName => GetRuntimeName();
    public string LeftInteractionProfile => GetTrackerInteractionProfile("left_hand");
    public string RightInteractionProfile => GetTrackerInteractionProfile("right_hand");

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
    private readonly Dictionary<BodyNode, TrackedObject> _handSkeletonTrackedObjects = new();

    private static readonly FingerType[] FingerOrder = new[]
    {
        FingerType.Thumb,
        FingerType.Index,
        FingerType.Middle,
        FingerType.Ring,
        FingerType.Pinky
    };

    private static readonly FingerSegmentType[] ThumbSegments = new[]
    {
        FingerSegmentType.Metacarpal,
        FingerSegmentType.Proximal,
        FingerSegmentType.Distal,
        FingerSegmentType.Tip
    };

    private static readonly FingerSegmentType[] FingerSegments = new[]
    {
        FingerSegmentType.Metacarpal,
        FingerSegmentType.Proximal,
        FingerSegmentType.Intermediate,
        FingerSegmentType.Distal,
        FingerSegmentType.Tip
    };

    /// <summary>
    /// Pick up the XR interface that PhaseXRDetection() already initialized.
    /// Also subscribes to TrackerAdded to log the HMD device name on connect.
    /// </summary>
    public void InitializeVR()
    {
        _xrInterface = XRServer.PrimaryInterface;

        if (_xrInterface != null && _xrInterface.IsInitialized())
        {
            GD.Print($"GodotVRDriver: VR active - interface: {_xrInterface.GetName()}");

            // Log the resolved platform + runtime + active interaction profile
            // so it's obvious at boot whether we're on Quest, Pico, SteamVR PC,
            // etc. Useful for gameplay code that branches on Platform, and for
            // remote debugging on standalone where the only log surface is adb.
            // - xlinka
            GD.Print($"GodotVRDriver: Platform={Platform}, Runtime='{RuntimeName}', " +
                     $"LeftProfile='{LeftInteractionProfile}', RightProfile='{RightInteractionProfile}'");

            XRServer.TrackerAdded += OnTrackerAdded;
        }
        else
        {
            GD.Print("GodotVRDriver: No VR interface active - running in desktop mode");
        }
    }

    private void OnTrackerAdded(StringName trackerName, long type)
    {
        var tracker = XRServer.GetTracker(trackerName);
        if (tracker == null) return;

        string desc = tracker is XRPositionalTracker pt ? pt.Description : tracker.Name.ToString();
        global::Godot.GD.Print($"GodotVRDriver: Tracker connected [{(XRServer.TrackerType)type}] '{trackerName}' — {desc}");

        if ((XRServer.TrackerType)type == XRServer.TrackerType.Head)
            global::Godot.GD.Print($"GodotVRDriver: HMD device: {desc}");
    }

    // ===== Platform / interaction profile introspection =====
    //
    // OpenXRInterface exposes `runtime_name` as a registered Godot property,
    // and XRPositionalTracker exposes the active OpenXR interaction profile
    // path via `Profile`. We go through Get() so this stays compatible across
    // minor Godot versions that may not expose the same C# bindings.
    // - xlinka

    private string GetRuntimeName()
    {
        if (_xrInterface == null) return string.Empty;
        var v = _xrInterface.Get("runtime_name");
        return v.AsString() ?? string.Empty;
    }

    private static string GetTrackerInteractionProfile(string trackerName)
    {
        var tracker = XRServer.GetTracker(trackerName);
        if (tracker is not XRPositionalTracker pt) return string.Empty;
        var profile = pt.Profile;
        return string.IsNullOrEmpty(profile) ? string.Empty : profile;
    }

    private VRPlatform DetectPlatform()
    {
        if (!IsVRActive) return VRPlatform.None;

        // PC OpenXR runtimes (SteamVR, Oculus PC, WMR, Monado, Varjo, ...)
        // all share the same desktop bucket - mobile/standalone is the
        // distinction that actually changes rendering and feature defaults.
        if (!OS.HasFeature("android")) return VRPlatform.DesktopOpenXR;

        var runtime = GetRuntimeName().ToLowerInvariant();
        if (runtime.Contains("oculus") || runtime.Contains("meta")) return VRPlatform.MetaQuestStandalone;
        if (runtime.Contains("pico") || runtime.Contains("bytedance")) return VRPlatform.PicoStandalone;
        if (runtime.Contains("vive") || runtime.Contains("htc")
                                       || runtime.Contains("focus")) return VRPlatform.ViveFocusStandalone;

        // Fall back to interaction profile sniffing if the runtime name
        // didn't give us anything (some early standalone runtimes report
        // generic strings until the first controller binds).
        var profiles = (LeftInteractionProfile + "|" + RightInteractionProfile).ToLowerInvariant();
        if (profiles.Contains("oculus")) return VRPlatform.MetaQuestStandalone;
        if (profiles.Contains("bytedance") || profiles.Contains("pico")) return VRPlatform.PicoStandalone;
        if (profiles.Contains("htc") || profiles.Contains("focus3")) return VRPlatform.ViveFocusStandalone;

        return VRPlatform.DesktopOpenXR;
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

        RegisterHandSkeletonDevices(inputInterface);

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

        UpdateHandSkeletonTracking(Chirality.Left);
        UpdateHandSkeletonTracking(Chirality.Right);

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

    private void RegisterHandSkeletonDevices(InputInterface inputInterface)
    {
        RegisterHandSkeletonDevice(inputInterface, BodyNode.LeftPalm);
        RegisterHandSkeletonDevice(inputInterface, BodyNode.RightPalm);

        foreach (var chirality in new[] { Chirality.Left, Chirality.Right })
        {
            foreach (var finger in FingerOrder)
            {
                var segments = finger == FingerType.Thumb ? ThumbSegments : FingerSegments;
                for (int i = 0; i < segments.Length; i++)
                {
                    RegisterHandSkeletonDevice(inputInterface, finger.ComposeFinger(segments[i], chirality));
                }
            }
        }
    }

    private void RegisterHandSkeletonDevice(InputInterface inputInterface, BodyNode node)
    {
        if (_handSkeletonTrackedObjects.ContainsKey(node))
            return;

        var tracked = inputInterface.CreateDevice<TrackedObject>($"VR_{node}");
        tracked.CorrespondingBodyNode = node;
        tracked.Priority = 120;
        _handSkeletonTrackedObjects[node] = tracked;
    }

    private void UpdateHandSkeletonTracking(Chirality side)
    {
        var controllerObj = side == Chirality.Left ? _leftControllerTrackedObject : _rightControllerTrackedObject;
        if (controllerObj == null)
            return;

        bool isTracking = controllerObj.IsTracking;
        if (!isTracking)
        {
            SetHandSkeletonTrackingState(side, false);
            return;
        }

        float3 controllerPos = controllerObj.RawPosition;
        floatQ controllerRot = controllerObj.RawRotation;
        var xrController = side == Chirality.Left ? _leftController : _rightController;
        var vrController = side == Chirality.Left ? _inputInterface?.LeftController : _inputInterface?.RightController;

        float trigger = Clamp01(vrController?.TriggerValue ?? 0f);
        float grip = Clamp01(vrController?.GripValue ?? 0f);
        float squeezeForce = 0f;
        float thumbAxis = 0f;
        bool triggerTouch = false;
        bool thumbTouch = false;

        if (xrController != null && GodotObject.IsInstanceValid(xrController))
        {
            trigger = MathF.Max(trigger, Clamp01(ReadFloatAction(xrController, "trigger")));
            grip = MathF.Max(grip, Clamp01(ReadFloatAction(xrController, "grip")));
            grip = MathF.Max(grip, Clamp01(ReadFloatAction(xrController, "grip_force")));
            grip = MathF.Max(grip, Clamp01(ReadFloatAction(xrController, "squeeze")));
            grip = MathF.Max(grip, Clamp01(ReadFloatAction(xrController, "squeeze_force")));
            squeezeForce = Clamp01(ReadFloatAction(xrController, "grip_force"));
            squeezeForce = MathF.Max(squeezeForce, Clamp01(ReadFloatAction(xrController, "squeeze_force")));

            var thumb = xrController.GetVector2("primary");
            thumbAxis = thumb.X;

            triggerTouch = ReadBoolAction(xrController, "trigger_touch");
            thumbTouch = ReadBoolAction(xrController, "primary_touch")
                || ReadBoolAction(xrController, "secondary_touch")
                || ReadBoolAction(xrController, "ax_touch")
                || ReadBoolAction(xrController, "by_touch")
                || ReadBoolAction(xrController, "thumbstick_touch")
                || ReadBoolAction(xrController, "trackpad_touch");
        }

        float indexCurl = Clamp01(trigger * 0.92f + (triggerTouch ? 0.18f : 0f));
        float baseCurl = Clamp01(MathF.Max(grip, squeezeForce));
        float middleCurl = Clamp01(baseCurl * 0.95f);
        float ringCurl = Clamp01(baseCurl * 1.02f + 0.03f);
        float pinkyCurl = Clamp01(baseCurl * 1.08f + 0.05f);
        float thumbCurl = Clamp01(baseCurl * 0.45f + trigger * 0.30f + (thumbTouch ? 0.20f : 0f));
        float splay = Clamp01(MathF.Abs(thumbAxis)) * MathF.Sign(thumbAxis) * 0.18f;
        float handScale = 1.12f;

        BodyNode palmNode = side == Chirality.Left ? BodyNode.LeftPalm : BodyNode.RightPalm;
        float3 palmLocal = MirrorForSide(new float3(0.0000f, -0.0150f, -0.0350f), side, handScale);
        SetHandNodePose(
            palmNode,
            Add(controllerPos, controllerRot * palmLocal),
            controllerRot,
            true);

        WriteFingerPose(side, FingerType.Thumb, thumbCurl, -splay * 0.50f, controllerPos, controllerRot, handScale);
        WriteFingerPose(side, FingerType.Index, indexCurl, splay * 0.70f, controllerPos, controllerRot, handScale);
        WriteFingerPose(side, FingerType.Middle, middleCurl, splay * 0.20f, controllerPos, controllerRot, handScale);
        WriteFingerPose(side, FingerType.Ring, ringCurl, -splay * 0.25f, controllerPos, controllerRot, handScale);
        WriteFingerPose(side, FingerType.Pinky, pinkyCurl, -splay * 0.75f, controllerPos, controllerRot, handScale);
    }

    private void WriteFingerPose(
        Chirality side,
        FingerType finger,
        float curl,
        float splay,
        float3 controllerPos,
        floatQ controllerRot,
        float handScale)
    {
        var openPoints = GetOpenPosePoints(finger);
        var closedPoints = GetClosedPosePoints(finger);
        var segments = finger == FingerType.Thumb ? ThumbSegments : FingerSegments;

        for (int i = 0; i < segments.Length; i++)
        {
            float3 open = openPoints[i];
            float3 closed = closedPoints[i];

            float3 local = new float3(
                Lerp(open.x, closed.x, curl),
                Lerp(open.y, closed.y, curl),
                Lerp(open.z, closed.z, curl));

            if (finger != FingerType.Thumb)
                local = new float3(local.x + splay * (0.4f + i * 0.1f), local.y, local.z);

            local = MirrorForSide(local, side, handScale);
            var world = Add(controllerPos, controllerRot * local);
            var node = finger.ComposeFinger(segments[i], side);
            SetHandNodePose(node, world, controllerRot, true);
        }
    }

    private void SetHandSkeletonTrackingState(Chirality side, bool isTracking)
    {
        BodyNode palmNode = side == Chirality.Left ? BodyNode.LeftPalm : BodyNode.RightPalm;
        SetHandNodeTrackingState(palmNode, isTracking);

        foreach (var finger in FingerOrder)
        {
            var segments = finger == FingerType.Thumb ? ThumbSegments : FingerSegments;
            for (int i = 0; i < segments.Length; i++)
                SetHandNodeTrackingState(finger.ComposeFinger(segments[i], side), isTracking);
        }
    }

    private void SetHandNodeTrackingState(BodyNode node, bool isTracking)
    {
        if (!_handSkeletonTrackedObjects.TryGetValue(node, out var tracked))
            return;

        tracked.IsTracking = isTracking;
        tracked.IsDeviceActive = isTracking;
        tracked.TrackingSpace = _inputInterface?.GlobalTrackingSpace;
    }

    private void SetHandNodePose(BodyNode node, float3 position, floatQ rotation, bool isTracking)
    {
        if (!_handSkeletonTrackedObjects.TryGetValue(node, out var tracked))
            return;

        tracked.RawPosition = position;
        tracked.RawRotation = rotation;
        tracked.IsTracking = isTracking;
        tracked.IsDeviceActive = isTracking;
        tracked.TrackingSpace = _inputInterface?.GlobalTrackingSpace;
    }

    private static float3[] GetOpenPosePoints(FingerType finger)
    {
        return finger switch
        {
            FingerType.Thumb => new[]
            {
                new float3(0.0240f, -0.0100f, -0.0320f),
                new float3(0.0360f, -0.0040f, -0.0430f),
                new float3(0.0440f,  0.0000f, -0.0560f),
                new float3(0.0500f,  0.0040f, -0.0680f),
            },
            FingerType.Index => new[]
            {
                new float3(0.0160f, -0.0060f, -0.0480f),
                new float3(0.0170f, -0.0030f, -0.0670f),
                new float3(0.0180f,  0.0010f, -0.0850f),
                new float3(0.0190f,  0.0040f, -0.1030f),
                new float3(0.0200f,  0.0070f, -0.1190f),
            },
            FingerType.Middle => new[]
            {
                new float3(0.0060f, -0.0060f, -0.0470f),
                new float3(0.0060f, -0.0020f, -0.0680f),
                new float3(0.0060f,  0.0020f, -0.0890f),
                new float3(0.0060f,  0.0060f, -0.1090f),
                new float3(0.0060f,  0.0100f, -0.1260f),
            },
            FingerType.Ring => new[]
            {
                new float3(-0.0040f, -0.0070f, -0.0450f),
                new float3(-0.0050f, -0.0030f, -0.0640f),
                new float3(-0.0060f,  0.0000f, -0.0820f),
                new float3(-0.0070f,  0.0030f, -0.0990f),
                new float3(-0.0080f,  0.0060f, -0.1130f),
            },
            _ => new[]
            {
                new float3(-0.0140f, -0.0090f, -0.0410f),
                new float3(-0.0160f, -0.0060f, -0.0570f),
                new float3(-0.0180f, -0.0030f, -0.0720f),
                new float3(-0.0200f,  0.0000f, -0.0860f),
                new float3(-0.0220f,  0.0030f, -0.0980f),
            },
        };
    }

    private static float3[] GetClosedPosePoints(FingerType finger)
    {
        return finger switch
        {
            FingerType.Thumb => new[]
            {
                new float3(0.0200f, -0.0080f, -0.0320f),
                new float3(0.0260f, -0.0100f, -0.0230f),
                new float3(0.0300f, -0.0120f, -0.0120f),
                new float3(0.0330f, -0.0130f, -0.0010f),
            },
            FingerType.Index => new[]
            {
                new float3(0.0160f, -0.0060f, -0.0470f),
                new float3(0.0200f, -0.0130f, -0.0320f),
                new float3(0.0220f, -0.0170f, -0.0180f),
                new float3(0.0240f, -0.0200f, -0.0060f),
                new float3(0.0260f, -0.0220f,  0.0060f),
            },
            FingerType.Middle => new[]
            {
                new float3(0.0060f, -0.0060f, -0.0460f),
                new float3(0.0090f, -0.0150f, -0.0310f),
                new float3(0.0100f, -0.0200f, -0.0180f),
                new float3(0.0110f, -0.0240f, -0.0070f),
                new float3(0.0120f, -0.0270f,  0.0040f),
            },
            FingerType.Ring => new[]
            {
                new float3(-0.0040f, -0.0070f, -0.0440f),
                new float3(-0.0030f, -0.0160f, -0.0300f),
                new float3(-0.0020f, -0.0210f, -0.0180f),
                new float3(-0.0010f, -0.0250f, -0.0080f),
                new float3( 0.0000f, -0.0280f,  0.0030f),
            },
            _ => new[]
            {
                new float3(-0.0130f, -0.0080f, -0.0410f),
                new float3(-0.0110f, -0.0160f, -0.0290f),
                new float3(-0.0100f, -0.0210f, -0.0180f),
                new float3(-0.0090f, -0.0250f, -0.0090f),
                new float3(-0.0080f, -0.0290f,  0.0010f),
            },
        };
    }

    private static float ReadFloatAction(XRController3D controller, string action)
    {
        if (controller == null || !GodotObject.IsInstanceValid(controller))
            return 0f;

        float value = controller.GetFloat(action);
        return float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
    }

    private static bool ReadBoolAction(XRController3D controller, string action)
    {
        if (controller == null || !GodotObject.IsInstanceValid(controller))
            return false;

        Variant value = controller.Get(action);
        return value.VariantType switch
        {
            Variant.Type.Bool => value.AsBool(),
            Variant.Type.Float => value.AsSingle() > 0.5f,
            Variant.Type.Int => value.AsInt32() > 0,
            _ => controller.GetFloat(action) > 0.5f
        };
    }

    private static float Clamp01(float value)
    {
        return Math.Clamp(value, 0f, 1f);
    }

    private static float Lerp(float from, float to, float t)
    {
        return from + (to - from) * Clamp01(t);
    }

    private static float3 MirrorForSide(float3 rightHandLocal, Chirality side, float scale)
    {
        float3 scaled = new float3(
            rightHandLocal.x * scale,
            rightHandLocal.y * scale,
            rightHandLocal.z * scale);

        return side == Chirality.Left
            ? new float3(-scaled.x, scaled.y, scaled.z)
            : scaled;
    }

    private static float3 Add(float3 a, float3 b)
    {
        return new float3(a.x + b.x, a.y + b.y, a.z + b.z);
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
        XRServer.TrackerAdded -= OnTrackerAdded;
        _xrInterface = null;
        GD.Print("GodotVRDriver: VR shutdown");
    }
}
