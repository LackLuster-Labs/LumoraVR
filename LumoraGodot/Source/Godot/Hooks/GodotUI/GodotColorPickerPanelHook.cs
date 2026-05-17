// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;
using Lumora.Core;
using Lumora.Core.GodotUI.Wizards;
using Lumora.Core.Math;
using Lumora.Godot.UI;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks.GodotUI;

#nullable enable

/// <summary>
/// Godot hook for the generic in-world color picker panel.
/// </summary>
public sealed class GodotColorPickerPanelHook : ComponentHook<ColorPickerPanel>
{
    private const string TitlePath = "MainPanel/VBox/Header/HBox/Title";
    private const string CloseButtonPath = "MainPanel/VBox/Header/HBox/CloseButton";
    private const string FieldLabelPath = "MainPanel/VBox/Content/Margin/VBox/UniformName";
    private const string PickerPath = "MainPanel/VBox/Content/Margin/VBox/ColorPicker";
    private const string SwatchPath = "MainPanel/VBox/Content/Margin/VBox/PreviewRow/Swatch";
    private const string ValuePath = "MainPanel/VBox/Content/Margin/VBox/PreviewRow/Value";

    private SubViewport? _viewport;
    private MeshInstance3D? _meshInstance;
    private QuadMesh? _quadMesh;
    private StandardMaterial3D? _material;
    private Node? _loadedScene;
    private Vector2I _lastViewportSize = Vector2I.Zero;

    private Area3D? _collisionArea;
    private CollisionShape3D? _collisionShape;
    private BoxShape3D? _boxShape;

    private Label? _titleLabel;
    private Button? _closeButton;
    private Label? _fieldLabel;
    private ColorPicker? _colorPicker;
    private ColorRect? _swatch;
    private Label? _valueLabel;

    private bool _suppressPickerEvents;

    public static IHook<ColorPickerPanel> Constructor()
    {
        return new GodotColorPickerPanelHook();
    }

    public override void Initialize()
    {
        base.Initialize();

        var resScale = UIReadability.GetReadableResolutionScale(Owner.ResolutionScale.Value);
        _viewport = new SubViewport
        {
            Name = "ColorPickerPanelViewport",
            Size = new Vector2I(
                (int)(Owner.Size.Value.x * resScale),
                (int)(Owner.Size.Value.y * resScale)),
            TransparentBg = true,
            HandleInputLocally = true,
            GuiDisableInput = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible,
            CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Linear,
            Msaa2D = Viewport.Msaa.Disabled
        };
        _lastViewportSize = _viewport.Size;

        _meshInstance = new MeshInstance3D { Name = "ColorPickerPanelQuad" };
        _quadMesh = new QuadMesh();
        UpdateQuadSize();
        _meshInstance.Mesh = _quadMesh;

        _material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
            AlbedoTexture = _viewport.GetTexture()
        };
        _meshInstance.MaterialOverride = _material;

        attachedNode.AddChild(_viewport);
        attachedNode.AddChild(_meshInstance);
        CreateCollisionArea();

        LoadScene();

        Owner.CurrentColor.OnChanged += OnOwnerColorChanged;
        Owner.Label.OnChanged += _ => RefreshVisuals();
        Owner.ShowAlpha.OnChanged += _ => RefreshVisuals();
        Owner.AllowHDR.OnChanged += _ => RefreshVisuals();

        LumoraLogger.Log("GodotColorPickerPanelHook: Initialized");
    }

    public override void ApplyChanges()
    {
        if (_viewport == null)
        {
            return;
        }

        var resScale = UIReadability.GetReadableResolutionScale(Owner.ResolutionScale.Value);
        var desiredSize = new Vector2I(
            (int)(Owner.Size.Value.x * resScale),
            (int)(Owner.Size.Value.y * resScale));
        if (_lastViewportSize != desiredSize)
        {
            _viewport.Size = desiredSize;
            _lastViewportSize = desiredSize;
            UpdateQuadSize();
        }

        if (_material != null && _material.AlbedoTexture != _viewport.GetTexture())
        {
            _material.AlbedoTexture = _viewport.GetTexture();
        }

        if (Owner.ScenePath.GetWasChangedAndClear())
        {
            LoadScene();
        }

        RefreshVisuals();
    }

    private void LoadScene()
    {
        if (_loadedScene != null && GodotObject.IsInstanceValid(_loadedScene))
        {
            _loadedScene.QueueFree();
            _loadedScene = null;
        }

        var scenePath = Owner.ScenePath.Value;
        if (string.IsNullOrEmpty(scenePath))
        {
            LumoraLogger.Warn("GodotColorPickerPanelHook: No scene path specified");
            return;
        }

        var packedScene = GD.Load<PackedScene>(scenePath);
        if (packedScene == null)
        {
            LumoraLogger.Warn($"GodotColorPickerPanelHook: Failed to load scene '{scenePath}'");
            return;
        }

        _loadedScene = packedScene.Instantiate();
        if (_loadedScene == null)
        {
            LumoraLogger.Warn("GodotColorPickerPanelHook: Failed to instantiate scene");
            return;
        }

        _viewport?.AddChild(_loadedScene);

        if (_loadedScene is Control control)
        {
            control.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            UIReadability.ApplyToTree(control);
        }

        CacheSceneNodes();
        RefreshVisuals();
    }

    private void CacheSceneNodes()
    {
        _titleLabel = _loadedScene?.GetNodeOrNull<Label>(TitlePath);
        _closeButton = _loadedScene?.GetNodeOrNull<Button>(CloseButtonPath);
        if (_closeButton != null)
        {
            _closeButton.Pressed += () => Owner.HandleButtonPress(CloseButtonPath);
        }

        if (_colorPicker != null && GodotObject.IsInstanceValid(_colorPicker))
        {
            _colorPicker.ColorChanged -= OnPickerColorChanged;
        }

        _fieldLabel = _loadedScene?.GetNodeOrNull<Label>(FieldLabelPath);
        _colorPicker = _loadedScene?.GetNodeOrNull<ColorPicker>(PickerPath);
        _swatch = _loadedScene?.GetNodeOrNull<ColorRect>(SwatchPath);
        _valueLabel = _loadedScene?.GetNodeOrNull<Label>(ValuePath);

        if (_colorPicker != null)
        {
            _colorPicker.ColorChanged += OnPickerColorChanged;
        }
    }

    private void RefreshVisuals()
    {
        var label = string.IsNullOrWhiteSpace(Owner.Label.Value) ? "Color" : Owner.Label.Value;
        var color = ToGodotColor(Owner.CurrentColor.Value);

        if (_titleLabel != null && GodotObject.IsInstanceValid(_titleLabel))
        {
            _titleLabel.Text = $"Color Picker - {label}";
        }

        if (_fieldLabel != null && GodotObject.IsInstanceValid(_fieldLabel))
        {
            _fieldLabel.Text = $"Field: {label}";
        }

        if (_colorPicker != null && GodotObject.IsInstanceValid(_colorPicker))
        {
            _colorPicker.EditAlpha = Owner.ShowAlpha.Value;
            _colorPicker.EditIntensity = Owner.AllowHDR.Value;
            _suppressPickerEvents = true;
            _colorPicker.Color = color;
            _suppressPickerEvents = false;
        }

        if (_swatch != null && GodotObject.IsInstanceValid(_swatch))
        {
            _swatch.Color = color;
        }

        if (_valueLabel != null && GodotObject.IsInstanceValid(_valueLabel))
        {
            _valueLabel.Text = ColorToHex(color);
        }
    }

    private void OnOwnerColorChanged(color _)
    {
        RefreshVisuals();
    }

    private void OnPickerColorChanged(Color pickedColor)
    {
        if (_suppressPickerEvents)
        {
            return;
        }

        var newValue = new color(pickedColor.R, pickedColor.G, pickedColor.B, pickedColor.A);
        World?.RunSynchronously(() => Owner.CurrentColor.Value = newValue);
        RefreshVisuals();
    }

    private void UpdateQuadSize()
    {
        if (_quadMesh == null)
        {
            return;
        }

        var size = Owner.Size.Value;
        var ppu = Owner.PixelsPerUnit.Value;
        _quadMesh.Size = new Vector2(size.x / ppu, size.y / ppu);
        UpdateCollisionSize();
    }

    private void CreateCollisionArea()
    {
        _collisionArea = new Area3D
        {
            Name = "ColorPickerPanelCollision",
            Monitorable = true,
            Monitoring = false,
            CollisionLayer = 1u << 3,
            CollisionMask = 0
        };

        _collisionShape = new CollisionShape3D();
        _boxShape = new BoxShape3D();
        UpdateCollisionSize();
        _collisionShape.Shape = _boxShape;

        _collisionArea.AddChild(_collisionShape);
        attachedNode.AddChild(_collisionArea);
    }

    private void UpdateCollisionSize()
    {
        if (_boxShape == null)
        {
            return;
        }

        var size = Owner.Size.Value;
        var ppu = Owner.PixelsPerUnit.Value;
        _boxShape.Size = new Vector3(size.x / ppu, size.y / ppu, 0.01f);
    }

    private static Color ToGodotColor(color value)
    {
        return new Color(value.r, value.g, value.b, value.a);
    }

    private static string ColorToHex(Color color)
    {
        byte r = (byte)Mathf.Clamp(Mathf.RoundToInt(color.R * 255f), 0, 255);
        byte g = (byte)Mathf.Clamp(Mathf.RoundToInt(color.G * 255f), 0, 255);
        byte b = (byte)Mathf.Clamp(Mathf.RoundToInt(color.B * 255f), 0, 255);
        byte a = (byte)Mathf.Clamp(Mathf.RoundToInt(color.A * 255f), 0, 255);
        return $"#{r:X2}{g:X2}{b:X2}{a:X2}";
    }

    public override void Destroy(bool destroyingWorld)
    {
        Owner.CurrentColor.OnChanged -= OnOwnerColorChanged;

        if (_colorPicker != null && GodotObject.IsInstanceValid(_colorPicker))
        {
            _colorPicker.ColorChanged -= OnPickerColorChanged;
        }

        if (!destroyingWorld)
        {
            _loadedScene?.QueueFree();
            _collisionArea?.QueueFree();
            _viewport?.QueueFree();
            _meshInstance?.QueueFree();
            _material?.Dispose();
            _quadMesh?.Dispose();
            _boxShape?.Dispose();
        }

        _loadedScene = null;
        _titleLabel = null;
        _closeButton = null;
        _fieldLabel = null;
        _colorPicker = null;
        _swatch = null;
        _valueLabel = null;
        _collisionArea = null;
        _collisionShape = null;
        _boxShape = null;
        _viewport = null;
        _meshInstance = null;
        _material = null;
        _quadMesh = null;
        _lastViewportSize = Vector2I.Zero;
        _suppressPickerEvents = false;

        base.Destroy(destroyingWorld);
    }
}
