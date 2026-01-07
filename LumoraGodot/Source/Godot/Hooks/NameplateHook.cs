using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks;

#nullable enable

/// <summary>
/// Hook for rendering user nameplates in Godot.
/// Creates a 3D billboard with the nameplate UI.
/// </summary>
public class NameplateHook : ComponentHook<Nameplate>
{
    private SubViewport? _viewport;
    private MeshInstance3D? _meshInstance;
    private QuadMesh? _quadMesh;
    private StandardMaterial3D? _material;
    private Node? _loadedScene;

    // UI Elements
    private Label? _nameLabel;
    private Panel? _background;

    // Cached style box for color updates
    private StyleBoxFlat? _backgroundStyle;

    public static IHook<Nameplate> Constructor()
    {
        return new NameplateHook();
    }

    public override void Initialize()
    {
        base.Initialize();

        // Create SubViewport for UI rendering
        _viewport = new SubViewport();
        _viewport.Name = "NameplateViewport";
        _viewport.Size = new Vector2I(400, 100); // 2x resolution for crisp text
        _viewport.TransparentBg = true;
        _viewport.HandleInputLocally = false;
        _viewport.GuiDisableInput = true;
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        _viewport.CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Linear;
        _viewport.Msaa2D = Viewport.Msaa.Msaa4X;

        // Create quad mesh for 3D display
        _meshInstance = new MeshInstance3D();
        _meshInstance.Name = "NameplateQuad";

        _quadMesh = new QuadMesh();
        UpdateQuadSize();
        _meshInstance.Mesh = _quadMesh;

        // Enable billboard mode
        _meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;

        // Create material
        _material = new StandardMaterial3D();
        _material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        _material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        _material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        _material.TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear;
        _material.AlbedoTexture = _viewport.GetTexture();

        // Enable billboard
        if (Owner.Billboard.Value)
        {
            _material.BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled;
            _material.BillboardKeepScale = true;
        }

        _meshInstance.MaterialOverride = _material;

        _meshInstance.Position = Vector3.Zero;

        attachedNode.AddChild(_viewport);
        attachedNode.AddChild(_meshInstance);

        LoadScene();

        AquaLogger.Log($"NameplateHook: Initialized for '{Owner.DisplayName.Value}'");
    }

    private void LoadScene()
    {
        var packedScene = GD.Load<PackedScene>(LumAssets.UI.Nameplate);
        if (packedScene == null)
        {
            AquaLogger.Warn($"NameplateHook: Failed to load scene '{LumAssets.UI.Nameplate}'");
            return;
        }

        _loadedScene = packedScene.Instantiate();
        if (_loadedScene == null)
        {
            AquaLogger.Warn("NameplateHook: Failed to instantiate scene");
            return;
        }

        _viewport?.AddChild(_loadedScene);

        // Find UI elements
        _nameLabel = _loadedScene.GetNode<Label>("NameLabel");
        _background = _loadedScene.GetNode<Panel>("Background");

        // Clone style box so we can modify colors per-instance
        if (_background != null)
        {
            var originalStyle = _background.GetThemeStylebox("panel") as StyleBoxFlat;
            if (originalStyle != null)
            {
                _backgroundStyle = (StyleBoxFlat)originalStyle.Duplicate();
                _background.AddThemeStyleboxOverride("panel", _backgroundStyle);
            }
        }

        // Initial update
        UpdateNameplate();
    }

    private void UpdateQuadSize()
    {
        if (_quadMesh == null) return;

        var size = Owner.Size.Value;
        // Convert from world units
        _quadMesh.Size = new Vector2(size.x, size.y);
    }

    private void UpdateNameplate()
    {
        // Update name text
        if (_nameLabel != null)
        {
            _nameLabel.Text = Owner.DisplayName.Value ?? "";
        }

        // Update rim glow color (border color)
        var rimColor = Owner.RimColor.Value;
        var godotColor = new Color(rimColor.r, rimColor.g, rimColor.b, rimColor.a);

        if (_backgroundStyle != null)
        {
            _backgroundStyle.BorderColor = godotColor;
        }
    }

    public override void ApplyChanges()
    {
        UpdateNameplate();
        UpdateQuadSize();

        if (_meshInstance != null)
        {
            _meshInstance.Position = Vector3.Zero;
        }

        // Update billboard mode
        if (_material != null)
        {
            _material.BillboardMode = Owner.Billboard.Value
                ? BaseMaterial3D.BillboardModeEnum.Enabled
                : BaseMaterial3D.BillboardModeEnum.Disabled;
        }
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld)
        {
            _loadedScene?.QueueFree();
            _viewport?.QueueFree();
            _meshInstance?.QueueFree();
            _material?.Dispose();
            _quadMesh?.Dispose();
            _backgroundStyle?.Dispose();
        }

        _loadedScene = null;
        _viewport = null;
        _meshInstance = null;
        _material = null;
        _quadMesh = null;
        _nameLabel = null;
        _background = null;
        _backgroundStyle = null;

        base.Destroy(destroyingWorld);
    }
}
