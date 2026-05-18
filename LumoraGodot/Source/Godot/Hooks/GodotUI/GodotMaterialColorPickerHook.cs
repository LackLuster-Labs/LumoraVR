// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;
using Lumora.Core;
using Lumora.Core.Components.Assets;
using Lumora.Core.GodotUI.Wizards;
using Lumora.Core.Math;
using Lumora.Godot.UI;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks.GodotUI;

#nullable enable

/// <summary>
/// Godot hook for standalone material color picker panel.
/// </summary>
public sealed class GodotMaterialColorPickerHook : ComponentHook<GodotMaterialColorPicker>
{
    private const string TitlePath = "MainPanel/VBox/Header/HBox/Title";
    private const string CloseButtonPath = "MainPanel/VBox/Header/HBox/CloseButton";
    private const string UniformLabelPath = "MainPanel/VBox/Content/Margin/VBox/UniformName";
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
    private Label? _uniformLabel;
    private ColorPicker? _colorPicker;
    private ColorRect? _swatch;
    private Label? _valueLabel;

    private CustomShaderMaterial? _boundMaterial;
    private Lumora.Core.Networking.Sync.ISyncList? _boundParamList;
    private ShaderUniformParam? _boundParam;

    private bool _suppressPickerEvents;
    private bool _hasLastBoundColor;
    private float4 _lastBoundColor;

    public static IHook<GodotMaterialColorPicker> Constructor()
    {
        return new GodotMaterialColorPickerHook();
    }

    public override void Initialize()
    {
        base.Initialize();

        var resScale = UIReadability.GetReadableResolutionScale(Owner.ResolutionScale.Value);
        _viewport = new SubViewport
        {
            Name = "MaterialColorPickerViewport",
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

        _meshInstance = new MeshInstance3D { Name = "MaterialColorPickerQuad" };
        _quadMesh = new QuadMesh();
        UpdateQuadSize();
        _meshInstance.Mesh = _quadMesh;

        _material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear
        };
        _material.AlbedoTexture = _viewport.GetTexture();
        _meshInstance.MaterialOverride = _material;

        attachedNode.AddChild(_viewport);
        attachedNode.AddChild(_meshInstance);
        CreateCollisionArea();

        LoadScene();
        BindMaterial(Owner.Material.Target);
        ResolveParameter();

        LumoraLogger.Log("GodotMaterialColorPickerHook: Initialized");
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

        var materialChanged = Owner.Material.GetWasChangedAndClear() || Owner.Material.Target != _boundMaterial;
        var parameterChanged = Owner.ParameterName.GetWasChangedAndClear();
        if (materialChanged)
        {
            BindMaterial(Owner.Material.Target);
            parameterChanged = true;
        }

        if (parameterChanged || _boundParam == null)
        {
            ResolveParameter();
        }

        if (_boundParam != null)
        {
            var value = _boundParam.Value.Value;
            if (!_hasLastBoundColor || !NearlyEqual(value, _lastBoundColor))
            {
                _lastBoundColor = value;
                _hasLastBoundColor = true;
                UpdatePickerVisuals(value);
            }
        }
    }

    private void LoadScene()
    {
        var scenePath = Owner.ScenePath.Value;
        if (string.IsNullOrEmpty(scenePath))
        {
            LumoraLogger.Warn("GodotMaterialColorPickerHook: No scene path specified");
            return;
        }

        if (_loadedScene != null && GodotObject.IsInstanceValid(_loadedScene))
        {
            _loadedScene.QueueFree();
            _loadedScene = null;
        }

        var packedScene = GD.Load<PackedScene>(scenePath);
        if (packedScene == null)
        {
            LumoraLogger.Warn($"GodotMaterialColorPickerHook: Failed to load scene '{scenePath}'");
            return;
        }

        _loadedScene = packedScene.Instantiate();
        if (_loadedScene == null)
        {
            LumoraLogger.Warn("GodotMaterialColorPickerHook: Failed to instantiate scene");
            return;
        }

        _viewport?.AddChild(_loadedScene);

        if (_loadedScene is Control control)
        {
            control.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            UIReadability.ApplyToTree(control);
        }

        CacheSceneNodes();
        RefreshHeaderAndLabels();
    }

    private void CacheSceneNodes()
    {
        _titleLabel = _loadedScene?.GetNodeOrNull<Label>(TitlePath);
        _closeButton = _loadedScene?.GetNodeOrNull<Button>(CloseButtonPath);
        if (_closeButton != null)
        {
            _closeButton.Pressed += () => Owner.HandleButtonPress(CloseButtonPath);
        }

        _uniformLabel = _loadedScene?.GetNodeOrNull<Label>(UniformLabelPath);
        _swatch = _loadedScene?.GetNodeOrNull<ColorRect>(SwatchPath);
        _valueLabel = _loadedScene?.GetNodeOrNull<Label>(ValuePath);

        if (_colorPicker != null && GodotObject.IsInstanceValid(_colorPicker))
        {
            _colorPicker.ColorChanged -= OnPickerColorChanged;
        }

        _colorPicker = _loadedScene?.GetNodeOrNull<ColorPicker>(PickerPath);
        if (_colorPicker != null)
        {
            _colorPicker.ColorChanged += OnPickerColorChanged;
        }
    }

    private void BindMaterial(CustomShaderMaterial? material)
    {
        if (_boundMaterial == material)
        {
            return;
        }

        UnbindMaterial();
        _boundMaterial = material;

        if (_boundMaterial != null)
        {
            _boundMaterial.Parameters.ElementsAdded += OnParametersChanged;
            _boundMaterial.Parameters.ElementsRemoved += OnParametersChanged;
            _boundParamList = _boundMaterial.Parameters;
            _boundParamList.ListCleared += OnParametersCleared;
        }
    }

    private void UnbindMaterial()
    {
        UnbindParameter();

        if (_boundMaterial == null)
        {
            return;
        }

        _boundMaterial.Parameters.ElementsAdded -= OnParametersChanged;
        _boundMaterial.Parameters.ElementsRemoved -= OnParametersChanged;
        if (_boundParamList != null)
        {
            _boundParamList.ListCleared -= OnParametersCleared;
            _boundParamList = null;
        }

        _boundMaterial = null;
    }

    private void ResolveParameter()
    {
        var parameterName = Owner.ParameterName.Value;
        ShaderUniformParam? found = null;
        if (_boundMaterial != null && !string.IsNullOrWhiteSpace(parameterName))
        {
            foreach (var param in _boundMaterial.Parameters)
            {
                if (string.Equals(param.Name.Value, parameterName, StringComparison.Ordinal))
                {
                    found = param;
                    break;
                }
            }
        }

        if (!ReferenceEquals(found, _boundParam))
        {
            UnbindParameter();
            _boundParam = found;
            if (_boundParam != null)
            {
                _boundParam.Value.OnChanged += OnBoundParamValueChanged;
            }
        }

        RefreshHeaderAndLabels();
    }

    private void UnbindParameter()
    {
        if (_boundParam == null)
        {
            return;
        }

        _boundParam.Value.OnChanged -= OnBoundParamValueChanged;
        _boundParam = null;
        _hasLastBoundColor = false;
    }

    private void OnParametersChanged(Lumora.Core.Networking.Sync.SyncElementList<ShaderUniformParam> list, int index, int count)
    {
        ResolveParameter();
    }

    private void OnParametersCleared(Lumora.Core.Networking.Sync.ISyncList list)
    {
        ResolveParameter();
    }

    private void OnBoundParamValueChanged(float4 value)
    {
        _lastBoundColor = value;
        _hasLastBoundColor = true;
        UpdatePickerVisuals(value);
    }

    private void RefreshHeaderAndLabels()
    {
        var uniformName = Owner.ParameterName.Value;
        var hasParam = _boundParam != null;
        if (_titleLabel != null)
        {
            _titleLabel.Text = string.IsNullOrWhiteSpace(uniformName)
                ? "Color Picker"
                : $"Color Picker - {uniformName}";
        }

        if (_uniformLabel != null)
        {
            _uniformLabel.Text = hasParam
                ? $"Uniform: {uniformName}"
                : "Uniform: (not found)";
        }

        if (_colorPicker != null)
        {
            _colorPicker.EditAlpha = true;
            _colorPicker.MouseFilter = hasParam
                ? Control.MouseFilterEnum.Stop
                : Control.MouseFilterEnum.Ignore;
            _colorPicker.SelfModulate = hasParam
                ? Colors.White
                : new Color(1f, 1f, 1f, 0.55f);
        }

        if (hasParam)
        {
            var value = _boundParam!.Value.Value;
            _lastBoundColor = value;
            _hasLastBoundColor = true;
            UpdatePickerVisuals(value);
        }
        else
        {
            UpdatePickerVisuals(new float4(0f, 0f, 0f, 1f));
        }
    }

    private void UpdatePickerVisuals(float4 value)
    {
        var color = new Color(value.x, value.y, value.z, value.w);

        if (_colorPicker != null && GodotObject.IsInstanceValid(_colorPicker))
        {
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

    private void OnPickerColorChanged(Color color)
    {
        if (_suppressPickerEvents || _boundParam == null)
        {
            return;
        }

        var newValue = new float4(color.R, color.G, color.B, color.A);
        var param = _boundParam;

        // ColorPicker signal fires on Godot main thread outside World.Update() —
        // dispatch the sync write to run during the next update with the Implementer lock held.
        World?.RunSynchronously(() => param?.Value.Value = newValue);

        _lastBoundColor = newValue;
        _hasLastBoundColor = true;
        UpdatePickerVisuals(newValue);
    }

    private static bool NearlyEqual(float4 a, float4 b)
    {
        const float eps = 0.0005f;
        return Math.Abs(a.x - b.x) <= eps &&
               Math.Abs(a.y - b.y) <= eps &&
               Math.Abs(a.z - b.z) <= eps &&
               Math.Abs(a.w - b.w) <= eps;
    }

    private static string ColorToHex(Color color)
    {
        byte r = (byte)Mathf.Clamp(Mathf.RoundToInt(color.R * 255f), 0, 255);
        byte g = (byte)Mathf.Clamp(Mathf.RoundToInt(color.G * 255f), 0, 255);
        byte b = (byte)Mathf.Clamp(Mathf.RoundToInt(color.B * 255f), 0, 255);
        byte a = (byte)Mathf.Clamp(Mathf.RoundToInt(color.A * 255f), 0, 255);
        return $"#{r:X2}{g:X2}{b:X2}{a:X2}";
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
        _collisionArea = new Area3D();
        _collisionArea.Name = "MaterialColorPickerCollision";
        _collisionArea.Monitorable = true;
        _collisionArea.Monitoring = false;
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
        if (_boxShape == null)
        {
            return;
        }

        var size = Owner.Size.Value;
        var ppu = Owner.PixelsPerUnit.Value;
        _boxShape.Size = new Vector3(size.x / ppu, size.y / ppu, 0.01f);
    }

    public override void Destroy(bool destroyingWorld)
    {
        UnbindMaterial();

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
        _uniformLabel = null;
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
        _hasLastBoundColor = false;

        base.Destroy(destroyingWorld);
    }
}
