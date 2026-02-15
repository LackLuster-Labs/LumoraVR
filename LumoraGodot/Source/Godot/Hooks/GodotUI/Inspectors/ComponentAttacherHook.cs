using System.Collections.Generic;
using System.Linq;
using Godot;
using Lumora.Core;
using Lumora.Core.GodotUI.Inspectors;
using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks.GodotUI.Inspectors;

#nullable enable

/// <summary>
/// Godot hook for the ComponentAttacher wizard panel.
/// Displays a searchable list of component types that can be attached.
/// </summary>
public sealed class ComponentAttacherHook : ComponentHook<ComponentAttacher>
{
    // Viewport and rendering
    private SubViewport? _viewport;
    private MeshInstance3D? _meshInstance;
    private QuadMesh? _quadMesh;
    private StandardMaterial3D? _material;
    private Node? _loadedScene;

    // UI elements
    private PanelContainer? _mainPanel;
    private LineEdit? _searchBox;
    private OptionButton? _categoryDropdown;
    private VBoxContainer? _componentList;
    private Label? _titleLabel;
    private Label? _targetLabel;

    // Collision for interaction
    private Area3D? _collisionArea;
    private CollisionShape3D? _collisionShape;
    private BoxShape3D? _boxShape;

    // State
    private readonly Dictionary<Button, ComponentTypeInfo> _buttonToType = new();

    public static IHook<ComponentAttacher> Constructor()
    {
        return new ComponentAttacherHook();
    }

    public override void Initialize()
    {
        base.Initialize();

        var resScale = Owner.ResolutionScale.Value;

        // Create SubViewport
        _viewport = new SubViewport
        {
            Name = "ComponentAttacherViewport",
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

        // Create mesh for 3D display
        _meshInstance = new MeshInstance3D { Name = "ComponentAttacherQuad" };
        _quadMesh = new QuadMesh();
        UpdateQuadSize();
        _meshInstance.Mesh = _quadMesh;

        // Create material
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

        Owner.OnDataRefresh += RefreshUI;
        Owner.OnSearchChanged += _ => RebuildComponentList();
        Owner.OnCategoryChanged += _ => RebuildComponentList();
        Owner.OnComponentAttached += OnComponentAttached;

        AquaLogger.Log("ComponentAttacherHook: Initialized");
    }

    private void LoadScene()
    {
        var scenePath = Owner.ScenePath.Value;
        if (!string.IsNullOrEmpty(scenePath) && ResourceLoader.Exists(scenePath))
        {
            var packedScene = GD.Load<PackedScene>(scenePath);
            if (packedScene != null)
            {
                _loadedScene = packedScene.Instantiate();
                if (_loadedScene != null)
                {
                    _viewport?.AddChild(_loadedScene);
                    if (_loadedScene is Control control)
                    {
                        control.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                    }
                    CacheSceneNodes();
                    RebuildUI();
                    return;
                }
            }
        }

        // Scene doesn't exist - create UI programmatically
        CreateDefaultUI();
    }

    private void CreateDefaultUI()
    {
        _mainPanel = new PanelContainer();
        _mainPanel.Name = "MainPanel";
        _mainPanel.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var styleBox = new StyleBoxFlat();
        styleBox.BgColor = new Color(0.12f, 0.12f, 0.14f, 0.95f);
        styleBox.SetCornerRadiusAll(6);
        _mainPanel.AddThemeStyleboxOverride("panel", styleBox);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 8);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_right", 8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        _mainPanel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.Name = "VBox";
        margin.AddChild(vbox);

        // Header
        CreateHeader(vbox);

        // Search and filter
        CreateSearchBar(vbox);

        // Component list
        CreateComponentListArea(vbox);

        _viewport?.AddChild(_mainPanel);
        _loadedScene = _mainPanel;

        RebuildUI();
    }

    private void CreateHeader(VBoxContainer parent)
    {
        var headerRow = new HBoxContainer();
        parent.AddChild(headerRow);

        _titleLabel = new Label();
        _titleLabel.Text = "Attach Component";
        _titleLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _titleLabel.AddThemeFontSizeOverride("font_size", 18);
        headerRow.AddChild(_titleLabel);

        var closeButton = new Button();
        closeButton.Text = "X";
        closeButton.CustomMinimumSize = new Vector2(30, 30);
        closeButton.Pressed += () => Owner.Close();
        headerRow.AddChild(closeButton);

        // Target slot label
        _targetLabel = new Label();
        _targetLabel.Text = "Target: (none)";
        _targetLabel.AddThemeFontSizeOverride("font_size", 12);
        _targetLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        parent.AddChild(_targetLabel);

        var separator = new HSeparator();
        parent.AddChild(separator);
    }

    private void CreateSearchBar(VBoxContainer parent)
    {
        var searchRow = new HBoxContainer();
        parent.AddChild(searchRow);

        _searchBox = new LineEdit();
        _searchBox.PlaceholderText = "Search components...";
        _searchBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _searchBox.ClearButtonEnabled = true;
        _searchBox.TextChanged += text =>
        {
            Owner.SearchFilter.Value = text;
        };
        searchRow.AddChild(_searchBox);

        var clearBtn = new Button();
        clearBtn.Text = "Clear";
        clearBtn.Pressed += () =>
        {
            _searchBox.Text = "";
            Owner.SearchFilter.Value = "";
        };
        searchRow.AddChild(clearBtn);

        // Category filter
        var categoryRow = new HBoxContainer();
        parent.AddChild(categoryRow);

        var categoryLabel = new Label();
        categoryLabel.Text = "Category:";
        categoryRow.AddChild(categoryLabel);

        _categoryDropdown = new OptionButton();
        _categoryDropdown.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _categoryDropdown.ItemSelected += index =>
        {
            var text = _categoryDropdown.GetItemText((int)index);
            Owner.CategoryFilter.Value = text == "All" ? "" : text;
        };
        categoryRow.AddChild(_categoryDropdown);

        PopulateCategoryDropdown();
    }

    private void CreateComponentListArea(VBoxContainer parent)
    {
        var scroll = new ScrollContainer();
        scroll.Name = "ComponentScroll";
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        parent.AddChild(scroll);

        _componentList = new VBoxContainer();
        _componentList.Name = "ComponentList";
        _componentList.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_componentList);
    }

    private void CacheSceneNodes()
    {
        _searchBox = _loadedScene?.GetNodeOrNull<LineEdit>("MainPanel/VBox/SearchRow/SearchBox");
        _categoryDropdown = _loadedScene?.GetNodeOrNull<OptionButton>("MainPanel/VBox/CategoryRow/CategoryDropdown");
        _componentList = _loadedScene?.GetNodeOrNull<VBoxContainer>("MainPanel/VBox/ComponentScroll/ComponentList");
        _titleLabel = _loadedScene?.GetNodeOrNull<Label>("MainPanel/VBox/Header/Title");
        _targetLabel = _loadedScene?.GetNodeOrNull<Label>("MainPanel/VBox/TargetLabel");

        var closeBtn = _loadedScene?.GetNodeOrNull<Button>("MainPanel/VBox/Header/CloseButton");
        closeBtn?.Connect("pressed", Callable.From(() => Owner.Close()));

        if (_searchBox != null)
        {
            _searchBox.TextChanged += text => Owner.SearchFilter.Value = text;
        }

        if (_categoryDropdown != null)
        {
            _categoryDropdown.ItemSelected += index =>
            {
                var text = _categoryDropdown.GetItemText((int)index);
                Owner.CategoryFilter.Value = text == "All" ? "" : text;
            };
        }

        PopulateCategoryDropdown();
    }

    private void PopulateCategoryDropdown()
    {
        if (_categoryDropdown == null) return;

        _categoryDropdown.Clear();
        _categoryDropdown.AddItem("All");

        foreach (var category in Owner.GetCategories())
        {
            _categoryDropdown.AddItem(category);
        }

        // Select current category
        var currentCategory = Owner.CategoryFilter.Value;
        if (string.IsNullOrEmpty(currentCategory))
        {
            _categoryDropdown.Select(0);
        }
        else
        {
            for (int i = 0; i < _categoryDropdown.ItemCount; i++)
            {
                if (_categoryDropdown.GetItemText(i) == currentCategory)
                {
                    _categoryDropdown.Select(i);
                    break;
                }
            }
        }
    }

    private void RebuildUI()
    {
        UpdateLabels();
        RebuildComponentList();
    }

    private void UpdateLabels()
    {
        if (_targetLabel != null)
        {
            var target = Owner.TargetSlot.Target;
            _targetLabel.Text = target != null
                ? $"Target: {target.Name.Value}"
                : "Target: (none)";
        }
    }

    private void RebuildComponentList()
    {
        if (_componentList == null) return;

        // Clear existing
        foreach (var child in _componentList.GetChildren())
        {
            child.QueueFree();
        }
        _buttonToType.Clear();

        var filteredComponents = Owner.GetFilteredComponents().ToList();

        if (filteredComponents.Count == 0)
        {
            var noResultsLabel = new Label();
            noResultsLabel.Text = "No components found";
            noResultsLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
            _componentList.AddChild(noResultsLabel);
            return;
        }

        // Group by category
        var grouped = filteredComponents.GroupBy(c => c.Category).OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            // Category header
            var categoryHeader = new Label();
            categoryHeader.Text = group.Key;
            categoryHeader.AddThemeFontSizeOverride("font_size", 14);
            categoryHeader.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.5f));
            _componentList.AddChild(categoryHeader);

            // Components in category
            foreach (var componentInfo in group.OrderBy(c => c.Name))
            {
                AddComponentButton(componentInfo);
            }

            // Spacer
            var spacer = new Control();
            spacer.CustomMinimumSize = new Vector2(0, 8);
            _componentList.AddChild(spacer);
        }
    }

    private void AddComponentButton(ComponentTypeInfo typeInfo)
    {
        var button = new Button();
        button.Name = $"Component_{typeInfo.FullName}";
        button.Text = typeInfo.Name;
        button.TooltipText = $"{typeInfo.FullName}\n{typeInfo.Namespace}";
        button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        button.Alignment = HorizontalAlignment.Left;

        button.Pressed += () =>
        {
            Owner.AttachComponent(typeInfo.Type);
        };

        _buttonToType[button] = typeInfo;
        _componentList!.AddChild(button);
    }

    private void OnComponentAttached(Slot slot, Component component)
    {
        AquaLogger.Log($"ComponentAttacherHook: Attached {component.GetType().Name} to '{slot.Name.Value}'");
    }

    private void RefreshUI()
    {
        UpdateLabels();
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

        if (Owner.TargetSlot.GetWasChangedAndClear())
        {
            UpdateLabels();
        }

        if (Owner.SearchFilter.GetWasChangedAndClear() ||
            Owner.CategoryFilter.GetWasChangedAndClear())
        {
            RebuildComponentList();
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
        _collisionArea.Name = "AttacherCollision";
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

    public override void Destroy(bool destroyingWorld)
    {
        Owner.OnDataRefresh -= RefreshUI;
        Owner.OnSearchChanged -= _ => RebuildComponentList();
        Owner.OnCategoryChanged -= _ => RebuildComponentList();
        Owner.OnComponentAttached -= OnComponentAttached;

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
        _mainPanel = null;
        _searchBox = null;
        _categoryDropdown = null;
        _componentList = null;
        _titleLabel = null;
        _targetLabel = null;
        _collisionArea = null;
        _collisionShape = null;
        _boxShape = null;
        _viewport = null;
        _meshInstance = null;
        _material = null;
        _quadMesh = null;
        _buttonToType.Clear();

        base.Destroy(destroyingWorld);
    }
}
