using Godot;
using Lumora.Core;
using Lumora.Core.GodotUI;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks.GodotUI;

#nullable enable

/// <summary>
/// Godot hook for GodotSceneCanvas.
/// Loads a .tscn scene and renders it to a 3D quad via SubViewport.
/// </summary>
public class GodotSceneCanvasHook : ComponentHook<GodotSceneCanvas>
{
    private SubViewport? _viewport;
    private MeshInstance3D? _meshInstance;
    private QuadMesh? _quadMesh;
    private StandardMaterial3D? _material;
    private Node? _loadedScene;
    private string _currentScenePath = "";

    /// <summary>
    /// The SubViewport containing the loaded scene.
    /// </summary>
    public SubViewport? Viewport => _viewport;

    /// <summary>
    /// The root node of the loaded scene.
    /// </summary>
    public Node? LoadedScene => _loadedScene;

    public static IHook<GodotSceneCanvas> Constructor()
    {
        return new GodotSceneCanvasHook();
    }

    public override void Initialize()
    {
        base.Initialize();

        // Create SubViewport
        _viewport = new SubViewport();
        _viewport.Name = "SceneViewport";
        _viewport.Size = new Vector2I((int)Owner.Size.Value.x, (int)Owner.Size.Value.y);
        _viewport.TransparentBg = Owner.TransparentBackground.Value;
        _viewport.HandleInputLocally = true;
        _viewport.GuiDisableInput = !Owner.Interactive.Value;
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;

        // Create mesh to display the viewport texture
        _meshInstance = new MeshInstance3D();
        _meshInstance.Name = "SceneQuad";

        _quadMesh = new QuadMesh();
        UpdateQuadSize();
        _meshInstance.Mesh = _quadMesh;

        // Create material with viewport texture
        _material = new StandardMaterial3D();
        _material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        _material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        _material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        _material.AlbedoTexture = _viewport.GetTexture();
        _meshInstance.MaterialOverride = _material;

        // Add to scene
        attachedNode.AddChild(_viewport);
        attachedNode.AddChild(_meshInstance);

        // Load initial scene if path is set
        if (!string.IsNullOrEmpty(Owner.ScenePath.Value))
        {
            LoadScene(Owner.ScenePath.Value);
        }

        AquaLogger.Log($"GodotSceneCanvasHook: Initialized with size {Owner.Size.Value}");
    }

    public override void ApplyChanges()
    {
        if (_viewport == null) return;

        // Update viewport settings
        _viewport.Size = new Vector2I((int)Owner.Size.Value.x, (int)Owner.Size.Value.y);
        _viewport.TransparentBg = Owner.TransparentBackground.Value;
        _viewport.GuiDisableInput = !Owner.Interactive.Value;

        // Update quad size
        UpdateQuadSize();

        // Update material texture
        if (_material != null)
        {
            _material.AlbedoTexture = _viewport.GetTexture();
        }

        // Reload scene if path changed
        if (Owner.ScenePath.Value != _currentScenePath)
        {
            LoadScene(Owner.ScenePath.Value);
        }
    }

    private void LoadScene(string path)
    {
        // Remove old scene
        if (_loadedScene != null && GodotObject.IsInstanceValid(_loadedScene))
        {
            _loadedScene.QueueFree();
            _loadedScene = null;
        }

        _currentScenePath = path;

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        // Load new scene
        var packedScene = GD.Load<PackedScene>(path);
        if (packedScene == null)
        {
            AquaLogger.Warn($"GodotSceneCanvasHook: Failed to load scene '{path}'");
            return;
        }

        _loadedScene = packedScene.Instantiate();
        if (_loadedScene == null)
        {
            AquaLogger.Warn($"GodotSceneCanvasHook: Failed to instantiate scene '{path}'");
            return;
        }

        _viewport?.AddChild(_loadedScene);

        // If the scene root is a Control, make it fill the viewport
        if (_loadedScene is Control control)
        {
            control.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        }

        AquaLogger.Log($"GodotSceneCanvasHook: Loaded scene '{path}'");
        Owner.NotifySceneLoaded();
    }

    private void UpdateQuadSize()
    {
        if (_quadMesh == null) return;

        var size = Owner.Size.Value;
        var ppu = Owner.PixelsPerUnit.Value;

        _quadMesh.Size = new Vector2(size.x / ppu, size.y / ppu);
    }

    /// <summary>
    /// Get a control by node path within the loaded scene.
    /// </summary>
    public Control? GetControl(string nodePath)
    {
        if (_loadedScene == null || string.IsNullOrEmpty(nodePath))
            return null;

        var node = _loadedScene.GetNodeOrNull(nodePath);
        return node as Control;
    }

    /// <summary>
    /// Get any node by path within the loaded scene.
    /// </summary>
    public Node? GetNode(string nodePath)
    {
        if (_loadedScene == null || string.IsNullOrEmpty(nodePath))
            return null;

        return _loadedScene.GetNodeOrNull(nodePath);
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld)
        {
            if (_loadedScene != null && GodotObject.IsInstanceValid(_loadedScene))
            {
                _loadedScene.QueueFree();
            }
            _viewport?.QueueFree();
            _meshInstance?.QueueFree();
            _material?.Dispose();
            _quadMesh?.Dispose();
        }

        _loadedScene = null;
        _viewport = null;
        _meshInstance = null;
        _material = null;
        _quadMesh = null;

        base.Destroy(destroyingWorld);
    }
}
