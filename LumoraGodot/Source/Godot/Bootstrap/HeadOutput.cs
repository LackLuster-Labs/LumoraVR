using System;
using Godot;
using Lumora.Core;
using Aquamarine.Godot.Extensions;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Source.Godot.Bootstrap;

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
        _camera = camera;
        _camera.Fov = DefaultFOV;
        _camera.Near = NearClip;
        _camera.Far = FarClip;

        // Check if VR is active
        var xrInterface = XRServer.FindInterface("OpenXR");
        _isVRActive = xrInterface != null && xrInterface.IsInitialized();

        if (_isVRActive)
        {
            SetupVRCamera();
            Type = OutputType.VR;
        }
        else
        {
            Type = OutputType.Screen;
        }

        AquaLogger.Log($"HeadOutput: Initialized with type={Type}, FOV={DefaultFOV}, isVR={_isVRActive}");
    }

    /// <summary>
    /// Setup VR camera system.
    /// </summary>
    private void SetupVRCamera()
    {
        // Check if we're in the scene tree yet
        var parent = GetParent();
        if (parent == null)
        {
            // Defer VR setup until we're added to the scene tree
            AquaLogger.Log("HeadOutput: Deferring VR setup until added to scene tree");
            CallDeferred(MethodName.SetupVRCamera);
            return;
        }

        // Create XR Origin if it doesn't exist
        _xrOrigin = GetNodeOrNull<XROrigin3D>("../XROrigin3D");
        if (_xrOrigin == null)
        {
            _xrOrigin = new XROrigin3D();
            _xrOrigin.Name = "XROrigin3D";
            parent.AddChild(_xrOrigin);
        }

        // Create or find XR Camera
        _vrCamera = _xrOrigin.GetNodeOrNull<XRCamera3D>("XRCamera3D");
        if (_vrCamera == null)
        {
            _vrCamera = new XRCamera3D();
            _vrCamera.Name = "XRCamera3D";
            _xrOrigin.AddChild(_vrCamera);
        }

        // Use VR camera as primary
        _camera = _vrCamera;

        AquaLogger.Log("HeadOutput: VR camera setup complete");
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
    /// Switch output type (e.g., VR <-> Screen).
    /// </summary>
    public void SwitchOutputType(OutputType newType)
    {
        if (Type == newType)
            return;

        AquaLogger.Log($"HeadOutput: Switching from {Type} to {newType}");

        Type = newType;

        if (newType == OutputType.VR && !_isVRActive)
        {
            AquaLogger.Warn("Cannot switch to VR - XR interface not active");
            Type = OutputType.Screen;
        }
    }

    /// <summary>
    /// Cleanup.
    /// </summary>
    public void Dispose()
    {
        _camera = null;
        _vrCamera = null;
        _xrOrigin = null;
    }
}
