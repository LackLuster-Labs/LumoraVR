using Godot;
using Lumora.Core;
using Lumora.Core.GodotUI;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks.GodotUI;

#nullable enable

/// <summary>
/// Godot hook for GodotUICanvas.
/// Creates a SubViewport for UI rendering and displays it on a 3D quad.
/// </summary>
public class GodotUICanvasHook : ComponentHook<GodotUICanvas>
{
    private SubViewport? _viewport;
    private MeshInstance3D? _meshInstance;
    private QuadMesh? _quadMesh;
    private StandardMaterial3D? _material;
    private Control? _rootControl;

    /// <summary>
    /// The SubViewport where UI is rendered. Child element hooks add their nodes here.
    /// </summary>
    public SubViewport? Viewport => _viewport;

    /// <summary>
    /// The root Control node. UI elements are added as children of this.
    /// </summary>
    public Control? RootControl => _rootControl;

    public static IHook<GodotUICanvas> Constructor()
    {
        return new GodotUICanvasHook();
    }

    public override void Initialize()
    {
        base.Initialize();

        // Create SubViewport for UI rendering
        _viewport = new SubViewport();
        _viewport.Name = "UIViewport";
        _viewport.Size = new Vector2I((int)Owner.Size.Value.x, (int)Owner.Size.Value.y);
        _viewport.TransparentBg = Owner.TransparentBackground.Value;
        _viewport.HandleInputLocally = true;
        _viewport.GuiDisableInput = !Owner.Interactive.Value;
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;

        // Set background color if not transparent
        if (!Owner.TransparentBackground.Value)
        {
            var bg = Owner.BackgroundColor.Value;
            _viewport.GetParent()?.GetTree()?.Root?.GetNode<WorldEnvironment>("WorldEnvironment")?.Environment?.SetBgColor(new Color(bg.r, bg.g, bg.b, bg.a));
        }

        // Create root Control to hold all UI elements
        _rootControl = new Control();
        _rootControl.Name = "RootControl";
        _rootControl.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _viewport.AddChild(_rootControl);

        // Create mesh to display the viewport texture
        _meshInstance = new MeshInstance3D();
        _meshInstance.Name = "UIQuad";

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

        AquaLogger.Log($"GodotUICanvasHook: Initialized with size {Owner.Size.Value}");
    }

    public override void ApplyChanges()
    {
        if (_viewport == null) return;

        // Update viewport size
        _viewport.Size = new Vector2I((int)Owner.Size.Value.x, (int)Owner.Size.Value.y);
        _viewport.TransparentBg = Owner.TransparentBackground.Value;
        _viewport.GuiDisableInput = !Owner.Interactive.Value;

        // Update quad size
        UpdateQuadSize();

        // Update material texture (in case viewport was recreated)
        if (_material != null)
        {
            _material.AlbedoTexture = _viewport.GetTexture();
        }
    }

    private void UpdateQuadSize()
    {
        if (_quadMesh == null) return;

        var size = Owner.Size.Value;
        var ppu = Owner.PixelsPerUnit.Value;

        // Convert pixels to world units
        _quadMesh.Size = new Vector2(size.x / ppu, size.y / ppu);
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld)
        {
            _rootControl?.QueueFree();
            _viewport?.QueueFree();
            _meshInstance?.QueueFree();
            _material?.Dispose();
            _quadMesh?.Dispose();
        }

        _rootControl = null;
        _viewport = null;
        _meshInstance = null;
        _material = null;
        _quadMesh = null;

        base.Destroy(destroyingWorld);
    }

    /// <summary>
    /// Find the canvas hook for a UI element by traversing parent slots.
    /// </summary>
    public static GodotUICanvasHook? FindCanvasForElement(GodotUIElement element)
    {
        var slot = element.Slot?.Parent;
        while (slot != null)
        {
            var canvas = slot.GetComponent<GodotUICanvas>();
            if (canvas?.Hook is GodotUICanvasHook canvasHook)
            {
                return canvasHook;
            }
            slot = slot.Parent;
        }
        return null;
    }
}
