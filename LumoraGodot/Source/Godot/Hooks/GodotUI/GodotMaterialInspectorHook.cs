// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

﻿using System.Collections.Generic;
using System;
using System.Linq;
using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.GodotUI.Wizards;
using Lumora.Core.Math;
using Lumora.Godot.UI;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks.GodotUI;

#nullable enable

/// <summary>
/// Godot hook for the material inspector panel.
/// Builds UI rows dynamically from CustomShaderMaterial uniforms.
/// </summary>
public sealed class GodotMaterialInspectorHook : ComponentHook<GodotMaterialInspector>
{
    private const string ContainerPath = "MainPanel/VBox/Content/Scroll/VBox";
    private const string TitlePath = "MainPanel/VBox/Header/HBox/Title";
    private const string CloseButtonPath = "MainPanel/VBox/Header/HBox/CloseButton";
    private const string LabelTemplatePath = "MainPanel/VBox/Content/Scroll/VBox/Row_0/Label";

    private SubViewport? _viewport;
    private MeshInstance3D? _meshInstance;
    private MeshInstance3D? _backingInstance;
    private QuadMesh? _quadMesh;
    private QuadMesh? _backingQuadMesh;
    private StandardMaterial3D? _material;
    private StandardMaterial3D? _backingMaterial;
    private Node? _loadedScene;
    private Vector2I _lastViewportSize = Vector2I.Zero;

    private Area3D? _collisionArea;
    private CollisionShape3D? _collisionShape;
    private BoxShape3D? _boxShape;

    private VBoxContainer? _paramContainer;
    private Label? _titleLabel;
    private Button? _closeButton;
    private Font? _labelFont;
    private int? _labelFontSize;
    private Color? _labelFontColor;
    private CustomShaderMaterial? _boundMaterial;
    private Lumora.Core.Networking.Sync.ISyncList? _boundParamList;
    private bool _isUpdating;
    private string _searchQuery = string.Empty;
    private readonly Dictionary<string, float4> _defaultValues = new(StringComparer.Ordinal);

    public static IHook<GodotMaterialInspector> Constructor()
    {
        return new GodotMaterialInspectorHook();
    }

    public override void Initialize()
    {
        base.Initialize();

        var resScale = UIReadability.GetReadableResolutionScale(Owner.ResolutionScale.Value);

        _viewport = new SubViewport
        {
            Name = "MaterialInspectorViewport",
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

        _meshInstance = new MeshInstance3D { Name = "MaterialInspectorQuad" };
        _quadMesh = new QuadMesh();
        _backingQuadMesh = new QuadMesh();
        UpdateQuadSize();
        _meshInstance.Mesh = _quadMesh;
        _backingMaterial = WorldPanelBacking.CreateBackingMaterial();
        _backingInstance = WorldPanelBacking.CreateBackingMesh("MaterialInspectorBacking", _backingQuadMesh, _backingMaterial);

        _material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear
        };
        WorldPanelBacking.ConfigureSurfaceMaterial(_material);
        _material.AlbedoTexture = _viewport.GetTexture();
        _meshInstance.MaterialOverride = _material;

        attachedNode.AddChild(_viewport);
        attachedNode.AddChild(_backingInstance);
        attachedNode.AddChild(_meshInstance);
        CreateCollisionArea();

        LoadScene();
        BindMaterial(Owner.Material.Target);

        LumoraLogger.Log("GodotMaterialInspectorHook: Initialized");
    }

    public override void ApplyChanges()
    {
        if (_viewport == null) return;

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

        if (Owner.Material.GetWasChangedAndClear())
        {
            BindMaterial(Owner.Material.Target);
        }
        else if (Owner.Material.Target != _boundMaterial)
        {
            BindMaterial(Owner.Material.Target);
        }
    }

    private void LoadScene()
    {
        var scenePath = Owner.ScenePath.Value;
        if (string.IsNullOrEmpty(scenePath))
        {
            LumoraLogger.Warn("GodotMaterialInspectorHook: No scene path specified");
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
            LumoraLogger.Warn($"GodotMaterialInspectorHook: Failed to load scene '{scenePath}'");
            return;
        }

        _loadedScene = packedScene.Instantiate();
        if (_loadedScene == null)
        {
            LumoraLogger.Warn("GodotMaterialInspectorHook: Failed to instantiate scene");
            return;
        }

        _viewport?.AddChild(_loadedScene);

        if (_loadedScene is Control control)
        {
            control.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            UIReadability.ApplyToTree(control);
        }

        CacheSceneNodes();
        RebuildUI();
    }

    private void CacheSceneNodes()
    {
        _paramContainer = _loadedScene?.GetNodeOrNull<VBoxContainer>(ContainerPath);
        _titleLabel = _loadedScene?.GetNodeOrNull<Label>(TitlePath);
        _closeButton = _loadedScene?.GetNodeOrNull<Button>(CloseButtonPath);
        if (_closeButton != null)
        {
            _closeButton.Pressed += () => Owner.HandleButtonPress(CloseButtonPath);
        }

        var templateLabel = _loadedScene?.GetNodeOrNull<Label>(LabelTemplatePath);
        if (templateLabel != null)
        {
            if (templateLabel.HasThemeFontOverride("font"))
            {
                _labelFont = templateLabel.GetThemeFont("font");
            }

            if (templateLabel.HasThemeFontSizeOverride("font_size"))
            {
                _labelFontSize = templateLabel.GetThemeFontSize("font_size");
            }

            if (templateLabel.HasThemeColorOverride("font_color"))
            {
                _labelFontColor = templateLabel.GetThemeColor("font_color");
            }
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
        _searchQuery = string.Empty;
        CaptureDefaultValues();

        if (_boundMaterial != null)
        {
            _boundMaterial.Parameters.ElementsAdded += OnParametersChanged;
            _boundMaterial.Parameters.ElementsRemoved += OnParametersChanged;
            _boundParamList = _boundMaterial.Parameters;
            _boundParamList.ListCleared += OnParametersCleared;
        }

        RebuildUI();
    }

    private void UnbindMaterial()
    {
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
        _defaultValues.Clear();
    }

    private void OnParametersChanged(Lumora.Core.Networking.Sync.SyncElementList<ShaderUniformParam> list, int index, int count)
    {
        EnsureDefaultValues();
        RebuildUI();
    }

    private void OnParametersCleared(Lumora.Core.Networking.Sync.ISyncList list)
    {
        _defaultValues.Clear();
        RebuildUI();
    }

    private void RebuildUI()
    {
        if (_paramContainer == null)
        {
            return;
        }

        _isUpdating = true;
        ClearContainer(_paramContainer);

        if (_titleLabel != null)
        {
            _titleLabel.Text = _boundMaterial == null ? "Material (None)" : "Material";
        }

        if (_boundMaterial == null)
        {
            _paramContainer.AddChild(CreateEmptyLabel("No material assigned."));
            _isUpdating = false;
            return;
        }

        _paramContainer.AddChild(CreateToolbar());

        if (_boundMaterial.Parameters.Count == 0)
        {
            _paramContainer.AddChild(CreateEmptyLabel("No shader uniforms."));
            _isUpdating = false;
            return;
        }

        var rows = _boundMaterial.Parameters
            .Where(param => string.IsNullOrWhiteSpace(_searchQuery) ||
                            param.Name.Value.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase))
            .OrderBy(param => param.Name.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (rows.Count == 0)
        {
            _paramContainer.AddChild(CreateEmptyLabel("No matching uniforms."));
            _isUpdating = false;
            return;
        }

        foreach (var param in rows)
        {
            _paramContainer.AddChild(CreateParamRow(param));
        }

        if (_loadedScene is Control loadedControl)
        {
            UIReadability.ApplyToTree(loadedControl);
        }

        _isUpdating = false;
    }

    private Control CreateToolbar()
    {
        var toolbar = new HBoxContainer();
        toolbar.AddThemeConstantOverride("separation", 6);
        toolbar.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var search = new LineEdit
        {
            PlaceholderText = "Search uniforms...",
            Text = _searchQuery
        };
        search.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        search.TextChanged += value =>
        {
            if (_isUpdating) return;
            _searchQuery = value ?? string.Empty;
            RebuildUI();
        };
        toolbar.AddChild(search);

        var resetAll = new Button { Text = "Reset All" };
        resetAll.Pressed += () =>
        {
            if (_isUpdating) return;
            ResetAllParameters();
            RebuildUI();
        };
        toolbar.AddChild(resetAll);

        return toolbar;
    }

    private void ClearContainer(VBoxContainer container)
    {
        var children = new List<Node>();
        foreach (var child in container.GetChildren())
        {
            children.Add(child);
        }

        foreach (var child in children)
        {
            child.QueueFree();
        }
    }

    private Label CreateEmptyLabel(string text)
    {
        var label = CreateLabel(text);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return label;
    }

    private Control CreateParamRow(ShaderUniformParam param)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.CustomMinimumSize = new Vector2(0, 28);

        var nameLabel = CreateLabel(param.Name.Value);
        nameLabel.CustomMinimumSize = new Vector2(140, 0);
        nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        nameLabel.TooltipText = $"{param.Type.Value}" + (param.HasRange.Value ? $" [{param.Range.Value.x:0.###}..{param.Range.Value.y:0.###}]" : string.Empty);
        row.AddChild(nameLabel);

        var editor = CreateEditor(param);
        if (editor != null)
        {
            editor.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(editor);
        }

        var reset = new Button
        {
            Text = "R",
            CustomMinimumSize = new Vector2(24, 24),
            TooltipText = "Reset parameter to default"
        };
        reset.Pressed += () =>
        {
            if (_isUpdating) return;
            ResetParameter(param);
            RebuildUI();
        };
        row.AddChild(reset);

        return row;
    }

    private Control? CreateEditor(ShaderUniformParam param)
    {
        if (param.Type.Value == ShaderUniformType.Vec4 && param.IsColor.Value)
        {
            return CreateColorEditor(param);
        }

        return param.Type.Value switch
        {
            ShaderUniformType.Float => CreateFloatEditor(param),
            ShaderUniformType.Int => CreateIntEditor(param),
            ShaderUniformType.Bool => CreateBoolEditor(param),
            ShaderUniformType.Vec2 => CreateVectorEditor(param, 2),
            ShaderUniformType.Vec3 => CreateVectorEditor(param, 3),
            ShaderUniformType.Vec4 => CreateVectorEditor(param, 4),
            ShaderUniformType.Texture2D => CreateTextureEditor(param),
            _ => CreateEmptyLabel("Unsupported")
        };
    }

    private Control CreateFloatEditor(ShaderUniformParam param)
    {
        if (param.HasRange.Value)
        {
            return CreateRangedNumericEditor(param, isInt: false);
        }

        var spin = CreateSpinBox(param, param.Value.Value.x, isInt: false);
        spin.ValueChanged += value =>
        {
            if (_isUpdating) return;
            var current = param.Value.Value;
            param.Value.Value = new float4((float)value, current.y, current.z, current.w);
        };
        return spin;
    }

    private Control CreateIntEditor(ShaderUniformParam param)
    {
        if (param.HasRange.Value)
        {
            return CreateRangedNumericEditor(param, isInt: true);
        }

        var spin = CreateSpinBox(param, param.Value.Value.x, isInt: true);
        spin.Step = 1;
        spin.ValueChanged += value =>
        {
            if (_isUpdating) return;
            var current = param.Value.Value;
            param.Value.Value = new float4((float)value, current.y, current.z, current.w);
        };
        return spin;
    }

    private Control CreateRangedNumericEditor(ShaderUniformParam param, bool isInt)
    {
        var container = new HBoxContainer();
        container.AddThemeConstantOverride("separation", 4);

        var min = param.Range.Value.x;
        var max = param.Range.Value.y;
        var step = isInt ? 1f : 0.01f;
        var value = param.Value.Value.x;

        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = value,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };

        var spin = CreateSpinBox(param, value, isInt);
        spin.MinValue = min;
        spin.MaxValue = max;
        spin.Step = step;
        spin.CustomMinimumSize = new Vector2(74, 0);

        bool internalSync = false;

        slider.ValueChanged += raw =>
        {
            if (_isUpdating || internalSync) return;
            internalSync = true;
            var mapped = isInt ? (float)Math.Round(raw) : (float)raw;
            spin.Value = mapped;
            var current = param.Value.Value;
            param.Value.Value = new float4(mapped, current.y, current.z, current.w);
            internalSync = false;
        };

        spin.ValueChanged += raw =>
        {
            if (_isUpdating || internalSync) return;
            internalSync = true;
            var mapped = isInt ? (float)Math.Round(raw) : (float)raw;
            slider.Value = mapped;
            var current = param.Value.Value;
            param.Value.Value = new float4(mapped, current.y, current.z, current.w);
            internalSync = false;
        };

        container.AddChild(slider);
        container.AddChild(spin);
        return container;
    }

    private Control CreateBoolEditor(ShaderUniformParam param)
    {
        var check = new CheckBox();
        check.ButtonPressed = param.Value.Value.x >= 0.5f;
        check.Toggled += value =>
        {
            if (_isUpdating) return;
            var current = param.Value.Value;
            param.Value.Value = new float4(value ? 1f : 0f, current.y, current.z, current.w);
        };
        return check;
    }

    private Control CreateVectorEditor(ShaderUniformParam param, int components)
    {
        var container = new HBoxContainer();
        container.AddThemeConstantOverride("separation", 4);

        for (int i = 0; i < components; i++)
        {
            var index = i;
            var value = GetComponentValue(param.Value.Value, index);
            var spin = CreateSpinBox(param, value, isInt: false);
            spin.CustomMinimumSize = new Vector2(60, 0);
            spin.ValueChanged += newValue =>
            {
                if (_isUpdating) return;
                var current = param.Value.Value;
                param.Value.Value = SetComponentValue(current, index, (float)newValue);
            };
            container.AddChild(spin);
        }

        return container;
    }

    private Control CreateColorEditor(ShaderUniformParam param)
    {
        var container = new HBoxContainer();
        container.AddThemeConstantOverride("separation", 4);

        var swatch = new ColorRect
        {
            CustomMinimumSize = new Vector2(16, 16),
            Color = new Color(param.Value.Value.x, param.Value.Value.y, param.Value.Value.z, param.Value.Value.w)
        };
        container.AddChild(swatch);

        var picker = new Button
        {
            Text = "Pick",
            CustomMinimumSize = new Vector2(96, 0)
        };
        picker.Pressed += () =>
        {
            OpenColorPickerPanel(param);
            swatch.Color = new Color(param.Value.Value.x, param.Value.Value.y, param.Value.Value.z, param.Value.Value.w);
        };

        container.AddChild(picker);
        return container;
    }

    private Control CreateTextureEditor(ShaderUniformParam param)
    {
        var container = new HBoxContainer();
        container.AddThemeConstantOverride("separation", 4);

        var label = CreateLabel(param.Texture.Target != null ? "Texture" : "None");
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        container.AddChild(label);

        var clearButton = new Button { Text = "Clear" };
        clearButton.Pressed += () =>
        {
            if (_isUpdating) return;
            param.Texture.Target = null;
        };
        container.AddChild(clearButton);

        return container;
    }

    private SpinBox CreateSpinBox(ShaderUniformParam param, float value, bool isInt)
    {
        var spin = new SpinBox
        {
            Step = isInt ? 1f : 0.01f,
            AllowGreater = !param.HasRange.Value,
            AllowLesser = !param.HasRange.Value
        };

        if (param.HasRange.Value)
        {
            spin.MinValue = param.Range.Value.x;
            spin.MaxValue = param.Range.Value.y;
        }
        else
        {
            spin.MinValue = isInt ? -100 : -10;
            spin.MaxValue = isInt ? 100 : 10;
        }

        spin.Value = value;
        return spin;
    }

    private Label CreateLabel(string text)
    {
        var label = new Label { Text = text };

        if (_labelFont != null)
        {
            label.AddThemeFontOverride("font", _labelFont);
        }

        if (_labelFontSize.HasValue)
        {
            label.AddThemeFontSizeOverride("font_size", _labelFontSize.Value);
        }

        if (_labelFontColor.HasValue)
        {
            label.AddThemeColorOverride("font_color", _labelFontColor.Value);
        }

        return label;
    }

    private static float GetComponentValue(float4 value, int index)
    {
        return index switch
        {
            0 => value.x,
            1 => value.y,
            2 => value.z,
            3 => value.w,
            _ => value.x
        };
    }

    private static float4 SetComponentValue(float4 value, int index, float component)
    {
        return index switch
        {
            0 => new float4(component, value.y, value.z, value.w),
            1 => new float4(value.x, component, value.z, value.w),
            2 => new float4(value.x, value.y, component, value.w),
            3 => new float4(value.x, value.y, value.z, component),
            _ => value
        };
    }

    private void UpdateQuadSize()
    {
        if (_quadMesh == null) return;

        var size = Owner.Size.Value;
        var ppu = Owner.PixelsPerUnit.Value;
        var panelSize = new Vector2(size.x / ppu, size.y / ppu);
        _quadMesh.Size = panelSize;
        if (_backingQuadMesh != null)
        {
            _backingQuadMesh.Size = WorldPanelBacking.GetBackingSize(panelSize);
        }
        UpdateCollisionSize();
    }

    private void CreateCollisionArea()
    {
        _collisionArea = new Area3D();
        _collisionArea.Name = "MaterialInspectorCollision";
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
        if (_boxShape == null) return;

        var size = Owner.Size.Value;
        var ppu = Owner.PixelsPerUnit.Value;
        _boxShape.Size = new Vector3(size.x / ppu, size.y / ppu, 0.01f);
    }

    private void OpenColorPickerPanel(ShaderUniformParam param)
    {
        if (_boundMaterial == null || string.IsNullOrWhiteSpace(param.Name.Value))
        {
            return;
        }

        var parentSlot = Owner.Slot.Parent ?? Owner.Slot;
        var slotName = $"MaterialColorPicker_{SanitizeName(param.Name.Value)}";

        var pickerSlot = parentSlot.FindChild(slotName, recursive: false);
        var picker = pickerSlot?.GetComponent<GodotMaterialColorPicker>();
        if (picker == null)
        {
            pickerSlot = parentSlot.AddSlot(slotName);
            picker = pickerSlot.AttachComponent<GodotMaterialColorPicker>();
        }

        picker.Material.Target = _boundMaterial;
        picker.ParameterName.Value = param.Name.Value;
        picker.PixelsPerUnit.Value = Owner.PixelsPerUnit.Value;

        if (pickerSlot != null)
        {
            PanelPlacement.PlaceBesidePanel(pickerSlot, Owner.Slot, 0.48f, 0.06f, 0.03f);
        }
    }

    private static string SanitizeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Color";
        }

        Span<char> chars = stackalloc char[raw.Length];
        int len = 0;
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                chars[len++] = c;
            }
            else if (len == 0 || chars[len - 1] != '_')
            {
                chars[len++] = '_';
            }
        }

        if (len == 0)
        {
            return "Color";
        }

        return new string(chars.Slice(0, len));
    }

    private void CaptureDefaultValues()
    {
        _defaultValues.Clear();
        if (_boundMaterial == null) return;

        foreach (var param in _boundMaterial.Parameters)
        {
            var name = param.Name.Value;
            if (string.IsNullOrEmpty(name) || _defaultValues.ContainsKey(name))
            {
                continue;
            }
            _defaultValues[name] = param.Value.Value;
        }
    }

    private void EnsureDefaultValues()
    {
        if (_boundMaterial == null) return;

        foreach (var param in _boundMaterial.Parameters)
        {
            var name = param.Name.Value;
            if (string.IsNullOrEmpty(name) || _defaultValues.ContainsKey(name))
            {
                continue;
            }
            _defaultValues[name] = param.Value.Value;
        }
    }

    private void ResetAllParameters()
    {
        if (_boundMaterial == null) return;

        EnsureDefaultValues();

        foreach (var param in _boundMaterial.Parameters)
        {
            ResetParameter(param);
        }
    }

    private void ResetParameter(ShaderUniformParam param)
    {
        if (param.Type.Value == ShaderUniformType.Texture2D)
        {
            param.Texture.Target = null;
            return;
        }

        var name = param.Name.Value;
        if (!string.IsNullOrEmpty(name) && _defaultValues.TryGetValue(name, out var def))
        {
            param.Value.Value = def;
            return;
        }

        // Fallback if defaults are unavailable.
        param.Value.Value = param.Type.Value switch
        {
            ShaderUniformType.Bool => new float4(0f, 0f, 0f, 0f),
            ShaderUniformType.Int => new float4(0f, 0f, 0f, 0f),
            ShaderUniformType.Float => new float4(0f, 0f, 0f, 0f),
            ShaderUniformType.Vec2 => new float4(0f, 0f, 0f, 0f),
            ShaderUniformType.Vec3 => new float4(0f, 0f, 0f, 0f),
            ShaderUniformType.Vec4 => new float4(0f, 0f, 0f, 0f),
            _ => param.Value.Value
        };
    }

    public override void Destroy(bool destroyingWorld)
    {
        UnbindMaterial();

        if (!destroyingWorld)
        {
            _loadedScene?.QueueFree();
            _collisionArea?.QueueFree();
            _viewport?.QueueFree();
            _backingInstance?.QueueFree();
            _meshInstance?.QueueFree();
            _backingMaterial?.Dispose();
            _material?.Dispose();
            _backingQuadMesh?.Dispose();
            _quadMesh?.Dispose();
            _boxShape?.Dispose();
        }

        _loadedScene = null;
        _collisionArea = null;
        _collisionShape = null;
        _boxShape = null;
        _viewport = null;
        _backingInstance = null;
        _meshInstance = null;
        _backingMaterial = null;
        _material = null;
        _backingQuadMesh = null;
        _quadMesh = null;
        _lastViewportSize = Vector2I.Zero;
        _paramContainer = null;
        _titleLabel = null;
        _closeButton = null;
        _labelFont = null;
        _labelFontSize = null;
        _labelFontColor = null;
        _defaultValues.Clear();
        _searchQuery = string.Empty;

        base.Destroy(destroyingWorld);
    }
}
