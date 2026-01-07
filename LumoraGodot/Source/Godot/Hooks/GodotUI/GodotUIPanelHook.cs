using Godot;
using Lumora.Core;
using Lumora.Core.GodotUI;
using Lumora.Core.Math;
using System.Collections.Generic;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks.GodotUI;

#nullable enable

/// <summary>
/// Universal Godot hook for UI panels that render to a 3D quad.
/// Parses loaded .tscn scenes and creates Lumora components for each UI element.
/// Includes collision shape for laser pointer interaction.
/// </summary>
public class GodotUIPanelHook : ComponentHook<GodotUIPanel>
{
    private SubViewport? _viewport;
    private MeshInstance3D? _meshInstance;
    private QuadMesh? _quadMesh;
    private StandardMaterial3D? _material;
    private Node? _loadedScene;

    // Collision for touch interaction
    private Area3D? _collisionArea;
    private CollisionShape3D? _collisionShape;
    private BoxShape3D? _boxShape;

    // Registry of scene node paths to their Control instances
    private readonly Dictionary<string, Control> _nodeRegistry = new();

    // Static registry for hooks (so element hooks can find their nodes)
    private static readonly Dictionary<GodotUIPanel, GodotUIPanelHook> _panelHooks = new();

    public static IHook<GodotUIPanel> Constructor()
    {
        return new GodotUIPanelHook();
    }

    /// <summary>
    /// Get a Control node by path from this panel's scene.
    /// </summary>
    public Control? GetNodeByPath(string path)
    {
        return _nodeRegistry.TryGetValue(path, out var node) ? node : null;
    }

    /// <summary>
    /// Find the panel hook for a given panel component.
    /// </summary>
    public static GodotUIPanelHook? FindHookForPanel(GodotUIPanel? panel)
    {
        if (panel == null) return null;
        return _panelHooks.TryGetValue(panel, out var hook) ? hook : null;
    }

    public override void Initialize()
    {
        base.Initialize();

        _panelHooks[Owner] = this;

        var resScale = Owner.ResolutionScale.Value;

        // Create SubViewport
        _viewport = new SubViewport();
        _viewport.Name = "UIPanelViewport";
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
        _meshInstance.Name = "UIPanelQuad";

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

        // Create collision area for touch interaction
        CreateCollisionArea();

        LoadScene();

        Owner.OnDataRefresh += RefreshUIData;

        AquaLogger.Log($"GodotUIPanelHook: Initialized for {Owner.GetType().Name}");
    }

    private void LoadScene()
    {
        var scenePath = Owner.ScenePath.Value;
        if (string.IsNullOrEmpty(scenePath))
        {
            AquaLogger.Warn("GodotUIPanelHook: No scene path specified");
            return;
        }

        var packedScene = GD.Load<PackedScene>(scenePath);
        if (packedScene == null)
        {
            AquaLogger.Warn($"GodotUIPanelHook: Failed to load scene '{scenePath}'");
            return;
        }

        _loadedScene = packedScene.Instantiate();
        if (_loadedScene == null)
        {
            AquaLogger.Warn("GodotUIPanelHook: Failed to instantiate scene");
            return;
        }

        _viewport?.AddChild(_loadedScene);

        // Build node registry and create Lumora components
        // Start from children of root (skip root node name in paths)
        _nodeRegistry.Clear();
        foreach (var child in _loadedScene.GetChildren())
        {
            ParseSceneNode(child, "", Owner.Slot);
        }

        // Notify component
        Owner.NotifySceneLoaded(new List<string>(_nodeRegistry.Keys));

        RefreshUIData();
        ResetScrollPositions();

        AquaLogger.Log($"GodotUIPanelHook: Scene loaded, created {_nodeRegistry.Count} UI element components");
    }

    private void ParseSceneNode(Node node, string parentPath, Slot parentSlot)
    {
        var nodeName = node.Name.ToString();
        var nodePath = string.IsNullOrEmpty(parentPath) ? nodeName : $"{parentPath}/{nodeName}";

        // Skip non-Control nodes
        if (node is not Control control)
        {
            // Still recurse into children
            foreach (var child in node.GetChildren())
            {
                ParseSceneNode(child, nodePath, parentSlot);
            }
            return;
        }

        // Register this control
        _nodeRegistry[nodePath] = control;

        // Create child slot for this UI element
        var elementSlot = parentSlot.AddSlot(nodeName);

        // Create appropriate component based on Control type
        GodotUIElement? element = null;

        if (control is Label)
        {
            element = elementSlot.AttachComponent<GodotLabel>();
        }
        else if (control is Button)
        {
            element = elementSlot.AttachComponent<GodotButton>();
            // Wire up button press
            var btn = (Button)control;
            var capturedPath = nodePath;
            btn.Pressed += () => Owner.HandleButtonPress(capturedPath);
        }
        else if (control is PanelContainer or Panel)
        {
            element = elementSlot.AttachComponent<GodotPanel>();
        }
        else if (control is ScrollContainer)
        {
            element = elementSlot.AttachComponent<GodotScrollContainer>();
        }
        else
        {
            // Generic UI element for other control types (VBox, HBox, etc.)
            element = elementSlot.AttachComponent<GodotUIElement>();
        }

        if (element != null)
        {
            // Set node path so hook can find the existing Godot node
            element.SceneNodePath.Value = nodePath;
            element.ParentPanel.Target = Owner;

            // Copy initial properties from Godot node to component
            SyncPropertiesFromNode(element, control);
        }

        // Recurse into children
        foreach (var child in node.GetChildren())
        {
            ParseSceneNode(child, nodePath, elementSlot);
        }
    }

    private void SyncPropertiesFromNode(GodotUIElement element, Control control)
    {
        // Sync visibility
        element.Visible.Value = control.Visible;

        // Sync modulate
        var mod = control.Modulate;
        element.Modulate.Value = new Lumora.Core.Math.color(mod.R, mod.G, mod.B, mod.A);

        // Sync min size
        var minSize = control.CustomMinimumSize;
        element.MinSize.Value = new Lumora.Core.Math.float2(minSize.X, minSize.Y);

        // Sync size flags
        element.SizeFlagsHorizontal.Value = (int)control.SizeFlagsHorizontal;
        element.SizeFlagsVertical.Value = (int)control.SizeFlagsVertical;

        // Sync specific properties for label
        if (element is GodotLabel label && control is Label godotLabel)
        {
            label.Text.Value = godotLabel.Text;
            // Font size would need theme lookup, skip for now
        }

        // Sync specific properties for button
        if (element is GodotButton button && control is Button godotButton)
        {
            button.Text.Value = godotButton.Text;
        }
    }

    private void ResetScrollPositions()
    {
        foreach (var (_, control) in _nodeRegistry)
        {
            if (control is ScrollContainer scroll)
            {
                scroll.ScrollVertical = 0;
                scroll.ScrollHorizontal = 0;
            }
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

        // Update collision shape to match
        UpdateCollisionSize();
    }

    private void CreateCollisionArea()
    {
        // Create Area3D for collision detection
        _collisionArea = new Area3D();
        _collisionArea.Name = "UIPanelCollision";
        _collisionArea.Monitorable = true;
        _collisionArea.Monitoring = false;

        // Set collision layer to UI layer (layer 4 = bit 3)
        _collisionArea.CollisionLayer = 1u << 3;
        _collisionArea.CollisionMask = 0;

        // Create collision shape
        _collisionShape = new CollisionShape3D();
        _boxShape = new BoxShape3D();
        UpdateCollisionSize();
        _collisionShape.Shape = _boxShape;

        _collisionArea.AddChild(_collisionShape);
        attachedNode.AddChild(_collisionArea);

        AquaLogger.Log($"GodotUIPanelHook: Created collision area for touch interaction");
    }

    private void UpdateCollisionSize()
    {
        if (_boxShape == null) return;

        var size = Owner.Size.Value;
        var ppu = Owner.PixelsPerUnit.Value;

        // Box shape is same size as quad, but very thin
        _boxShape.Size = new Vector3(size.x / ppu, size.y / ppu, 0.01f);
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

        RefreshUIData();
    }

    private void UnloadScene()
    {
        _nodeRegistry.Clear();
        _loadedScene?.QueueFree();
        _loadedScene = null;
    }

    public override void Destroy(bool destroyingWorld)
    {
        _panelHooks.Remove(Owner);
        Owner.OnDataRefresh -= RefreshUIData;

        if (!destroyingWorld)
        {
            UnloadScene();
            _collisionArea?.QueueFree();
            _viewport?.QueueFree();
            _meshInstance?.QueueFree();
            _material?.Dispose();
            _quadMesh?.Dispose();
            _boxShape?.Dispose();
        }

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
