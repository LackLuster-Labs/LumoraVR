using System.Collections.Generic;
using Godot;
using Lumora.Core;
using Lumora.Core.GodotUI.Inspectors;
using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks.GodotUI.Inspectors;

#nullable enable

/// <summary>
/// Godot hook for the SlotInspector panel.
/// Builds the tree view and property editors dynamically.
/// </summary>
public sealed class SlotInspectorHook : ComponentHook<SlotInspector>
{
    // Scene node paths
    private const string TreePath = "MainPanel/VBox/Content/HierarchyScroll/Tree";
    private const string PropertiesPath = "MainPanel/VBox/Content/PropertiesScroll/VBox";
    private const string TitlePath = "MainPanel/VBox/Header/HBox/Title";
    private const string ParentButtonPath = "MainPanel/VBox/Header/HBox/ParentButton";

    // Viewport and rendering
    private SubViewport? _viewport;
    private MeshInstance3D? _meshInstance;
    private QuadMesh? _quadMesh;
    private StandardMaterial3D? _material;
    private Node? _loadedScene;

    // UI elements
    private Tree? _hierarchyTree;
    private VBoxContainer? _propertiesContainer;
    private Label? _titleLabel;
    private Button? _parentButton;

    // Collision for interaction
    private Area3D? _collisionArea;
    private CollisionShape3D? _collisionShape;
    private BoxShape3D? _boxShape;

    // State tracking
    private Slot? _boundTarget;
    private Slot? _boundSelected;
    private readonly Dictionary<TreeItem, Slot> _treeItemToSlot = new();
    private readonly Dictionary<Slot, TreeItem> _slotToTreeItem = new();

    public static IHook<SlotInspector> Constructor()
    {
        return new SlotInspectorHook();
    }

    public override void Initialize()
    {
        base.Initialize();

        var resScale = Owner.ResolutionScale.Value;

        // Create SubViewport
        _viewport = new SubViewport
        {
            Name = "SlotInspectorViewport",
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
        _meshInstance = new MeshInstance3D { Name = "SlotInspectorQuad" };
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
        BindSlot(Owner.TargetSlot.Target);

        Owner.OnDataRefresh += RefreshUI;

        AquaLogger.Log("SlotInspectorHook: Initialized");
    }

    private void LoadScene()
    {
        var scenePath = Owner.ScenePath.Value;
        if (string.IsNullOrEmpty(scenePath))
        {
            // Scene doesn't exist yet - create UI programmatically
            CreateDefaultUI();
            return;
        }

        var packedScene = GD.Load<PackedScene>(scenePath);
        if (packedScene == null)
        {
            AquaLogger.Warn($"SlotInspectorHook: Scene not found at '{scenePath}', creating default UI");
            CreateDefaultUI();
            return;
        }

        _loadedScene = packedScene.Instantiate();
        if (_loadedScene == null)
        {
            CreateDefaultUI();
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

    private void CreateDefaultUI()
    {
        // Create a programmatic UI since the scene file doesn't exist yet
        var panel = new PanelContainer();
        panel.Name = "MainPanel";
        panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var vbox = new VBoxContainer();
        vbox.Name = "VBox";
        panel.AddChild(vbox);

        // Header
        var header = new HBoxContainer();
        header.Name = "Header";
        vbox.AddChild(header);

        _titleLabel = new Label();
        _titleLabel.Name = "Title";
        _titleLabel.Text = "Slot Inspector";
        _titleLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(_titleLabel);

        _parentButton = new Button();
        _parentButton.Name = "ParentButton";
        _parentButton.Text = "↑";
        _parentButton.Pressed += () => Owner.NavigateToParent();
        header.AddChild(_parentButton);

        var closeButton = new Button();
        closeButton.Name = "CloseButton";
        closeButton.Text = "X";
        closeButton.Pressed += () => Owner.Close();
        header.AddChild(closeButton);

        // Content area
        var content = new HSplitContainer();
        content.Name = "Content";
        content.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        vbox.AddChild(content);

        // Hierarchy tree
        var hierarchyScroll = new ScrollContainer();
        hierarchyScroll.Name = "HierarchyScroll";
        hierarchyScroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        content.AddChild(hierarchyScroll);

        _hierarchyTree = new Tree();
        _hierarchyTree.Name = "Tree";
        _hierarchyTree.HideRoot = false;
        _hierarchyTree.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _hierarchyTree.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _hierarchyTree.ItemSelected += OnTreeItemSelected;
        _hierarchyTree.ItemActivated += OnTreeItemActivated;
        hierarchyScroll.AddChild(_hierarchyTree);

        // Properties panel
        var propertiesScroll = new ScrollContainer();
        propertiesScroll.Name = "PropertiesScroll";
        propertiesScroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        content.AddChild(propertiesScroll);

        _propertiesContainer = new VBoxContainer();
        _propertiesContainer.Name = "VBox";
        _propertiesContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        propertiesScroll.AddChild(_propertiesContainer);

        _viewport?.AddChild(panel);
        _loadedScene = panel;

        RebuildUI();
    }

    private void CacheSceneNodes()
    {
        _hierarchyTree = _loadedScene?.GetNodeOrNull<Tree>(TreePath);
        _propertiesContainer = _loadedScene?.GetNodeOrNull<VBoxContainer>(PropertiesPath);
        _titleLabel = _loadedScene?.GetNodeOrNull<Label>(TitlePath);
        _parentButton = _loadedScene?.GetNodeOrNull<Button>(ParentButtonPath);

        if (_hierarchyTree != null)
        {
            _hierarchyTree.ItemSelected += OnTreeItemSelected;
            _hierarchyTree.ItemActivated += OnTreeItemActivated;
        }

        if (_parentButton != null)
        {
            _parentButton.Pressed += () => Owner.NavigateToParent();
        }
    }

    private void BindSlot(Slot? slot)
    {
        if (_boundTarget == slot) return;

        UnbindSlot();
        _boundTarget = slot;

        if (_boundTarget != null)
        {
            _boundTarget.OnChildAdded += OnChildAdded;
            _boundTarget.OnChildRemoved += OnChildRemoved;
            _boundTarget.OnNameChanged += OnSlotNameChanged;
        }

        RebuildUI();
    }

    private void UnbindSlot()
    {
        if (_boundTarget != null)
        {
            _boundTarget.OnChildAdded -= OnChildAdded;
            _boundTarget.OnChildRemoved -= OnChildRemoved;
            _boundTarget.OnNameChanged -= OnSlotNameChanged;
        }
        _boundTarget = null;
    }

    private void OnChildAdded(Slot parent, Slot child)
    {
        RebuildUI();
    }

    private void OnChildRemoved(Slot parent, Slot child)
    {
        RebuildUI();
    }

    private void OnSlotNameChanged(Slot slot, string newName)
    {
        RefreshUI();
    }

    private void OnTreeItemSelected()
    {
        var selected = _hierarchyTree?.GetSelected();
        if (selected != null && _treeItemToSlot.TryGetValue(selected, out var slot))
        {
            Owner.SelectedSlot.Target = slot;
        }
    }

    private void OnTreeItemActivated()
    {
        // Double-click - open gizmo
        Owner.OpenGizmoForSelected();
    }

    private void RebuildUI()
    {
        if (_titleLabel != null)
        {
            _titleLabel.Text = _boundTarget != null
                ? $"Inspector: {_boundTarget.Name.Value}"
                : "Slot Inspector";
        }

        RebuildHierarchyTree();
        RebuildPropertiesPanel();
    }

    private void RebuildHierarchyTree()
    {
        if (_hierarchyTree == null) return;

        _hierarchyTree.Clear();
        _treeItemToSlot.Clear();
        _slotToTreeItem.Clear();

        if (_boundTarget == null) return;

        var root = _hierarchyTree.CreateItem();
        root.SetText(0, _boundTarget.Name.Value);
        _treeItemToSlot[root] = _boundTarget;
        _slotToTreeItem[_boundTarget] = root;

        BuildTreeRecursive(root, _boundTarget);

        // Select current slot
        if (Owner.SelectedSlot.Target != null &&
            _slotToTreeItem.TryGetValue(Owner.SelectedSlot.Target, out var selectedItem))
        {
            selectedItem.Select(0);
        }
    }

    private void BuildTreeRecursive(TreeItem parent, Slot slot, int depth = 0)
    {
        if (depth > 10) return; // Prevent infinite recursion

        foreach (var child in slot.Children)
        {
            var item = _hierarchyTree!.CreateItem(parent);
            item.SetText(0, child.Name.Value);
            _treeItemToSlot[item] = child;
            _slotToTreeItem[child] = item;

            if (child.ChildCount > 0)
            {
                item.Collapsed = true; // Start collapsed
                BuildTreeRecursive(item, child, depth + 1);
            }
        }
    }

    private void RebuildPropertiesPanel()
    {
        if (_propertiesContainer == null) return;

        // Clear existing
        foreach (var child in _propertiesContainer.GetChildren())
        {
            child.QueueFree();
        }

        var selected = Owner.SelectedSlot.Target;
        if (selected == null) return;

        // Add slot properties
        AddPropertyRow("Name", selected.Name.Value, v => selected.Name.Value = v?.ToString() ?? "");
        AddPropertyRow("Active", selected.ActiveSelf.Value, v => selected.ActiveSelf.Value = (bool)v);
        AddVector3Row("Position", selected.LocalPosition.Value, v => selected.LocalPosition.Value = v);
        AddQuaternionRow("Rotation", selected.LocalRotation.Value, v => selected.LocalRotation.Value = v);
        AddVector3Row("Scale", selected.LocalScale.Value, v => selected.LocalScale.Value = v);

        // Components section
        if (Owner.ShowComponents.Value)
        {
            var separator = new HSeparator();
            _propertiesContainer.AddChild(separator);

            var componentsLabel = new Label();
            componentsLabel.Text = "Components";
            _propertiesContainer.AddChild(componentsLabel);

            foreach (var component in selected.Components)
            {
                AddComponentSection(component);
            }
        }
    }

    private void AddPropertyRow(string label, string value, System.Action<object> onChange)
    {
        var row = new HBoxContainer();

        var labelNode = new Label();
        labelNode.Text = label;
        labelNode.CustomMinimumSize = new Vector2(80, 0);
        row.AddChild(labelNode);

        var edit = new LineEdit();
        edit.Text = value;
        edit.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        edit.TextSubmitted += newText => onChange(newText);
        row.AddChild(edit);

        _propertiesContainer!.AddChild(row);
    }

    private void AddPropertyRow(string label, bool value, System.Action<object> onChange)
    {
        var row = new HBoxContainer();

        var labelNode = new Label();
        labelNode.Text = label;
        labelNode.CustomMinimumSize = new Vector2(80, 0);
        row.AddChild(labelNode);

        var check = new CheckBox();
        check.ButtonPressed = value;
        check.Toggled += newValue => onChange(newValue);
        row.AddChild(check);

        _propertiesContainer!.AddChild(row);
    }

    private void AddVector3Row(string label, float3 value, System.Action<float3> onChange)
    {
        var row = new HBoxContainer();

        var labelNode = new Label();
        labelNode.Text = label;
        labelNode.CustomMinimumSize = new Vector2(80, 0);
        row.AddChild(labelNode);

        var xSpin = CreateSpinBox(value.x, v => onChange(new float3((float)v, value.y, value.z)));
        var ySpin = CreateSpinBox(value.y, v => onChange(new float3(value.x, (float)v, value.z)));
        var zSpin = CreateSpinBox(value.z, v => onChange(new float3(value.x, value.y, (float)v)));

        row.AddChild(xSpin);
        row.AddChild(ySpin);
        row.AddChild(zSpin);

        _propertiesContainer!.AddChild(row);
    }

    private void AddQuaternionRow(string label, floatQ value, System.Action<floatQ> onChange)
    {
        // Display as euler angles for easier editing (convert radians to degrees)
        const float RadToDeg = 57.2957795f;
        const float DegToRad = 0.0174532925f;
        var eulerRad = value.ToEuler();
        var euler = new float3(eulerRad.x * RadToDeg, eulerRad.y * RadToDeg, eulerRad.z * RadToDeg);

        var row = new HBoxContainer();

        var labelNode = new Label();
        labelNode.Text = label;
        labelNode.CustomMinimumSize = new Vector2(80, 0);
        row.AddChild(labelNode);

        var xSpin = CreateSpinBox(euler.x, v =>
        {
            var newEuler = new float3((float)v * DegToRad, euler.y * DegToRad, euler.z * DegToRad);
            onChange(floatQ.FromEuler(newEuler));
        });
        var ySpin = CreateSpinBox(euler.y, v =>
        {
            var newEuler = new float3(euler.x * DegToRad, (float)v * DegToRad, euler.z * DegToRad);
            onChange(floatQ.FromEuler(newEuler));
        });
        var zSpin = CreateSpinBox(euler.z, v =>
        {
            var newEuler = new float3(euler.x * DegToRad, euler.y * DegToRad, (float)v * DegToRad);
            onChange(floatQ.FromEuler(newEuler));
        });

        row.AddChild(xSpin);
        row.AddChild(ySpin);
        row.AddChild(zSpin);

        _propertiesContainer!.AddChild(row);
    }

    private SpinBox CreateSpinBox(double value, System.Action<double> onChange)
    {
        var spin = new SpinBox();
        spin.Step = 0.01;
        spin.AllowGreater = true;
        spin.AllowLesser = true;
        spin.MinValue = -1000;
        spin.MaxValue = 1000;
        spin.Value = value;
        spin.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        spin.CustomMinimumSize = new Vector2(60, 0);
        spin.ValueChanged += (v) => onChange(v);
        return spin;
    }

    private void AddComponentSection(Component component)
    {
        var header = new HBoxContainer();

        var label = new Label();
        label.Text = component.GetType().Name;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(label);

        var removeBtn = new Button();
        removeBtn.Text = "X";
        removeBtn.Pressed += () =>
        {
            component.Slot.RemoveComponent(component);
            RebuildPropertiesPanel();
        };
        header.AddChild(removeBtn);

        _propertiesContainer!.AddChild(header);

        // TODO: Add component property editors using reflection
    }

    private void RefreshUI()
    {
        RebuildPropertiesPanel();
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
            BindSlot(Owner.TargetSlot.Target);
        }

        if (Owner.SelectedSlot.GetWasChangedAndClear())
        {
            RebuildPropertiesPanel();

            // Update tree selection
            if (Owner.SelectedSlot.Target != null &&
                _slotToTreeItem.TryGetValue(Owner.SelectedSlot.Target, out var item))
            {
                item.Select(0);
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
        _collisionArea.Name = "InspectorCollision";
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
        UnbindSlot();

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
        _hierarchyTree = null;
        _propertiesContainer = null;
        _titleLabel = null;
        _parentButton = null;
        _collisionArea = null;
        _collisionShape = null;
        _boxShape = null;
        _viewport = null;
        _meshInstance = null;
        _material = null;
        _quadMesh = null;
        _treeItemToSlot.Clear();
        _slotToTreeItem.Clear();

        base.Destroy(destroyingWorld);
    }
}
