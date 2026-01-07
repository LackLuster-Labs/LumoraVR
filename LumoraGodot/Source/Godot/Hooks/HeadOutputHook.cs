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

    public override void Initialize()
    {
        base.Initialize();

        // Get UserRoot from the same slot
        var userRoot = Owner.Slot.GetComponent<UserRoot>();
        if (userRoot == null)
        {
            AquaLogger.Warn($"HeadOutputHook: No UserRoot found on slot '{Owner.Slot.SlotName.Value}'");
            return;
        }

        // Only create camera for local user
        if (userRoot.ActiveUser != Owner.World.LocalUser)
        {
            AquaLogger.Log($"HeadOutputHook: Not local user, skipping camera creation");
            return;
        }

        // Create Camera3D
        _camera = new Camera3D();
        _camera.Name = "UserCamera";
        _camera.Current = true; // Make this the active camera
        _camera.Fov = 90f;
        _camera.Near = 0.05f;
        _camera.Far = 1000f;

        // Attach camera to the user's head slot (or user root if no head slot)
        Node3D targetNode = attachedNode;
        if (userRoot.HeadSlot != null && userRoot.HeadSlot.Hook != null)
        {
            var headHook = userRoot.HeadSlot.Hook as SlotHook;
            if (headHook != null)
            {
                // Ensure a Node3D exists for the head slot
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

    public override void ApplyChanges()
    {
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
