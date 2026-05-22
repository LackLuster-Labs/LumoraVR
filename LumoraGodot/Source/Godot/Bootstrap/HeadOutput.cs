// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

﻿using System;
using Godot;
using Lumora.Core;
using Lumora.Godot.Extensions;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.Bootstrap;

/// <summary>
/// Manages camera rendering for VR and screen modes.
/// Handles position/rotation, FOV, and view overrides.
/// </summary>
public partial class HeadOutput : Node
{
    /// <summary>
    /// Output type determines how camera is positioned and rendered.
    /// </summary>
    public enum OutputType
    {
        /// <summary>VR headset rendering (stereo, tracked)</summary>
        VR,

        /// <summary>Standard screen rendering (mono, user-controlled or first-person)</summary>
        Screen,

        /// <summary>360-degree equirectangular rendering</summary>
        Screen360,

        /// <summary>Static camera (no movement)</summary>
        Static
    }

    // ===== CONFIGURATION =====
    [Export] public OutputType Type { get; set; } = OutputType.Screen;
    [Export] public float DefaultFOV { get; set; } = 90f;
    [Export] public float NearClip { get; set; } = 0.25f; // Clips through head sphere
    [Export] public float FarClip { get; set; } = 1000f;

    // ===== CAMERA REFERENCES =====
    private Camera3D _camera;
    private Camera3D _desktopCamera;
    private XRCamera3D _vrCamera;
    private XROrigin3D _xrOrigin;

    // ===== STATE =====
    private bool _isVRActive = false;
    private Vector3 _overridePosition = Vector3.Zero;
    private Quaternion _overrideRotation = Quaternion.Identity;
    private bool _hasPositionOverride = false;
    private bool _hasRotationOverride = false;

    /// <summary>
    /// Current camera position in world space.
    /// </summary>
    public Vector3 CameraPosition => _camera?.GlobalPosition ?? Vector3.Zero;

    /// <summary>
    /// Current camera rotation.
    /// </summary>
    public Quaternion CameraRotation => _camera?.GlobalTransform.Basis.GetRotationQuaternion() ?? Quaternion.Identity;

    /// <summary>
    /// Initialize HeadOutput with a camera.
    /// </summary>
    public void Initialize(Camera3D camera)
    {
        _desktopCamera = camera;
        _camera = _desktopCamera;
        ConfigureCamera(_desktopCamera, setFov: true);

        // Check if VR is active
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

        LumoraLogger.Log($"HeadOutput: Initialized with type={Type}, FOV={DefaultFOV}, isVR={_isVRActive}");
    }

    /// <summary>
    /// Locate the XROrigin3D / XRCamera3D defined in Bootstrap.tscn.
    /// They live inside the XR SubViewport (see %XROrigin3D / %XRCamera3D) and
    /// are no longer created at runtime - the .tscn ships them so the XR
    /// viewport stays valid for the whole process lifetime.
    /// We resolve via CurrentScene rather than the calling node's owner-chain
    /// because HeadOutput is created at runtime (Owner == null), which breaks
    /// the bare-percent unique-name lookup.
    /// - xlinka
    /// </summary>
    private void SetupVRCamera()
    {
        if (!IsInsideTree())
        {
            LumoraLogger.Log("HeadOutput: Deferring VR setup until added to scene tree");
            CallDeferred(MethodName.SetupVRCamera);
            return;
        }

        var sceneRoot = GetTree()?.CurrentScene;
        _xrOrigin = sceneRoot?.GetNodeOrNull<XROrigin3D>("%XROrigin3D");
        _vrCamera = sceneRoot?.GetNodeOrNull<XRCamera3D>("%XRCamera3D");

        if (_xrOrigin == null || _vrCamera == null)
        {
            LumoraLogger.Error("HeadOutput: %XROrigin3D / %XRCamera3D not found in scene. " +
                "Bootstrap.tscn must contain the XR SubViewport sub-tree.");
            return;
        }

        ConfigureCamera(_vrCamera, setFov: false);

        LumoraLogger.Log("HeadOutput: VR camera bound to %XRCamera3D");
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
            camera.Fov = DefaultFOV;
    }

    /// <summary>
    /// Update camera positioning based on focused world.
    /// </summary>
    public void UpdatePositioning(Lumora.Core.Engine engine)
    {
        if (_camera == null || engine == null)
            return;

        // Get focused world
        var focusedWorld = engine.WorldManager?.FocusedWorld;
        if (focusedWorld == null)
            return;

        // Update camera settings from world
        UpdateCameraSettings(focusedWorld);

        // Update position/rotation
        if (Type == OutputType.VR)
        {
            UpdateVRPositioning(focusedWorld);
        }
        else if (Type == OutputType.Screen)
        {
            UpdateScreenPositioning(focusedWorld);
        }
        // TODO: Screen360, Static modes
    }

    /// <summary>
    /// Update camera settings (FOV, clip planes) from world.
    /// </summary>
    private void UpdateCameraSettings(World world)
    {
        // TODO: Get settings from world when RenderSettings component exists
        // For now, use defaults
        _camera.Near = NearClip;
        _camera.Far = FarClip;

        if (Type == OutputType.Screen)
        {
            _camera.Fov = DefaultFOV;
        }
    }

    /// <summary>
    /// Update VR camera positioning.
    /// VR cameras are tracked automatically by XR system.
    /// </summary>
    private void UpdateVRPositioning(World world)
    {
        // VR camera is automatically tracked by OpenXR
        // We just need to position the XR Origin based on world's local user

        if (world.LocalUser == null)
            return;

        // Align the XR origin to the local user's root so HMD/controllers match avatar transforms
        var userRootSlot = world.LocalUser.Root?.Slot;
        if (_xrOrigin != null)
        {
            if (userRootSlot != null)
            {
                var originPosition = userRootSlot.GlobalPosition.ToGodot();
                var originRotation = new Basis(userRootSlot.GlobalRotation.ToGodot());
                _xrOrigin.GlobalTransform = new Transform3D(originRotation, originPosition);
            }
            else
            {
                _xrOrigin.GlobalPosition = Vector3.Zero;
                _xrOrigin.GlobalRotation = Vector3.Zero;
            }
        }
    }

    /// <summary>
    /// Update screen camera positioning.
    /// Screen cameras can be user-controlled or follow first-person view.
    /// </summary>
    private void UpdateScreenPositioning(World world)
    {
        // Check for position/rotation overrides
        if (_hasPositionOverride)
        {
            _camera.GlobalPosition = _overridePosition;
        }
        else
        {
            // Follow local user's head position if they have a UserRoot
            if (world.LocalUser?.Root != null)
            {
                var userRoot = world.LocalUser.Root;
                _camera.GlobalPosition = userRoot.HeadPosition.ToGodot();
            }
            else
            {
                // Fallback to default height
                _camera.GlobalPosition = new Vector3(0, 1.6f, 0);
            }
        }

        if (_hasRotationOverride)
        {
            _camera.GlobalTransform = new Transform3D(new Basis(_overrideRotation), _camera.GlobalPosition);
        }
        else
        {
            // Follow local user's head rotation if they have a UserRoot
            if (world.LocalUser?.Root != null)
            {
                var userRoot = world.LocalUser.Root;
                var rotation = userRoot.HeadRotation;
                _camera.GlobalTransform = new Transform3D(new Basis(rotation.ToGodot()), _camera.GlobalPosition);
            }
        }
    }

    /// <summary>
    /// Set position override for camera.
    /// Used for custom camera control (e.g., photo mode, cinematic cameras).
    /// </summary>
    public void SetPositionOverride(Vector3 position)
    {
        _overridePosition = position;
        _hasPositionOverride = true;
    }

    /// <summary>
    /// Clear position override.
    /// </summary>
    public void ClearPositionOverride()
    {
        _hasPositionOverride = false;
    }

    /// <summary>
    /// Set rotation override for camera.
    /// </summary>
    public void SetRotationOverride(Quaternion rotation)
    {
        _overrideRotation = rotation;
        _hasRotationOverride = true;
    }

    /// <summary>
    /// Clear rotation override.
    /// </summary>
    public void ClearRotationOverride()
    {
        _hasRotationOverride = false;
    }

    /// <summary>
    /// Called by XRModeManager when the VR active state changes at runtime.
    /// Updates the internal flag and re-runs VR camera setup if activating VR.
    /// </summary>
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

        LumoraLogger.Log($"HeadOutput: VR active state → {isActive}");
    }

    /// <summary>
    /// Switch output type (e.g., VR ↔ Screen).
    /// When switching to VR call <see cref="NotifyVRActiveChanged"/> first so
    /// <c>_isVRActive</c> is already up-to-date by the time this runs.
    /// </summary>
    public void SwitchOutputType(OutputType newType)
    {
        if (Type == newType)
            return;

        LumoraLogger.Log($"HeadOutput: Switching from {Type} to {newType}");

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

    /// <summary>
    /// Cleanup.
    /// </summary>
    public void Dispose()
    {
        _camera = null;
        _desktopCamera = null;
        _vrCamera = null;
        _xrOrigin = null;
    }
}
