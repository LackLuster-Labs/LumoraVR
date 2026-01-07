using System.Collections.Generic;
using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.GodotUI.Wizards;
using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks.GodotUI;

#nullable enable

/// <summary>
/// Godot hook for the material inspector panel.
/// Builds UI rows dynamically from CustomShaderMaterial uniforms.
/// </summary>
public sealed class GodotMaterialInspectorHook : ComponentHook<GodotMaterialInspector>
{
    private const string ContainerPath = "MainPanel/VBox/Content/Scroll/VBox";
    private const string TitlePath = "MainPanel/VBox/Header/HBox/Title";
    private const string LabelTemplatePath = "MainPanel/VBox/Content/Scroll/VBox/Row_0/Label";

    private SubViewport? _viewport;
    private MeshInstance3D? _meshInstance;
    private QuadMesh? _quadMesh;
    private StandardMaterial3D? _material;
    private Node? _loadedScene;
    private VBoxContainer? _paramContainer;
    private Label? _titleLabel;
    private Font? _labelFont;
    private int? _labelFontSize;
    private Color? _labelFontColor;
    private CustomShaderMaterial? _boundMaterial;
    private Lumora.Core.Networking.Sync.ISyncList? _boundParamList;
    private bool _isUpdating;

    public static IHook<GodotMaterialInspector> Constructor()
    {
        return new GodotMaterialInspectorHook();
    }

    public override void Initialize()
    {
        base.Initialize();

        var resScale = Owner.ResolutionScale.Value;

        _viewport = new SubViewport
        {
            Name = "MaterialInspectorViewport",
            Size = new Vector2I(
                (int)(Owner.Size.Value.x * resScale),
                (int)(Owner.Size.Value.y * resScale)),
            TransparentBg = true,
            HandleInputLocally = true,
            GuiDisableInput = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Linear,
            Msaa2D = Viewport.Msaa.Msaa4X
        };

        _meshInstance = new MeshInstance3D { Name = "MaterialInspectorQuad" };
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

        LoadScene();
        BindMaterial(Owner.Material.Target);

        AquaLogger.Log("GodotMaterialInspectorHook: Initialized");
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
            AquaLogger.Warn("GodotMaterialInspectorHook: No scene path specified");
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
            AquaLogger.Warn($"GodotMaterialInspectorHook: Failed to load scene '{scenePath}'");
            return;
        }

        _loadedScene = packedScene.Instantiate();
        if (_loadedScene == null)
        {
            AquaLogger.Warn("GodotMaterialInspectorHook: Failed to instantiate scene");
            return;
        }

        _viewport?.AddChild(_loadedScene);

        if (_loadedScene is Control control)
        {
            control.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        }

        CacheSceneNodes();
        RebuildUI();
    }

    private void CacheSceneNodes()
    {
        _paramContainer = _loadedScene?.GetNodeOrNull<VBoxContainer>(ContainerPath);
        _titleLabel = _loadedScene?.GetNodeOrNull<Label>(TitlePath);

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
    }

    private void OnParametersChanged(Lumora.Core.Networking.Sync.SyncElementList<ShaderUniformParam> list, int index, int count)
    {
        RebuildUI();
    }

    private void OnParametersCleared(Lumora.Core.Networking.Sync.ISyncList list)
    {
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

        if (_boundMaterial.Parameters.Count == 0)
        {
            _paramContainer.AddChild(CreateEmptyLabel("No shader uniforms."));
            _isUpdating = false;
            return;
        }

        foreach (var param in _boundMaterial.Parameters)
        {
            _paramContainer.AddChild(CreateParamRow(param));
        }

        _isUpdating = false;
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

        var nameLabel = CreateLabel(param.Name.Value);
        nameLabel.CustomMinimumSize = new Vector2(120, 0);
        nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        row.AddChild(nameLabel);

        var editor = CreateEditor(param);
        if (editor != null)
        {
            editor.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(editor);
        }

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
        var picker = new ColorPickerButton
        {
            EditAlpha = true,
            Color = new Color(param.Value.Value.x, param.Value.Value.y, param.Value.Value.z, param.Value.Value.w),
            CustomMinimumSize = new Vector2(80, 0)
        };

        picker.ColorChanged += color =>
        {
            if (_isUpdating) return;
            param.Value.Value = new float4(color.R, color.G, color.B, color.A);
        };

        return picker;
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
        _quadMesh.Size = new Vector2(size.x / ppu, size.y / ppu);
    }

    public override void Destroy(bool destroyingWorld)
    {
        UnbindMaterial();

        if (!destroyingWorld)
        {
            _loadedScene?.QueueFree();
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
        _paramContainer = null;
        _titleLabel = null;
        _labelFont = null;
        _labelFontSize = null;
        _labelFontColor = null;

        base.Destroy(destroyingWorld);
    }
}
