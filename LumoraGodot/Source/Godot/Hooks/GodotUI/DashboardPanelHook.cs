using Godot;
using Lumora.Core;
using Lumora.Core.GodotUI;
using Lumora.Core.Math;
using System.Collections.Generic;
using Aquamarine.Source.Input;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks.GodotUI;

#nullable enable

/// <summary>
/// Hook for the Dashboard panel that handles visibility toggling.
/// - Desktop: Shows as 2D screen overlay
/// - VR: Shows as 3D panel in front of user
/// </summary>
public class DashboardPanelHook : ComponentHook<DashboardPanel>
{
    // VR mode: 3D panel
    private SubViewport? _viewport;
    private MeshInstance3D? _meshInstance;
    private QuadMesh? _quadMesh;
    private StandardMaterial3D? _material;
    private Area3D? _collisionArea;
    private CollisionShape3D? _collisionShape;
    private BoxShape3D? _boxShape;

    // Desktop mode: 2D overlay
    private CanvasLayer? _canvasLayer;
    private Control? _desktopUI;
    private float _desktopScale = 1f;

    // Shared
    private Node? _loadedScene;
    private readonly Dictionary<string, Control> _nodeRegistry = new();
    private static DashboardPanelHook? _activeDashboard;
    public static DashboardPanelHook? ActiveDashboard => _activeDashboard;
    private bool _isVRMode = false;

    public static IHook<DashboardPanel> Constructor()
    {
        return new DashboardPanelHook();
    }

    public override void Initialize()
    {
        base.Initialize();

        _activeDashboard = this;
        _isVRMode = IInputProvider.Instance?.IsVR ?? false;

        if (_isVRMode)
        {
            InitializeVRMode();
        }
        else
        {
            InitializeDesktopMode();
        }

        Owner.OnDataRefresh += RefreshUIData;
        Owner.IsVisible.Changed += _ => UpdateVisibility();
        UpdateVisibility();

        AquaLogger.Log($"DashboardPanelHook: Initialized in {(_isVRMode ? "VR" : "Desktop")} mode, visible={Owner.IsVisible.Value}");
    }

    private void InitializeDesktopMode()
    {
        // Create CanvasLayer for screen overlay (layer 100 = on top of everything)
        _canvasLayer = new CanvasLayer();
        _canvasLayer.Name = "DashboardCanvas";
        _canvasLayer.Layer = 100;

        // Get the scene tree root to add the canvas layer
        // Use Engine.GetMainLoop() since attachedNode may not be in tree yet
        var sceneTree = global::Godot.Engine.GetMainLoop() as SceneTree;
        sceneTree?.Root?.AddChild(_canvasLayer);

        // Load scene directly as Control
        LoadSceneDesktop();
    }

    private void InitializeVRMode()
    {
        var resScale = Owner.ResolutionScale.Value;

        // Create SubViewport for VR
        _viewport = new SubViewport();
        _viewport.Name = "DashboardViewport";
        _viewport.Size = new Vector2I(
            (int)(Owner.Size.Value.x * resScale),
            (int)(Owner.Size.Value.y * resScale));
        _viewport.TransparentBg = true;
        _viewport.HandleInputLocally = true;
        _viewport.GuiDisableInput = false;
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        _viewport.CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Linear;
        _viewport.Msaa2D = Viewport.Msaa.Msaa4X;

        // Create mesh for 3D display
        _meshInstance = new MeshInstance3D();
        _meshInstance.Name = "DashboardQuad";

        _quadMesh = new QuadMesh();
        UpdateQuadSize();
        _meshInstance.Mesh = _quadMesh;

        // Create material
        _material = new StandardMaterial3D();
        _material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        _material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        _material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        _material.TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear;
        _material.AlbedoTexture = _viewport.GetTexture();
        _meshInstance.MaterialOverride = _material;

        attachedNode.AddChild(_viewport);
        attachedNode.AddChild(_meshInstance);

        // Create collision area for laser interaction
        CreateCollisionArea();

        // Load scene into viewport
        LoadSceneVR();
    }

    private void LoadSceneDesktop()
    {
        var scenePath = Owner.ScenePath.Value;
        if (string.IsNullOrEmpty(scenePath))
        {
            AquaLogger.Warn("DashboardPanelHook: No scene path specified");
            return;
        }

        var packedScene = GD.Load<PackedScene>(scenePath);
        if (packedScene == null)
        {
            AquaLogger.Warn($"DashboardPanelHook: Failed to load scene '{scenePath}'");
            return;
        }

        _loadedScene = packedScene.Instantiate();
        if (_loadedScene == null || _loadedScene is not Control control)
        {
            AquaLogger.Warn("DashboardPanelHook: Failed to instantiate scene as Control");
            return;
        }

        _desktopUI = control;

        _canvasLayer?.AddChild(_desktopUI);
        UpdateDesktopLayout();

        // Build node registry and wire up buttons
        _nodeRegistry.Clear();
        ParseSceneNode(_loadedScene, "");

        Owner.NotifySceneLoaded(new List<string>(_nodeRegistry.Keys));
        AquaLogger.Log($"DashboardPanelHook: Desktop scene loaded with {_nodeRegistry.Count} controls, scale={_desktopScale:F2}");
    }

    private void LoadSceneVR()
    {
        var scenePath = Owner.ScenePath.Value;
        if (string.IsNullOrEmpty(scenePath))
        {
            AquaLogger.Warn("DashboardPanelHook: No scene path specified");
            return;
        }

        var packedScene = GD.Load<PackedScene>(scenePath);
        if (packedScene == null)
        {
            AquaLogger.Warn($"DashboardPanelHook: Failed to load scene '{scenePath}'");
            return;
        }

        _loadedScene = packedScene.Instantiate();
        if (_loadedScene == null)
        {
            AquaLogger.Warn("DashboardPanelHook: Failed to instantiate scene");
            return;
        }

        _viewport?.AddChild(_loadedScene);

        // Build node registry
        _nodeRegistry.Clear();
        ParseSceneNode(_loadedScene, "");

        Owner.NotifySceneLoaded(new List<string>(_nodeRegistry.Keys));
        AquaLogger.Log($"DashboardPanelHook: VR scene loaded with {_nodeRegistry.Count} controls");
    }

    private void ParseSceneNode(Node node, string parentPath)
    {
        var nodeName = node.Name.ToString();
        var nodePath = string.IsNullOrEmpty(parentPath) ? nodeName : $"{parentPath}/{nodeName}";

        if (node is Control control)
        {
            _nodeRegistry[nodePath] = control;

            // Wire up button presses
            if (control is Button btn)
            {
                var capturedPath = nodePath;
                btn.Pressed += () => Owner.HandleButtonPress(capturedPath);
            }
        }

        foreach (var child in node.GetChildren())
        {
            ParseSceneNode(child, nodePath);
        }
    }

    private void RefreshUIData()
    {
        if (_loadedScene == null) return;

        var data = Owner.GetUIData();
        var colors = Owner.GetUIColors();

        foreach (var (path, value) in data)
        {
            if (_nodeRegistry.TryGetValue(path, out var control) && control is Label label)
            {
                label.Text = value;

                if (colors.TryGetValue(path, out var coreColor))
                {
                    label.AddThemeColorOverride("font_color", new Color(coreColor.r, coreColor.g, coreColor.b, coreColor.a));
                }
            }
        }
    }

    private void UpdateQuadSize()
    {
        if (_quadMesh == null) return;

        var size = Owner.Size.Value;
        var ppu = Owner.PixelsPerUnit.Value;

        _quadMesh.Size = new Vector2(size.x / ppu, size.y / ppu);
        UpdateCollisionSize();
    }

    private void CreateCollisionArea()
    {
        _collisionArea = new Area3D();
        _collisionArea.Name = "DashboardCollision";
        _collisionArea.Monitorable = true;
        _collisionArea.Monitoring = false;

        // UI layer (layer 4 = bit 3)
        _collisionArea.CollisionLayer = 1u << 3;
        _collisionArea.CollisionMask = 0;

        _collisionShape = new CollisionShape3D();
        _boxShape = new BoxShape3D();
        UpdateCollisionSize();
        _collisionShape.Shape = _boxShape;

        _collisionArea.AddChild(_collisionShape);
        attachedNode.AddChild(_collisionArea);
    }

    private void UpdateCollisionSize()
    {
        if (_boxShape == null) return;

        var size = Owner.Size.Value;
        var ppu = Owner.PixelsPerUnit.Value;

        _boxShape.Size = new Vector3(size.x / ppu, size.y / ppu, 0.01f);
    }

    private void UpdateVisibility()
    {
        var visible = Owner.IsVisible.Value;

        if (_isVRMode)
        {
            // VR mode: show/hide 3D panel
            if (_meshInstance != null)
                _meshInstance.Visible = visible;

            if (_collisionArea != null)
                _collisionArea.Monitorable = visible;

            // When becoming visible, position in front of user
            if (visible)
            {
                PositionInFrontOfUser();
            }
        }
        else
        {
            // Desktop mode: show/hide 2D overlay
            if (_canvasLayer != null)
                _canvasLayer.Visible = visible;

            if (visible)
            {
                UpdateDesktopLayout();
            }
        }
    }

    private void UpdateDesktopLayout()
    {
        if (_desktopUI == null)
            return;

        var viewport = _desktopUI.GetViewport();
        var windowSize = DisplayServer.WindowGetSize();
        var screenSize = viewport?.GetVisibleRect().Size ?? new Vector2(windowSize.X, windowSize.Y);
        _desktopScale = 1f;

        _desktopUI.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _desktopUI.Position = Vector2.Zero;
        _desktopUI.CustomMinimumSize = screenSize;
        _desktopUI.Scale = Vector2.One;
        _desktopUI.PivotOffset = Vector2.Zero;
    }

    /// <summary>
    /// Position the dashboard panel in front of the user's head.
    /// </summary>
    public void PositionInFrontOfUser()
    {
        var headPos = IInputProvider.LimbPosition(IInputProvider.InputLimb.Head);
        var headRot = IInputProvider.LimbRotation(IInputProvider.InputLimb.Head);

        // Get forward direction (only use yaw, not pitch/roll)
        var forward = headRot * Vector3.Forward;
        forward.Y = 0;
        forward = forward.Normalized();

        if (forward.LengthSquared() < 0.001f)
            forward = Vector3.Forward;

        // Calculate panel position
        var spawnDist = Owner.SpawnDistance.Value;
        var vertOffset = Owner.VerticalOffset.Value;

        var panelPos = headPos + forward * spawnDist + Vector3.Up * vertOffset;

        // Face the user (look at head position)
        var lookDir = (headPos - panelPos).Normalized();
        lookDir.Y = 0;
        if (lookDir.LengthSquared() < 0.001f)
            lookDir = -Vector3.Forward;
        lookDir = lookDir.Normalized();

        var panelRot = Quaternion.FromEuler(new Vector3(0, Mathf.Atan2(lookDir.X, lookDir.Z), 0));

        // Apply to slot
        Owner.Slot.GlobalPosition = new float3(panelPos.X, panelPos.Y, panelPos.Z);
        Owner.Slot.GlobalRotation = new floatQ(panelRot.X, panelRot.Y, panelRot.Z, panelRot.W);
    }

    public override void ApplyChanges()
    {
        if (_viewport == null) return;

        var resScale = Owner.ResolutionScale.Value;
        _viewport.Size = new Vector2I(
            (int)(Owner.Size.Value.x * resScale),
            (int)(Owner.Size.Value.y * resScale));
        UpdateQuadSize();

        if (_material != null)
        {
            _material.AlbedoTexture = _viewport.GetTexture();
        }

        UpdateVisibility();
        RefreshUIData();
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (_activeDashboard == this)
            _activeDashboard = null;

        Owner.OnDataRefresh -= RefreshUIData;

        if (!destroyingWorld)
        {
            // Desktop mode cleanup
            _canvasLayer?.QueueFree();
            _desktopUI = null;

            // VR mode cleanup
            _loadedScene?.QueueFree();
            _collisionArea?.QueueFree();
            _viewport?.QueueFree();
            _meshInstance?.QueueFree();
            _material?.Dispose();
            _quadMesh?.Dispose();
            _boxShape?.Dispose();
        }

        _nodeRegistry.Clear();
        _canvasLayer = null;
        _loadedScene = null;
        _collisionArea = null;
        _collisionShape = null;
        _boxShape = null;
        _viewport = null;
        _meshInstance = null;
        _material = null;
        _quadMesh = null;

        base.Destroy(destroyingWorld);
    }
}
