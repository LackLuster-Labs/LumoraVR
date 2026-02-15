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
    private BoxMesh? _boxMesh;

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
    private static readonly Color BoundsColor = new(1f, 0f, 1f, 0.5f); // Magenta
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

        _boxMesh = new BoxMesh();
        _boxMesh.Size = Vector3.One; // Will be updated based on slot bounds
        _boundsMesh.Mesh = _boxMesh;
        _boundsMesh.MaterialOverride = _boundsMaterial;

        // Make it wireframe-like by using a custom shader or thin box
        // For now, use semi-transparent box

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

        // Update position to match target
        attachedNode.GlobalPosition = ToGodotVector(target.GlobalPosition);

        // Update bounding box
        UpdateBoundingBox(target);

        // Update axis lines
        UpdateAxisLines(target);

        // Update name label
        if (_nameLabel != null)
        {
            _nameLabel.Text = target.Name.Value;
        }

        // Update toolbar position (above bounding box)
        UpdateToolbarPosition(target);

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
        if (_boxMesh == null || _boundsMesh == null)
            return;

        // Compute a simple bounding box based on the slot's children
        // For now, use a default size
        float size = 0.2f;
        _boxMesh.Size = new Vector3(size, size, size);

        // Use rotation from target if in local space
        if (Owner.IsLocalSpace.Value)
        {
            _boundsMesh.GlobalRotation = ToGodotVector(target.GlobalRotation.ToEuler());
        }
        else
        {
            _boundsMesh.Rotation = Vector3.Zero;
        }
    }

    private void UpdateAxisLines(Slot target)
    {
        // Draw lines from slot position to parent-relative axes
        var localPos = target.Position;

        if (_xAxisLine != null)
        {
            float length = System.Math.Abs(localPos.x);
            _xAxisLine.Position = new Vector3(-localPos.x / 2, 0, 0);
            if (_xAxisLine.Mesh is CylinderMesh cyl)
                cyl.Height = length > 0.001f ? length : 0.001f;
            _xAxisLine.Visible = length > 0.001f && Owner.Active.Value;
        }

        if (_yAxisLine != null)
        {
            float length = System.Math.Abs(localPos.y);
            _yAxisLine.Position = new Vector3(0, -localPos.y / 2, 0);
            if (_yAxisLine.Mesh is CylinderMesh cyl)
                cyl.Height = length > 0.001f ? length : 0.001f;
            _yAxisLine.Visible = length > 0.001f && Owner.Active.Value;
        }

        if (_zAxisLine != null)
        {
            float length = System.Math.Abs(localPos.z);
            _zAxisLine.Position = new Vector3(0, 0, -localPos.z / 2);
            if (_zAxisLine.Mesh is CylinderMesh cyl)
                cyl.Height = length > 0.001f ? length : 0.001f;
            _zAxisLine.Visible = length > 0.001f && Owner.Active.Value;
        }
    }

    private void UpdateToolbarPosition(Slot target)
    {
        if (_toolbarRoot == null || _nameLabel == null)
            return;

        // Position toolbar above the bounding box
        float height = 0.15f; // Approximate bounding box top + offset
        _toolbarRoot.Position = new Vector3(0, height + SlotGizmo.BUTTONS_OFFSET, 0);

        // Name label above toolbar
        _nameLabel.Position = new Vector3(0, height + SlotGizmo.BUTTONS_OFFSET + SlotGizmo.BUTTON_SIZE + 0.02f, 0);
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
    private static Vector3 ToGodotVector(float3 v, float w) => new(v.x, v.y, v.z);

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
            _boxMesh?.Dispose();
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
        _boxMesh = null;

        base.Destroy(destroyingWorld);
    }
}
