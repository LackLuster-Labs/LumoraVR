using Godot;
using Lumora.Core.Components;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// Hook for HeadOutput component → Godot Camera3D.
/// Manages camera attachment to user's head slot and follows head position/rotation.
/// </summary>
public class HeadOutputHook : ComponentHook<HeadOutput>
{
    private Camera3D _camera;
    private bool _isInitialized = false;
    private bool _loggedMissingUserRoot = false;

    public override void Initialize()
    {
        base.Initialize();

        TryInitializeCamera();
    }

    public override void ApplyChanges()
    {
        if (!_isInitialized)
        {
            TryInitializeCamera();
        }

        if (!_isInitialized || _camera == null || !GodotObject.IsInstanceValid(_camera))
            return;

        // Only update for local user (check via slot's UserRoot)
        var userRoot = Owner.Slot.GetComponent<UserRoot>();
        if (userRoot?.ActiveUser != Owner.World.LocalUser)
            return;

        // Camera automatically follows its parent node (head slot)
        // The head slot's position is updated by UserRoot/CharacterController

        // Update camera settings if needed
        if (Owner.Enabled != _camera.Current)
        {
            _camera.Current = Owner.Enabled;
        }
    }

    private void TryInitializeCamera()
    {
        if (_isInitialized)
            return;

        var userRoot = Owner.Slot.GetComponent<UserRoot>();
        if (userRoot == null)
        {
            if (!_loggedMissingUserRoot)
            {
                AquaLogger.Warn($"HeadOutputHook: No UserRoot found on slot '{Owner.Slot.SlotName.Value}'");
                _loggedMissingUserRoot = true;
            }
            return;
        }

        if (userRoot.ActiveUser != Owner.World.LocalUser)
            return;

        _camera = new Camera3D
        {
            Name = "UserCamera",
            Current = true,
            Fov = 90f,
            Near = 0.05f,
            Far = 1000f
        };

        Node3D targetNode = attachedNode;
        if (userRoot.HeadSlot != null && userRoot.HeadSlot.Hook != null)
        {
            var headHook = userRoot.HeadSlot.Hook as SlotHook;
            if (headHook != null)
            {
                targetNode = headHook.RequestNode3D();
                AquaLogger.Log($"HeadOutputHook: Attaching camera to head slot");
            }
        }

        if (targetNode != null && GodotObject.IsInstanceValid(targetNode))
        {
            targetNode.AddChild(_camera);
            _isInitialized = true;
            AquaLogger.Log($"HeadOutputHook: Created camera for local user '{userRoot.ActiveUser.UserName.Value}'");
        }
        else
        {
            AquaLogger.Error($"HeadOutputHook: Failed to find valid node to attach camera");
        }
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld && _camera != null && GodotObject.IsInstanceValid(_camera))
        {
            _camera.QueueFree();
        }

        _camera = null;
        _isInitialized = false;

        base.Destroy(destroyingWorld);
    }
}
