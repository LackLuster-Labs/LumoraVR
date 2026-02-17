using System.Collections.Generic;
using Aquamarine.Godot.Hooks;
using Godot;
using Lumora.Core;
using Lumora.Core.Components.Gizmos;
using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks.Gizmos;

#nullable enable

/// <summary>
/// Godot hook for rendering the SlotGizmo.
/// Displays bounding box, name label, and toolbar buttons.
/// </summary>
public sealed class SlotGizmoHook : ComponentHook<SlotGizmo>
{
    // Materials
    private StandardMaterial3D? _boundsMaterial;
    private StandardMaterial3D? _xAxisMaterial;
    private StandardMaterial3D? _yAxisMaterial;
    private StandardMaterial3D? _zAxisMaterial;
    private StandardMaterial3D? _highlightMaterial;

    // Bounding box visuals
    private MeshInstance3D? _boundsMesh;
    private ImmediateMesh? _boundsImmediateMesh;
    private Vector3 _boundsCenter = Vector3.Zero;
    private Vector3 _boundsSize = Vector3.One * 0.2f;

    // Axis lines for showing position relative to parent
    private MeshInstance3D? _xAxisLine;
    private MeshInstance3D? _yAxisLine;
    private MeshInstance3D? _zAxisLine;

    // Name label
    private Label3D? _nameLabel;

    // Toolbar buttons (will be 3D clickable buttons)
    private Node3D? _toolbarRoot;
    private MeshInstance3D? _translateButton;
    private MeshInstance3D? _rotateButton;
    private MeshInstance3D? _scaleButton;
    private MeshInstance3D? _spaceButton;
    private MeshInstance3D? _parentButton;

    // Collision areas for button interaction
    private Area3D? _translateArea;
    private Area3D? _rotateArea;
    private Area3D? _scaleArea;
    private Area3D? _spaceArea;
    private Area3D? _parentArea;

    // Colors
    private static readonly Color BoundsColor = new(0.72f, 0.67f, 1f, 0.95f); // Soft violet lines
    private static readonly Color XAxisColor = new(1f, 0f, 0f, 1f); // Red
    private static readonly Color YAxisColor = new(0f, 1f, 0f, 1f); // Green
    private static readonly Color ZAxisColor = new(0f, 0f, 1f, 1f); // Blue
    private static readonly Color HighlightColor = new(1f, 1f, 0f, 0.8f); // Yellow

    public static IHook<SlotGizmo> Constructor()
    {
        return new SlotGizmoHook();
    }

    public override void Initialize()
    {
        base.Initialize();

        CreateMaterials();
        CreateBoundingBox();
        CreateAxisLines();
        CreateNameLabel();
        CreateToolbar();

        UpdateVisuals();

        AquaLogger.Log($"SlotGizmoHook: Initialized for slot '{Owner.TargetSlot?.Name.Value}'");
    }

    private void CreateMaterials()
    {
        _boundsMaterial = CreateOverlayMaterial(BoundsColor);
        _xAxisMaterial = CreateOverlayMaterial(XAxisColor);
        _yAxisMaterial = CreateOverlayMaterial(YAxisColor);
        _zAxisMaterial = CreateOverlayMaterial(ZAxisColor);
        _highlightMaterial = CreateOverlayMaterial(HighlightColor);
    }

    private StandardMaterial3D CreateOverlayMaterial(Color color)
    {
        var mat = new StandardMaterial3D();
        mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        mat.AlbedoColor = color;
        mat.NoDepthTest = true; // Always visible (overlay)
        mat.RenderPriority = 100;
        return mat;
    }

    private void CreateBoundingBox()
    {
        _boundsMesh = new MeshInstance3D();
        _boundsMesh.Name = "BoundingBox";

        _boundsImmediateMesh = new ImmediateMesh();
        _boundsMesh.Mesh = _boundsImmediateMesh;
        _boundsMesh.MaterialOverride = _boundsMaterial;

        attachedNode.AddChild(_boundsMesh);
    }

    private void CreateAxisLines()
    {
        // X axis (red)
        _xAxisLine = CreateAxisLine(_xAxisMaterial!, Vector3.Right, "XAxis");
        attachedNode.AddChild(_xAxisLine);

        // Y axis (green)
        _yAxisLine = CreateAxisLine(_yAxisMaterial!, Vector3.Up, "YAxis");
        attachedNode.AddChild(_yAxisLine);

        // Z axis (blue)
        _zAxisLine = CreateAxisLine(_zAxisMaterial!, Vector3.Back, "ZAxis");
        attachedNode.AddChild(_zAxisLine);
    }

    private MeshInstance3D CreateAxisLine(StandardMaterial3D material, Vector3 direction, string name)
    {
        var line = new MeshInstance3D();
        line.Name = name;

        var cylinder = new CylinderMesh();
        cylinder.TopRadius = 0.002f;
        cylinder.BottomRadius = 0.002f;
        cylinder.Height = 1f;

        line.Mesh = cylinder;
        line.MaterialOverride = material;

        // Rotate to point in the correct direction
        if (direction == Vector3.Right)
        {
            line.RotationDegrees = new Vector3(0, 0, 90);
        }
        else if (direction == Vector3.Back)
        {
            line.RotationDegrees = new Vector3(90, 0, 0);
        }
        // Y axis is already pointing up by default

        return line;
    }

    private void CreateNameLabel()
    {
        _nameLabel = new Label3D();
        _nameLabel.Name = "NameLabel";
        _nameLabel.Text = Owner.TargetSlot?.Name.Value ?? "Unknown";
        _nameLabel.FontSize = 32;
        _nameLabel.Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
        _nameLabel.NoDepthTest = true;
        _nameLabel.Modulate = new Color(1f, 1f, 1f, 1f);
        _nameLabel.OutlineModulate = new Color(0.2f, 0f, 0.2f, 1f);
        _nameLabel.OutlineSize = 4;
        _nameLabel.PixelSize = 0.001f;

        attachedNode.AddChild(_nameLabel);
    }

    private void CreateToolbar()
    {
        _toolbarRoot = new Node3D();
        _toolbarRoot.Name = "Toolbar";
        attachedNode.AddChild(_toolbarRoot);

        float buttonSize = SlotGizmo.BUTTON_SIZE;
        float separation = SlotGizmo.BUTTON_SEPARATION;
        float totalWidth = buttonSize * 5 + separation * 4;
        float startX = -totalWidth / 2 + buttonSize / 2;

        // Translate button
        _translateButton = CreateToolbarButton("Translate", startX, buttonSize, XAxisColor);
        _translateArea = CreateButtonCollision(_translateButton, buttonSize);
        _translateArea.InputEvent += OnTranslateButtonPressed;

        // Rotate button
        _rotateButton = CreateToolbarButton("Rotate", startX + (buttonSize + separation), buttonSize, YAxisColor);
        _rotateArea = CreateButtonCollision(_rotateButton, buttonSize);
        _rotateArea.InputEvent += OnRotateButtonPressed;

        // Scale button
        _scaleButton = CreateToolbarButton("Scale", startX + (buttonSize + separation) * 2, buttonSize, ZAxisColor);
        _scaleArea = CreateButtonCollision(_scaleButton, buttonSize);
        _scaleArea.InputEvent += OnScaleButtonPressed;

        // Space toggle button
        _spaceButton = CreateToolbarButton("Space", startX + (buttonSize + separation) * 3, buttonSize, new Color(0.5f, 0.5f, 0.5f));
        _spaceArea = CreateButtonCollision(_spaceButton, buttonSize);
        _spaceArea.InputEvent += OnSpaceButtonPressed;

        // Parent button
        _parentButton = CreateToolbarButton("Parent", startX + (buttonSize + separation) * 4, buttonSize, new Color(1f, 0.5f, 0f));
        _parentArea = CreateButtonCollision(_parentButton, buttonSize);
        _parentArea.InputEvent += OnParentButtonPressed;
    }

    private MeshInstance3D CreateToolbarButton(string name, float xPos, float size, Color color)
    {
        var button = new MeshInstance3D();
        button.Name = name;

        var sphere = new SphereMesh();
        sphere.Radius = size / 2;
        sphere.Height = size;

        button.Mesh = sphere;
        button.MaterialOverride = CreateOverlayMaterial(color);
        button.Position = new Vector3(xPos, 0, 0);

        _toolbarRoot!.AddChild(button);
        return button;
    }

    private Area3D CreateButtonCollision(MeshInstance3D button, float size)
    {
        var area = new Area3D();
        area.Name = button.Name + "Area";

        var shape = new CollisionShape3D();
        var sphere = new SphereShape3D();
        sphere.Radius = size / 2;
        shape.Shape = sphere;

        area.AddChild(shape);
        button.AddChild(area);

        // Set collision layer to UI layer (4)
        area.CollisionLayer = 1u << 3;
        area.CollisionMask = 0;
        area.Monitorable = true;
        area.Monitoring = false;

        return area;
    }

    private void OnTranslateButtonPressed(Node camera, InputEvent @event, Vector3 position, Vector3 normal, long shapeIdx)
    {
        if (@event is InputEventMouseButton mouseEvent && mouseEvent.Pressed && mouseEvent.ButtonIndex == MouseButton.Left)
        {
            Owner.SwitchToTranslation();
        }
    }

    private void OnRotateButtonPressed(Node camera, InputEvent @event, Vector3 position, Vector3 normal, long shapeIdx)
    {
        if (@event is InputEventMouseButton mouseEvent && mouseEvent.Pressed && mouseEvent.ButtonIndex == MouseButton.Left)
        {
            Owner.SwitchToRotation();
        }
    }

    private void OnScaleButtonPressed(Node camera, InputEvent @event, Vector3 position, Vector3 normal, long shapeIdx)
    {
        if (@event is InputEventMouseButton mouseEvent && mouseEvent.Pressed && mouseEvent.ButtonIndex == MouseButton.Left)
        {
            Owner.SwitchToScale();
        }
    }

    private void OnSpaceButtonPressed(Node camera, InputEvent @event, Vector3 position, Vector3 normal, long shapeIdx)
    {
        if (@event is InputEventMouseButton mouseEvent && mouseEvent.Pressed && mouseEvent.ButtonIndex == MouseButton.Left)
        {
            Owner.ToggleSpace();
        }
    }

    private void OnParentButtonPressed(Node camera, InputEvent @event, Vector3 position, Vector3 normal, long shapeIdx)
    {
        if (@event is InputEventMouseButton mouseEvent && mouseEvent.Pressed && mouseEvent.ButtonIndex == MouseButton.Left)
        {
            Owner.OpenParent();
        }
    }

    public override void ApplyChanges()
    {
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        if (Owner.TargetSlot == null)
            return;

        var target = Owner.TargetSlot;

        // Keep gizmo anchored to target transform (local mode) or world axes (global mode)
        attachedNode.GlobalPosition = ToGodotVector(target.GlobalPosition);
        attachedNode.GlobalRotation = Owner.IsLocalSpace.Value
            ? ToGodotVector(target.GlobalRotation.ToEuler())
            : Vector3.Zero;
        attachedNode.Scale = Vector3.One;

        // Update bounds around target geometry and draw wireframe box.
        UpdateBoundingBox(target);

        // Update axis triad centered on current bounds.
        UpdateAxisLines();

        // Update name label
        if (_nameLabel != null)
        {
            _nameLabel.Text = target.Name.Value;
        }

        // Place orb controls above the object center/top.
        UpdateToolbarPosition();

        // Update visibility
        bool visible = Owner.Active.Value && !Owner.IsFolded.Value;
        if (_boundsMesh != null) _boundsMesh.Visible = visible;
        if (_xAxisLine != null) _xAxisLine.Visible = visible;
        if (_yAxisLine != null) _yAxisLine.Visible = visible;
        if (_zAxisLine != null) _zAxisLine.Visible = visible;
        if (_nameLabel != null) _nameLabel.Visible = visible;
        if (_toolbarRoot != null) _toolbarRoot.Visible = visible;

        // Highlight active mode button
        UpdateModeHighlight();
    }

    private void UpdateBoundingBox(Slot target)
    {
        if (_boundsImmediateMesh == null || _boundsMesh == null)
            return;

        if (!TryComputeBounds(target, out var bounds))
        {
            bounds = new Aabb(new Vector3(-0.1f, -0.1f, -0.1f), new Vector3(0.2f, 0.2f, 0.2f));
        }

        _boundsCenter = bounds.Position + (bounds.Size * 0.5f);
        _boundsSize = new Vector3(
            Mathf.Max(bounds.Size.X, 0.05f),
            Mathf.Max(bounds.Size.Y, 0.05f),
            Mathf.Max(bounds.Size.Z, 0.05f));

        DrawWireBounds(_boundsCenter, _boundsSize);
    }

    private void UpdateAxisLines()
    {
        var maxDimension = Mathf.Max(Mathf.Max(_boundsSize.X, _boundsSize.Y), _boundsSize.Z);
        var axisLength = Mathf.Max(maxDimension * 0.6f, 0.12f);

        if (_xAxisLine != null)
        {
            _xAxisLine.Position = _boundsCenter + new Vector3(axisLength * 0.5f, 0f, 0f);
            if (_xAxisLine.Mesh is CylinderMesh cyl)
                cyl.Height = axisLength;
        }

        if (_yAxisLine != null)
        {
            _yAxisLine.Position = _boundsCenter + new Vector3(0f, axisLength * 0.5f, 0f);
            if (_yAxisLine.Mesh is CylinderMesh cyl)
                cyl.Height = axisLength;
        }

        if (_zAxisLine != null)
        {
            _zAxisLine.Position = _boundsCenter + new Vector3(0f, 0f, -axisLength * 0.5f);
            if (_zAxisLine.Mesh is CylinderMesh cyl)
                cyl.Height = axisLength;
        }
    }

    private void UpdateToolbarPosition()
    {
        if (_toolbarRoot == null || _nameLabel == null)
            return;

        // Position toolbar above center/top of bounds.
        float topY = _boundsCenter.Y + (_boundsSize.Y * 0.5f);
        _toolbarRoot.Position = new Vector3(
            _boundsCenter.X,
            topY + SlotGizmo.BUTTONS_OFFSET + 0.015f,
            _boundsCenter.Z);

        // Name label above toolbar.
        _nameLabel.Position = _toolbarRoot.Position + new Vector3(0f, SlotGizmo.BUTTON_SIZE + 0.02f, 0f);
    }

    private bool TryComputeBounds(Slot target, out Aabb boundsInGizmoSpace)
    {
        boundsInGizmoSpace = default;

        if (target.Hook is not SlotHook slotHook)
            return false;

        var targetNode = slotHook.ForceGetNode3D();
        if (!GodotObject.IsInstanceValid(targetNode))
            return false;

        if (!TryComputeWorldBounds(targetNode, out var worldBounds))
            return false;

        var worldToGizmo = attachedNode.GlobalTransform.AffineInverse();
        boundsInGizmoSpace = TransformAabb(worldToGizmo, worldBounds);
        return true;
    }

    private static bool TryComputeWorldBounds(Node3D targetNode, out Aabb worldBounds)
    {
        worldBounds = default;
        bool hasBounds = false;

        var stack = new Stack<Node>();
        stack.Push(targetNode);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (Node child in current.GetChildren())
            {
                stack.Push(child);
            }

            if (current is not Node3D node3D || !node3D.Visible)
                continue;

            if (TryGetLocalAabb(node3D, out var localBounds))
            {
                var transformed = TransformAabb(node3D.GlobalTransform, localBounds);
                AddAabb(ref worldBounds, transformed, ref hasBounds);
            }
        }

        return hasBounds;
    }

    private static bool TryGetLocalAabb(Node3D node, out Aabb localBounds)
    {
        switch (node)
        {
            case MeshInstance3D meshInstance when meshInstance.Mesh != null:
                localBounds = meshInstance.GetAabb();
                return localBounds.Size != Vector3.Zero;

            case CollisionShape3D collisionShape when collisionShape.Shape != null:
                return TryGetShapeLocalAabb(collisionShape.Shape, out localBounds);

            default:
                localBounds = default;
                return false;
        }
    }

    private static bool TryGetShapeLocalAabb(Shape3D shape, out Aabb localBounds)
    {
        switch (shape)
        {
            case BoxShape3D box:
                localBounds = new Aabb(-box.Size * 0.5f, box.Size);
                return true;

            case SphereShape3D sphere:
                var diameter = sphere.Radius * 2f;
                localBounds = new Aabb(
                    new Vector3(-sphere.Radius, -sphere.Radius, -sphere.Radius),
                    new Vector3(diameter, diameter, diameter));
                return true;

            case CapsuleShape3D capsule:
                var capsuleHalfHeight = (capsule.Height + (capsule.Radius * 2f)) * 0.5f;
                var capsuleDiameter = capsule.Radius * 2f;
                localBounds = new Aabb(
                    new Vector3(-capsule.Radius, -capsuleHalfHeight, -capsule.Radius),
                    new Vector3(capsuleDiameter, capsuleHalfHeight * 2f, capsuleDiameter));
                return true;

            case CylinderShape3D cylinder:
                var cylinderHalfHeight = cylinder.Height * 0.5f;
                var cylinderDiameter = cylinder.Radius * 2f;
                localBounds = new Aabb(
                    new Vector3(-cylinder.Radius, -cylinderHalfHeight, -cylinder.Radius),
                    new Vector3(cylinderDiameter, cylinder.Height, cylinderDiameter));
                return true;

            default:
                localBounds = default;
                return false;
        }
    }

    private static Aabb TransformAabb(Transform3D transform, Aabb source)
    {
        var corners = GetAabbCorners(source);
        var transformedMin = transform * corners[0];
        var transformedMax = transformedMin;

        for (int i = 1; i < corners.Length; i++)
        {
            var p = transform * corners[i];
            transformedMin = transformedMin.Min(p);
            transformedMax = transformedMax.Max(p);
        }

        return new Aabb(transformedMin, transformedMax - transformedMin);
    }

    private static Vector3[] GetAabbCorners(Aabb bounds)
    {
        var min = bounds.Position;
        var max = bounds.End;
        return new[]
        {
            new Vector3(min.X, min.Y, min.Z),
            new Vector3(max.X, min.Y, min.Z),
            new Vector3(min.X, max.Y, min.Z),
            new Vector3(max.X, max.Y, min.Z),
            new Vector3(min.X, min.Y, max.Z),
            new Vector3(max.X, min.Y, max.Z),
            new Vector3(min.X, max.Y, max.Z),
            new Vector3(max.X, max.Y, max.Z)
        };
    }

    private static void AddAabb(ref Aabb aggregate, Aabb next, ref bool hasBounds)
    {
        if (!hasBounds)
        {
            aggregate = next;
            hasBounds = true;
            return;
        }

        var min = aggregate.Position.Min(next.Position);
        var max = aggregate.End.Max(next.End);
        aggregate = new Aabb(min, max - min);
    }

    private void DrawWireBounds(Vector3 center, Vector3 size)
    {
        if (_boundsImmediateMesh == null || _boundsMaterial == null)
            return;

        _boundsImmediateMesh.ClearSurfaces();

        var half = size * 0.5f;
        var p000 = center + new Vector3(-half.X, -half.Y, -half.Z);
        var p100 = center + new Vector3(half.X, -half.Y, -half.Z);
        var p010 = center + new Vector3(-half.X, half.Y, -half.Z);
        var p110 = center + new Vector3(half.X, half.Y, -half.Z);
        var p001 = center + new Vector3(-half.X, -half.Y, half.Z);
        var p101 = center + new Vector3(half.X, -half.Y, half.Z);
        var p011 = center + new Vector3(-half.X, half.Y, half.Z);
        var p111 = center + new Vector3(half.X, half.Y, half.Z);

        _boundsImmediateMesh.SurfaceBegin(Mesh.PrimitiveType.Lines, _boundsMaterial);

        // Bottom
        _boundsImmediateMesh.SurfaceAddVertex(p000); _boundsImmediateMesh.SurfaceAddVertex(p100);
        _boundsImmediateMesh.SurfaceAddVertex(p100); _boundsImmediateMesh.SurfaceAddVertex(p101);
        _boundsImmediateMesh.SurfaceAddVertex(p101); _boundsImmediateMesh.SurfaceAddVertex(p001);
        _boundsImmediateMesh.SurfaceAddVertex(p001); _boundsImmediateMesh.SurfaceAddVertex(p000);

        // Top
        _boundsImmediateMesh.SurfaceAddVertex(p010); _boundsImmediateMesh.SurfaceAddVertex(p110);
        _boundsImmediateMesh.SurfaceAddVertex(p110); _boundsImmediateMesh.SurfaceAddVertex(p111);
        _boundsImmediateMesh.SurfaceAddVertex(p111); _boundsImmediateMesh.SurfaceAddVertex(p011);
        _boundsImmediateMesh.SurfaceAddVertex(p011); _boundsImmediateMesh.SurfaceAddVertex(p010);

        // Vertical
        _boundsImmediateMesh.SurfaceAddVertex(p000); _boundsImmediateMesh.SurfaceAddVertex(p010);
        _boundsImmediateMesh.SurfaceAddVertex(p100); _boundsImmediateMesh.SurfaceAddVertex(p110);
        _boundsImmediateMesh.SurfaceAddVertex(p101); _boundsImmediateMesh.SurfaceAddVertex(p111);
        _boundsImmediateMesh.SurfaceAddVertex(p001); _boundsImmediateMesh.SurfaceAddVertex(p011);

        _boundsImmediateMesh.SurfaceEnd();
    }

    private void UpdateModeHighlight()
    {
        // Reset all buttons to normal color
        SetButtonHighlight(_translateButton, false);
        SetButtonHighlight(_rotateButton, false);
        SetButtonHighlight(_scaleButton, false);

        // Highlight active mode
        switch (Owner.ActiveMode.Value)
        {
            case 0:
                SetButtonHighlight(_translateButton, true);
                break;
            case 1:
                SetButtonHighlight(_rotateButton, true);
                break;
            case 2:
                SetButtonHighlight(_scaleButton, true);
                break;
        }

        // Update space button color based on local/global
        if (_spaceButton?.MaterialOverride is StandardMaterial3D spaceMat)
        {
            spaceMat.AlbedoColor = Owner.IsLocalSpace.Value
                ? new Color(0.5f, 0.5f, 0.5f)
                : new Color(0.2f, 0.8f, 0.8f);
        }
    }

    private void SetButtonHighlight(MeshInstance3D? button, bool highlight)
    {
        if (button?.MaterialOverride is StandardMaterial3D mat)
        {
            var baseColor = mat.AlbedoColor;
            mat.AlbedoColor = new Color(
                baseColor.R,
                baseColor.G,
                baseColor.B,
                highlight ? 1f : 0.6f
            );
        }
    }

    private static Vector3 ToGodotVector(float3 v) => new(v.x, v.y, v.z);

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld)
        {
            _boundsMesh?.QueueFree();
            _xAxisLine?.QueueFree();
            _yAxisLine?.QueueFree();
            _zAxisLine?.QueueFree();
            _nameLabel?.QueueFree();
            _toolbarRoot?.QueueFree();

            _boundsMaterial?.Dispose();
            _xAxisMaterial?.Dispose();
            _yAxisMaterial?.Dispose();
            _zAxisMaterial?.Dispose();
            _highlightMaterial?.Dispose();
            _boundsImmediateMesh?.Dispose();
        }

        _boundsMesh = null;
        _xAxisLine = null;
        _yAxisLine = null;
        _zAxisLine = null;
        _nameLabel = null;
        _toolbarRoot = null;
        _boundsMaterial = null;
        _xAxisMaterial = null;
        _yAxisMaterial = null;
        _zAxisMaterial = null;
        _highlightMaterial = null;
        _boundsImmediateMesh = null;

        base.Destroy(destroyingWorld);
    }
}
