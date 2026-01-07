using Godot;
using Lumora.Core;
using Lumora.Core.HelioUI;
using Lumora.Core.Math;

namespace Aquamarine.Godot.Hooks.HelioUI;

/// <summary>
/// Godot hook that provides VR laser pointer input for Helio UI.
/// Bridges OpenXR controller input to the HelioInputModule.
/// </summary>
public partial class HelioVRPointerHook : Node3D
{
    /// <summary>
    /// The Helio input module to send events to.
    /// </summary>
    public HelioInputModule InputModule { get; set; }

    /// <summary>
    /// Which hand this pointer represents.
    /// </summary>
    [Export]
    public int HandIndex { get; set; } = 0;

    /// <summary>
    /// Maximum raycast distance for UI interaction.
    /// </summary>
    [Export]
    public float MaxDistance { get; set; } = 10f;

    /// <summary>
    /// Whether to show the laser beam visual.
    /// </summary>
    [Export]
    public bool ShowLaser { get; set; } = true;

    /// <summary>
    /// Laser color when not hitting UI.
    /// </summary>
    [Export]
    public Color LaserColor { get; set; } = new Color(0.3f, 0.5f, 1f, 0.5f);

    /// <summary>
    /// Laser color when hovering over interactable.
    /// </summary>
    [Export]
    public Color LaserHoverColor { get; set; } = new Color(0.5f, 1f, 0.5f, 0.8f);

    private MeshInstance3D _laserMesh;
    private MeshInstance3D _cursorMesh;
    private StandardMaterial3D _laserMaterial;
    private bool _isPressed;
    private HelioRaycastResult _lastHit;
    private IHelioInteractable _hoveredInteractable;

    public override void _Ready()
    {
        CreateLaserVisual();
        CreateCursorVisual();
    }

    private void CreateLaserVisual()
    {
        _laserMesh = new MeshInstance3D();
        _laserMesh.Name = "LaserBeam";

        var cylinder = new CylinderMesh();
        cylinder.TopRadius = 0.002f;
        cylinder.BottomRadius = 0.002f;
        cylinder.Height = 1f;
        _laserMesh.Mesh = cylinder;

        _laserMaterial = new StandardMaterial3D();
        _laserMaterial.AlbedoColor = LaserColor;
        _laserMaterial.EmissionEnabled = true;
        _laserMaterial.Emission = LaserColor;
        _laserMaterial.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        _laserMaterial.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        _laserMesh.MaterialOverride = _laserMaterial;

        AddChild(_laserMesh);
        _laserMesh.Visible = ShowLaser;
    }

    private void CreateCursorVisual()
    {
        _cursorMesh = new MeshInstance3D();
        _cursorMesh.Name = "Cursor";

        var sphere = new SphereMesh();
        sphere.Radius = 0.01f;
        sphere.Height = 0.02f;
        _cursorMesh.Mesh = sphere;

        var cursorMaterial = new StandardMaterial3D();
        cursorMaterial.AlbedoColor = Colors.White;
        cursorMaterial.EmissionEnabled = true;
        cursorMaterial.Emission = Colors.White;
        cursorMaterial.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        _cursorMesh.MaterialOverride = cursorMaterial;

        AddChild(_cursorMesh);
        _cursorMesh.Visible = false;
    }

    public override void _Process(double delta)
    {
        if (InputModule == null) return;

        // Get controller ray
        var rayOrigin = ToLumoraFloat3(GlobalPosition);
        var rayDirection = ToLumoraFloat3(-GlobalTransform.Basis.Z);

        HelioRaycastResult? hitResult = RaycastUI(rayOrigin, rayDirection);

        // Update visuals
        UpdateLaserVisual(hitResult);
        UpdateCursorVisual(hitResult);

        // Create event data
        var eventData = new HelioPointerEventData
        {
            PointerId = HandIndex,
            Type = PointerType.Laser,
            RayOrigin = rayOrigin,
            RayDirection = rayDirection
        };

        if (hitResult.HasValue)
        {
            var hit = hitResult.Value;
            eventData.Position = hit.CanvasPosition;
            eventData.WorldPosition = hit.WorldPosition;

            // Handle hover state changes
            var newInteractable = hit.Interactable;
            if (newInteractable != _hoveredInteractable)
            {
                // Exit old
                if (_hoveredInteractable != null)
                {
                    InputModule.ProcessPointerExit(_hoveredInteractable, eventData);
                }

                // Enter new
                if (newInteractable != null)
                {
                    InputModule.ProcessPointerEnter(newInteractable, eventData);
                }

                _hoveredInteractable = newInteractable;
            }

            _lastHit = hit;
        }
        else if (_hoveredInteractable != null)
        {
            // Exit current hover
            InputModule.ProcessPointerExit(_hoveredInteractable, eventData);
            _hoveredInteractable = null;
        }
    }

    /// <summary>
    /// Called when trigger is pressed on this hand's controller.
    /// </summary>
    public void OnTriggerPressed()
    {
        if (_hoveredInteractable == null || InputModule == null) return;

        _isPressed = true;

        var eventData = new HelioPointerEventData
        {
            PointerId = HandIndex,
            Type = PointerType.Laser,
            Position = _lastHit.CanvasPosition,
            WorldPosition = _lastHit.WorldPosition
        };

        InputModule.ProcessPointerDown(_hoveredInteractable, eventData);
    }

    /// <summary>
    /// Called when trigger is released on this hand's controller.
    /// </summary>
    public void OnTriggerReleased()
    {
        if (InputModule == null) return;

        if (_isPressed && _hoveredInteractable != null)
        {
            var eventData = new HelioPointerEventData
            {
                PointerId = HandIndex,
                Type = PointerType.Laser,
                Position = _lastHit.CanvasPosition,
                WorldPosition = _lastHit.WorldPosition
            };

            InputModule.ProcessPointerUp(_hoveredInteractable, eventData);
        }

        _isPressed = false;
    }

    private void UpdateLaserVisual(HelioRaycastResult? hit)
    {
        if (!ShowLaser || _laserMesh == null) return;

        float length = hit.HasValue ? hit.Value.Distance : MaxDistance;
        length = Mathf.Clamp(length, 0.01f, MaxDistance);

        // Update laser geometry
        var cylinder = (CylinderMesh)_laserMesh.Mesh;
        cylinder.Height = length;

        // Position at halfway point, rotated to point forward
        _laserMesh.Position = new Vector3(0, 0, -length / 2);
        _laserMesh.Rotation = new Vector3(Mathf.DegToRad(90), 0, 0);

        // Update color based on hover state
        var color = _hoveredInteractable != null ? LaserHoverColor : LaserColor;
        _laserMaterial.AlbedoColor = color;
        _laserMaterial.Emission = color;

        _laserMesh.Visible = true;
    }

    private void UpdateCursorVisual(HelioRaycastResult? hit)
    {
        if (_cursorMesh == null) return;

        if (hit.HasValue && hit.Value.IsValid)
        {
            var worldPos = hit.Value.WorldPosition;
            _cursorMesh.GlobalPosition = new Vector3(worldPos.x, worldPos.y, worldPos.z);
            _cursorMesh.Visible = true;

            // Scale cursor based on distance
            float scale = hit.Value.Distance * 0.02f;
            _cursorMesh.Scale = new Vector3(scale, scale, scale);
        }
        else
        {
            _cursorMesh.Visible = false;
        }
    }

    private float3 ToLumoraFloat3(Vector3 v)
    {
        return new float3(v.X, v.Y, v.Z);
    }

    private HelioRaycastResult? RaycastUI(float3 rayOrigin, float3 rayDirection)
    {
        HelioRaycastResult? closest = null;

        foreach (var raycaster in HelioRaycaster.ActiveRaycasters)
        {
            if (raycaster == null || raycaster.TargetCanvas?.Target == null)
                continue;

            if (raycaster.Raycast(rayOrigin, rayDirection, out var hit) && hit.IsValid)
            {
                if (!closest.HasValue || hit.Distance < closest.Value.Distance)
                {
                    closest = hit;
                }
            }
        }

        return closest;
    }

    public override void _ExitTree()
    {
        if (_hoveredInteractable != null && InputModule != null)
        {
            InputModule.ProcessPointerExit(_hoveredInteractable, new HelioPointerEventData
            {
                PointerId = HandIndex,
                Type = PointerType.Laser
            });
        }
    }
}
