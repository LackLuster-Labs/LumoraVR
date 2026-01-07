using Godot;
using Lumora.Core.Components;
using Lumora.Core.Math;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// Hook for Camera component → Godot Camera3D.
/// Platform camera hook for Godot.
/// </summary>
public class CameraHook : ComponentHook<Camera>
{
    private Node3D _cameraContainer;
    private Camera3D _camera;

    public Camera3D GodotCamera => _camera;

    public override void Initialize()
    {
        base.Initialize();

        _cameraContainer = new Node3D();
        _cameraContainer.Name = "CameraContainer";
        attachedNode.AddChild(_cameraContainer);

        _camera = new Camera3D();
        _camera.Name = "Camera";
        _cameraContainer.AddChild(_camera);

        _camera.Environment = null;
        _camera.Attributes = null;
    }

    public override void ApplyChanges()
    {
        _camera.Projection = Owner.Projection.Value == ProjectionType.Orthographic
            ? Camera3D.ProjectionType.Orthogonal
            : Camera3D.ProjectionType.Perspective;

        _camera.Fov = Owner.FieldOfView.Value;
        _camera.Size = Owner.OrthographicSize.Value;
        _camera.Near = Owner.NearClip.Value;
        _camera.Far = Owner.FarClip.Value;

        UpdateClearMode();
        UpdateViewport();

        _camera.Current = Owner.Enabled.Value && Owner.Slot.IsActive;
    }

    private void UpdateClearMode()
    {
        switch (Owner.Clear.Value)
        {
            case ClearMode.Skybox:
                break;

            case ClearMode.Color:
                break;

            case ClearMode.DepthOnly:
            case ClearMode.Nothing:
                break;
        }
    }

    private void UpdateViewport()
    {
        float4 viewport = Owner.ViewportRect.Value;
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld && _cameraContainer != null && GodotObject.IsInstanceValid(_cameraContainer))
        {
            _cameraContainer.QueueFree();
        }
        _cameraContainer = null;
        _camera = null;

        base.Destroy(destroyingWorld);
    }
}
