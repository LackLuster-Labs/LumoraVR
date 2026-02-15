using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using Lumora.Core;
using Lumora.Core.GodotUI;
using Lumora.Core.GodotUI.Inspectors;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks.GodotUI.Inspectors;

#nullable enable

/// <summary>
/// Godot hook for the SceneInspector panel.
/// Builds a combined hierarchy tree and component property view.
/// </summary>
public sealed class SceneInspectorHook : ComponentHook<SceneInspector>
{
    // Viewport and rendering
    private SubViewport? _viewport;
    private MeshInstance3D? _meshInstance;
    private QuadMesh? _quadMesh;
    private StandardMaterial3D? _material;
    private Node? _loadedScene;

    // UI containers
    private PanelContainer? _mainPanel;
    private Tree? _hierarchyTree;
    private VBoxContainer? _componentContainer;
    private Label? _titleLabel;
    private Label? _rootLabel;
    private HSplitContainer? _splitContainer;
    private ScrollContainer? _hierarchyScroll;
    private ScrollContainer? _componentScroll;

    // Scroll/drag state
    private bool _isDragging;
    private Vector2 _lastMousePos;
    private const float ScrollSpeed = 30f;
    private const float DragScrollSpeed = 2f;

    // Buttons
    private Button? _rootUpButton;
    private Button? _setRootButton;
    private Button? _addChildButton;
    private Button? _duplicateButton;
    private Button? _destroyButton;
    private Button? _attachComponentButton;
    private CheckButton? _inheritedToggle;

    // Collision for interaction
    private Area3D? _collisionArea;
    private CollisionShape3D? _collisionShape;
    private BoxShape3D? _boxShape;

    // State tracking
    private Slot? _boundRoot;
    private Slot? _observedComponentSlot; // Slot we're listening for component add/remove on
    private string? _lastBuiltSelectionId; // Track RefID of what selection the component panel was last built for
    private Vector2I _lastViewportSize = Vector2I.Zero;
    private readonly Dictionary<TreeItem, Slot> _treeItemToSlot = new();
    private readonly Dictionary<Slot, TreeItem> _slotToTreeItem = new();
    private readonly HashSet<string> _expandedComponents = new(); // Track which components are expanded by type name
    private Action<Slot?>? _rootChangedHandler;
    private Action<Slot?>? _selectionChangedHandler;

    public static IHook<SceneInspector> Constructor()
    {
        return new SceneInspectorHook();
    }

    public override void Initialize()
    {
        base.Initialize();

        var resScale = Owner.ResolutionScale.Value;

        // Create SubViewport
        _viewport = new SubViewport
        {
            Name = "SceneInspectorViewport",
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
        _meshInstance = new MeshInstance3D { Name = "SceneInspectorQuad" };
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
        SetupInputHandling();
        BindRoot(Owner.Root.Target);

        Owner.OnDataRefresh += RefreshUI;
        _rootChangedHandler = OnOwnerRootChanged;
        _selectionChangedHandler = OnOwnerSelectionChanged;
        Owner.OnRootChanged += _rootChangedHandler;
        Owner.OnSelectionChanged += _selectionChangedHandler;
        Owner.OnAttachComponentRequested += OnAttachComponentRequested;

        AquaLogger.Log("SceneInspectorHook: Initialized");
    }

    private void OnOwnerRootChanged(Slot? root)
    {
        BindRoot(root);
    }

    private void OnOwnerSelectionChanged(Slot? newSelection)
    {
        try
        {
            // Unsubscribe from previous slot's component events
            if (_observedComponentSlot != null)
            {
                _observedComponentSlot.OnComponentAdded -= OnComponentListChanged;
                _observedComponentSlot.OnComponentRemoved -= OnComponentListChanged;
                _observedComponentSlot = null;
            }

            // Subscribe to new slot's component events following the component inspector pattern.
            if (newSelection != null && !newSelection.IsDestroyed)
            {
                newSelection.OnComponentAdded += OnComponentListChanged;
                newSelection.OnComponentRemoved += OnComponentListChanged;
                _observedComponentSlot = newSelection;
            }

            _lastBuiltSelectionId = null;
            RebuildComponentPanel();
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"SceneInspectorHook: Selection change rebuild failed: {ex.Message}");
        }
    }

    private void OnComponentListChanged(Slot slot, Component component)
    {
        // A component was added or removed from the selected slot - force rebuild
        _lastBuiltSelectionId = null;
        RebuildComponentPanel();
    }

    private void SetupInputHandling()
    {
        if (_viewport == null) return;

        // Create a control to capture input events for the entire viewport
        var inputCapture = new Control();
        inputCapture.Name = "InputCapture";
        inputCapture.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        inputCapture.MouseFilter = Control.MouseFilterEnum.Pass; // Pass through but still receive events
        inputCapture.GuiInput += OnViewportInput;
        _viewport.AddChild(inputCapture);
        _viewport.MoveChild(inputCapture, 0); // Move to back so it doesn't block other controls
    }

    private void OnViewportInput(InputEvent @event)
    {
        // Mouse wheel scrolling
        if (@event is InputEventMouseButton mouseButton)
        {
            if (mouseButton.ButtonIndex == MouseButton.WheelUp && mouseButton.Pressed)
            {
                ScrollActivePanel(-ScrollSpeed);
                return;
            }
            if (mouseButton.ButtonIndex == MouseButton.WheelDown && mouseButton.Pressed)
            {
                ScrollActivePanel(ScrollSpeed);
                return;
            }

            // Right mouse button for drag scrolling
            if (mouseButton.ButtonIndex == MouseButton.Right)
            {
                _isDragging = mouseButton.Pressed;
                _lastMousePos = mouseButton.Position;
                return;
            }
        }

        // Mouse motion for drag scrolling
        if (@event is InputEventMouseMotion mouseMotion && _isDragging)
        {
            var delta = mouseMotion.Position - _lastMousePos;
            _lastMousePos = mouseMotion.Position;

            // Scroll both panels based on mouse position
            ScrollActivePanel(-delta.Y * DragScrollSpeed);
        }
    }

    private void ScrollActivePanel(float amount)
    {
        // Determine which panel the mouse is over based on split position
        var mousePos = _viewport?.GetMousePosition() ?? Vector2.Zero;
        var splitOffset = _splitContainer?.SplitOffset ?? 0;

        if (mousePos.X < splitOffset)
        {
            // Mouse is over hierarchy panel - Tree handles its own scrolling
            // We don't need to manually scroll the tree as it has built-in scroll
        }
        else
        {
            // Mouse is over component panel
            if (_componentScroll != null)
            {
                _componentScroll.ScrollVertical += (int)amount;
            }
        }
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

        // Header with title and controls
        CreateHeader(vbox);

        // Split container for hierarchy and components
        _splitContainer = new HSplitContainer();
        _splitContainer.Name = "SplitContainer";
        _splitContainer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _splitContainer.SplitOffset = (int)(Owner.Size.Value.x * Owner.SplitRatio.Value);
        vbox.AddChild(_splitContainer);

        // Left panel - Hierarchy
        CreateHierarchyPanel(_splitContainer);

        // Right panel - Components
        CreateComponentPanel(_splitContainer);

        _viewport?.AddChild(_mainPanel);
        _loadedScene = _mainPanel;

        RebuildUI();
    }

    private void CreateHeader(VBoxContainer parent)
    {
        var headerBox = new VBoxContainer();
        headerBox.Name = "Header";
        parent.AddChild(headerBox);

        // Title row
        var titleRow = new HBoxContainer();
        titleRow.Name = "TitleRow";
        headerBox.AddChild(titleRow);

        _titleLabel = new Label();
        _titleLabel.Name = "Title";
        _titleLabel.Text = "Scene Inspector";
        _titleLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _titleLabel.AddThemeFontSizeOverride("font_size", 18);
        titleRow.AddChild(_titleLabel);

        var closeButton = new Button();
        closeButton.Name = "CloseButton";
        closeButton.Text = "X";
        closeButton.CustomMinimumSize = new Vector2(30, 30);
        closeButton.Pressed += () => Owner.Close();
        titleRow.AddChild(closeButton);

        // Root navigation row
        var rootRow = new HBoxContainer();
        rootRow.Name = "RootRow";
        headerBox.AddChild(rootRow);

        _rootUpButton = new Button();
        _rootUpButton.Name = "RootUpButton";
        _rootUpButton.Text = "^";
        _rootUpButton.TooltipText = "Navigate to parent";
        _rootUpButton.CustomMinimumSize = new Vector2(30, 30);
        _rootUpButton.Pressed += () => Owner.NavigateRootUp();
        rootRow.AddChild(_rootUpButton);

        _rootLabel = new Label();
        _rootLabel.Name = "RootLabel";
        _rootLabel.Text = "Root: (none)";
        _rootLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        rootRow.AddChild(_rootLabel);

        _setRootButton = new Button();
        _setRootButton.Name = "SetRootButton";
        _setRootButton.Text = "Set Root";
        _setRootButton.TooltipText = "Set selection as hierarchy root";
        _setRootButton.Pressed += () => Owner.SetSelectionAsRoot();
        rootRow.AddChild(_setRootButton);

        // Toolbar
        var toolbar = new HBoxContainer();
        toolbar.Name = "Toolbar";
        headerBox.AddChild(toolbar);

        _addChildButton = new Button();
        _addChildButton.Name = "AddChildButton";
        _addChildButton.Text = "+ Child";
        _addChildButton.TooltipText = "Add child slot";
        _addChildButton.Pressed += () => Owner.AddChild();
        toolbar.AddChild(_addChildButton);

        _duplicateButton = new Button();
        _duplicateButton.Name = "DuplicateButton";
        _duplicateButton.Text = "Dup";
        _duplicateButton.TooltipText = "Duplicate selection";
        _duplicateButton.Pressed += () => Owner.DuplicateSelection();
        toolbar.AddChild(_duplicateButton);

        _destroyButton = new Button();
        _destroyButton.Name = "DestroyButton";
        _destroyButton.Text = "Del";
        _destroyButton.TooltipText = "Destroy selection";
        _destroyButton.Pressed += () => Owner.DestroySelection();
        toolbar.AddChild(_destroyButton);

        var spacer = new Control();
        spacer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        toolbar.AddChild(spacer);

        _attachComponentButton = new Button();
        _attachComponentButton.Name = "AttachComponentButton";
        _attachComponentButton.Text = "+ Component";
        _attachComponentButton.TooltipText = "Attach component";
        _attachComponentButton.Pressed += () => Owner.OpenComponentAttacher();
        toolbar.AddChild(_attachComponentButton);

        _inheritedToggle = new CheckButton();
        _inheritedToggle.Name = "InheritedToggle";
        _inheritedToggle.Text = "Inherited";
        _inheritedToggle.TooltipText = "Show inherited members";
        _inheritedToggle.ButtonPressed = Owner.ShowInherited.Value;
        _inheritedToggle.Toggled += pressed =>
        {
            Owner.ShowInherited.Value = pressed;
            RebuildComponentPanel();
        };
        toolbar.AddChild(_inheritedToggle);

        // Separator
        var separator = new HSeparator();
        headerBox.AddChild(separator);
    }

    private void CreateHierarchyPanel(HSplitContainer parent)
    {
        var hierarchyPanel = new PanelContainer();
        hierarchyPanel.Name = "HierarchyPanel";
        hierarchyPanel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        parent.AddChild(hierarchyPanel);

        var hierarchyVBox = new VBoxContainer();
        hierarchyPanel.AddChild(hierarchyVBox);

        var hierarchyLabel = new Label();
        hierarchyLabel.Text = "Hierarchy";
        hierarchyLabel.AddThemeFontSizeOverride("font_size", 14);
        hierarchyVBox.AddChild(hierarchyLabel);

        // Tree has built-in scrolling, so we add it directly to the VBox
        // Using a wrapper ScrollContainer can interfere with Tree's mouse input
        _hierarchyTree = new Tree();
        _hierarchyTree.Name = "HierarchyTree";
        _hierarchyTree.HideRoot = false;
        _hierarchyTree.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _hierarchyTree.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _hierarchyTree.SelectMode = Tree.SelectModeEnum.Single;
        _hierarchyTree.AllowRmbSelect = true;
        _hierarchyTree.FocusMode = Control.FocusModeEnum.All;
        _hierarchyTree.MouseFilter = Control.MouseFilterEnum.Stop; // Ensure tree captures mouse events
        _hierarchyTree.ItemSelected += OnTreeItemSelected;
        _hierarchyTree.ItemActivated += OnTreeItemActivated;
        // Add direct GUI input handling as fallback for PushInput events
        _hierarchyTree.GuiInput += OnTreeGuiInput;
        hierarchyVBox.AddChild(_hierarchyTree);

        // Keep scroll reference as null since Tree handles its own scrolling
        _hierarchyScroll = null;
    }

    private void CreateComponentPanel(HSplitContainer parent)
    {
        var componentPanel = new PanelContainer();
        componentPanel.Name = "ComponentPanel";
        componentPanel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        parent.AddChild(componentPanel);

        var componentVBox = new VBoxContainer();
        componentPanel.AddChild(componentVBox);

        var componentLabel = new Label();
        componentLabel.Text = "Components";
        componentLabel.AddThemeFontSizeOverride("font_size", 14);
        componentVBox.AddChild(componentLabel);

        _componentScroll = new ScrollContainer();
        _componentScroll.Name = "ComponentScroll";
        _componentScroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        componentVBox.AddChild(_componentScroll);

        _componentContainer = new VBoxContainer();
        _componentContainer.Name = "ComponentContainer";
        _componentContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _componentScroll.AddChild(_componentContainer);
    }

    private void CacheSceneNodes()
    {
        // Cache key nodes from the .tscn layout (paths match SceneInspector.tscn)
        _hierarchyTree = _loadedScene?.GetNodeOrNull<Tree>("MainPanel/VBox/SplitContainer/HierarchyPanel/VBox/HierarchyTree");
        _componentContainer = _loadedScene?.GetNodeOrNull<VBoxContainer>("MainPanel/VBox/SplitContainer/ComponentPanel/VBox/ComponentScroll/ComponentContainer");
        _hierarchyScroll = null; // Tree handles its own scrolling
        _componentScroll = _loadedScene?.GetNodeOrNull<ScrollContainer>("MainPanel/VBox/SplitContainer/ComponentPanel/VBox/ComponentScroll");
        _splitContainer = _loadedScene?.GetNodeOrNull<HSplitContainer>("MainPanel/VBox/SplitContainer");
        _titleLabel = _loadedScene?.GetNodeOrNull<Label>("MainPanel/VBox/Header/TitleRow/HBox/Title");
        _rootLabel = _loadedScene?.GetNodeOrNull<Label>("MainPanel/VBox/Header/RootRow/RootLabel");

        // Connect buttons
        var closeBtn = _loadedScene?.GetNodeOrNull<Button>("MainPanel/VBox/Header/TitleRow/HBox/CloseButton");
        closeBtn?.Connect("pressed", Callable.From(() => Owner.Close()));

        _rootUpButton = _loadedScene?.GetNodeOrNull<Button>("MainPanel/VBox/Header/RootRow/RootUpButton");
        _rootUpButton?.Connect("pressed", Callable.From(() => Owner.NavigateRootUp()));

        _setRootButton = _loadedScene?.GetNodeOrNull<Button>("MainPanel/VBox/Header/RootRow/SetRootButton");
        _setRootButton?.Connect("pressed", Callable.From(() => Owner.SetSelectionAsRoot()));

        _addChildButton = _loadedScene?.GetNodeOrNull<Button>("MainPanel/VBox/Header/Toolbar/HBox/AddChildButton");
        _addChildButton?.Connect("pressed", Callable.From(() => Owner.AddChild()));

        _duplicateButton = _loadedScene?.GetNodeOrNull<Button>("MainPanel/VBox/Header/Toolbar/HBox/DuplicateButton");
        _duplicateButton?.Connect("pressed", Callable.From(() => Owner.DuplicateSelection()));

        _destroyButton = _loadedScene?.GetNodeOrNull<Button>("MainPanel/VBox/Header/Toolbar/HBox/DestroyButton");
        _destroyButton?.Connect("pressed", Callable.From(() => Owner.DestroySelection()));

        _attachComponentButton = _loadedScene?.GetNodeOrNull<Button>("MainPanel/VBox/Header/Toolbar/HBox/AttachComponentButton");
        _attachComponentButton?.Connect("pressed", Callable.From(() => Owner.OpenComponentAttacher()));

        _inheritedToggle = _loadedScene?.GetNodeOrNull<CheckButton>("MainPanel/VBox/Header/Toolbar/HBox/InheritedToggle");
        if (_inheritedToggle != null)
        {
            _inheritedToggle.ButtonPressed = Owner.ShowInherited.Value;
            _inheritedToggle.Toggled += pressed =>
            {
                Owner.ShowInherited.Value = pressed;
                RebuildComponentPanel();
            };
        }

        if (_hierarchyTree != null)
        {
            _hierarchyTree.ItemSelected += OnTreeItemSelected;
            _hierarchyTree.ItemActivated += OnTreeItemActivated;
            _hierarchyTree.GuiInput += OnTreeGuiInput;
            _hierarchyTree.FocusMode = Control.FocusModeEnum.All;
            _hierarchyTree.MouseFilter = Control.MouseFilterEnum.Stop;
        }

        // Create Lumora bidirectional UI components for all static .tscn elements
        ParseStaticUIElements();
    }

    /// <summary>
    /// Parse the loaded .tscn scene and create Lumora UI components for each static Control node.
    /// This follows the GodotUIPanelHook.ParseSceneNode pattern, making the inspector UI
    /// itself inspectable and modifiable at runtime via Lumora components.
    /// </summary>
    private void ParseStaticUIElements()
    {
        if (_loadedScene == null) return;

        // Recursively parse all child controls from the scene root
        foreach (var child in _loadedScene.GetChildren())
        {
            ParseSceneNodeRecursive(child, "", Owner.Slot);
        }

        AquaLogger.Log($"SceneInspectorHook: Created Lumora UI components for .tscn elements");
    }

    private void ParseSceneNodeRecursive(Node node, string parentPath, Slot parentSlot)
    {
        var nodeName = node.Name.ToString();
        var nodePath = string.IsNullOrEmpty(parentPath) ? nodeName : $"{parentPath}/{nodeName}";

        if (node is not Control control)
        {
            foreach (var child in node.GetChildren())
            {
                ParseSceneNodeRecursive(child, nodePath, parentSlot);
            }
            return;
        }

        // Create child slot and appropriate Lumora component
        var elementSlot = parentSlot.AddSlot(nodeName);
        GodotUIElement? element = null;

        if (control is Label)
        {
            element = elementSlot.AttachComponent<GodotLabel>();
        }
        else if (control is CheckButton or Button)
        {
            element = elementSlot.AttachComponent<GodotButton>();
            // Wire button press to Owner.HandleButtonPress
            if (control is Button btn)
            {
                var capturedPath = nodePath;
                btn.Pressed += () => Owner.HandleButtonPress(capturedPath);
            }
        }
        else if (control is PanelContainer or Panel)
        {
            element = elementSlot.AttachComponent<GodotPanel>();
        }
        else if (control is ScrollContainer)
        {
            element = elementSlot.AttachComponent<GodotScrollContainer>();
        }
        else if (control is Tree)
        {
            // Tree is a special control - create generic UI element
            element = elementSlot.AttachComponent<GodotUIElement>();
        }
        else
        {
            // Generic UI element for containers (VBox, HBox, HSplitContainer, etc.)
            element = elementSlot.AttachComponent<GodotUIElement>();
        }

        if (element != null)
        {
            element.SceneNodePath.Value = nodePath;
            element.ParentPanel.Target = Owner;

            // Sync initial properties from the Godot node
            element.Visible.Value = control.Visible;
            var mod = control.Modulate;
            element.Modulate.Value = new color(mod.R, mod.G, mod.B, mod.A);
            var minSize = control.CustomMinimumSize;
            element.MinSize.Value = new float2(minSize.X, minSize.Y);
            element.SizeFlagsHorizontal.Value = (int)control.SizeFlagsHorizontal;
            element.SizeFlagsVertical.Value = (int)control.SizeFlagsVertical;

            if (element is GodotLabel label && control is Label godotLabel)
            {
                label.Text.Value = godotLabel.Text;
            }

            if (element is GodotButton button && control is Button godotButton)
            {
                button.Text.Value = godotButton.Text;
            }
        }

        // Recurse into children
        foreach (var child in node.GetChildren())
        {
            ParseSceneNodeRecursive(child, nodePath, elementSlot);
        }
    }

    // Slots we're currently listening to for child changes (all slots visible in the tree)
    private readonly HashSet<Slot> _observedSlots = new();

    private void BindRoot(Slot? slot)
    {
        if (_boundRoot == slot) return;

        UnsubscribeAllSlots();
        _boundRoot = slot;

        RebuildUI();
    }

    /// <summary>
    /// Subscribe to child-add/remove and name-change events on all slots visible in the hierarchy.
    /// This follows the pattern where each slot inspector listens to its own slot.
    /// </summary>
    private void SubscribeToSlot(Slot slot)
    {
        if (_observedSlots.Contains(slot)) return;
        _observedSlots.Add(slot);
        slot.OnChildAdded += OnChildChanged;
        slot.OnChildRemoved += OnChildChanged;
        slot.OnNameChanged += OnSlotNameChanged;
    }

    private void UnsubscribeAllSlots()
    {
        foreach (var slot in _observedSlots)
        {
            if (slot != null && !slot.IsDestroyed)
            {
                slot.OnChildAdded -= OnChildChanged;
                slot.OnChildRemoved -= OnChildChanged;
                slot.OnNameChanged -= OnSlotNameChanged;
            }
        }
        _observedSlots.Clear();
    }

    private void OnChildChanged(Slot parent, Slot child)
    {
        try
        {
            // Ignore transient inspector gizmo slots
            if (child.Name.Value.StartsWith("Gizmo_", StringComparison.Ordinal))
                return;

            RebuildHierarchyTree();
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"SceneInspectorHook: OnChildChanged exception: {ex.Message}");
        }
    }

    private void OnSlotNameChanged(Slot slot, string newName) => RefreshUI();

    private Slot? _pendingFocus;

    /// <summary>
    /// Direct GUI input handler for the Tree.
    /// PushInput from LaserPointer may not trigger Tree's internal ItemSelected signal,
    /// so we manually detect the clicked item and select it as a fallback.
    /// </summary>
    private void OnTreeGuiInput(InputEvent @event)
    {
        try
        {
            if (@event is not InputEventMouseButton mouseButton) return;
            if (!mouseButton.Pressed || mouseButton.ButtonIndex != MouseButton.Left) return;

            _hierarchyTree?.GrabFocus();

            var item = _hierarchyTree?.GetItemAtPosition(mouseButton.Position);
            if (item == null) return;

            // item.Select(0) may trigger OnTreeItemSelected synchronously.
            // That handler will call Owner.SelectSlot if it fires.
            item.Select(0);

            // Fallback: if the signal didn't fire (PushInput quirk), apply selection manually.
            if (_treeItemToSlot.TryGetValue(item, out var slot) && !slot.IsDestroyed)
            {
                if (Owner.ComponentView.Target != slot)
                {
                    Owner.SelectSlot(slot);
                }
            }
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"SceneInspectorHook: OnTreeGuiInput exception: {ex.Message}");
        }
    }

    private void OnTreeItemSelected()
    {
        try
        {
            var selected = _hierarchyTree?.GetSelected();
            if (selected == null) return;

            if (_treeItemToSlot.TryGetValue(selected, out var slot) && !slot.IsDestroyed)
            {
                Owner.SelectSlot(slot);
            }
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"SceneInspectorHook: OnTreeItemSelected exception: {ex.Message}");
        }
    }

    private void OnTreeItemActivated()
    {
        // Double-click focuses on slot (sets as root and selects)
        var selected = _hierarchyTree?.GetSelected();
        if (selected != null && _treeItemToSlot.TryGetValue(selected, out var slot) && !slot.IsDestroyed)
        {
            _pendingFocus = slot;
        }
    }

    private void ProcessPendingActions()
    {
        if (_pendingFocus != null)
        {
            var slot = _pendingFocus;
            _pendingFocus = null;
            Owner.FocusSlot(slot);
        }
    }

    private void OnAttachComponentRequested(Slot slot)
    {
        // The InspectorInputHandler will handle spawning the ComponentAttacher
        AquaLogger.Log($"SceneInspectorHook: Component attach requested for '{slot.Name.Value}'");
    }

    private void RebuildUI()
    {
        UpdateLabels();
        RebuildHierarchyTree();
        RebuildComponentPanel();
    }

    private void UpdateLabels()
    {
        if (_titleLabel != null)
        {
            var selection = Owner.ComponentView.Target;
            _titleLabel.Text = selection != null
                ? $"Scene Inspector - {selection.Name.Value}"
                : "Scene Inspector";
        }

        if (_rootLabel != null)
        {
            _rootLabel.Text = _boundRoot != null
                ? $"Root: {_boundRoot.Name.Value}"
                : "Root: (none)";
        }

        // Update button states
        var hasSelection = Owner.ComponentView.Target != null;
        var canDelete = hasSelection && !Owner.ComponentView.Target!.IsRootSlot;

        if (_duplicateButton != null) _duplicateButton.Disabled = !canDelete;
        if (_destroyButton != null) _destroyButton.Disabled = !canDelete;
        if (_addChildButton != null) _addChildButton.Disabled = !hasSelection;
        if (_attachComponentButton != null) _attachComponentButton.Disabled = !hasSelection;
        if (_setRootButton != null) _setRootButton.Disabled = !hasSelection;
        if (_rootUpButton != null) _rootUpButton.Disabled = _boundRoot == null || _boundRoot.IsRootSlot;
    }

    private void RebuildHierarchyTree()
    {
        try
        {
            if (_hierarchyTree == null) return;

            _hierarchyTree.Clear();
            _treeItemToSlot.Clear();
            _slotToTreeItem.Clear();
            UnsubscribeAllSlots();

            if (_boundRoot == null || _boundRoot.IsDestroyed) return;

            // Subscribe to the root slot
            SubscribeToSlot(_boundRoot);

            var root = _hierarchyTree.CreateItem();
            root.SetText(0, _boundRoot.Name.Value);
            root.SetIcon(0, GetSlotIcon(_boundRoot));
            _treeItemToSlot[root] = _boundRoot;
            _slotToTreeItem[_boundRoot] = root;

            BuildTreeRecursive(root, _boundRoot);

            // Select current slot in tree
            if (Owner.ComponentView.Target != null &&
                _slotToTreeItem.TryGetValue(Owner.ComponentView.Target, out var selectedItem))
            {
                selectedItem.Select(0);
            }
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"SceneInspectorHook: RebuildHierarchyTree exception: {ex.Message}");
        }
    }

    private void BuildTreeRecursive(TreeItem parent, Slot slot, int depth = 0)
    {
        if (depth > 20) return; // Prevent deep recursion

        var childrenSnapshot = slot.Children
            .Where(child => child != null && !child.IsDestroyed)
            .ToList();

        foreach (var child in childrenSnapshot)
        {
            if (child.Name.Value.StartsWith("Gizmo_", StringComparison.Ordinal))
                continue;

            // Listen for changes on every visible slot using per-slot inspector subscriptions.
            SubscribeToSlot(child);

            var item = _hierarchyTree!.CreateItem(parent);
            item.SetText(0, child.Name.Value);
            item.SetIcon(0, GetSlotIcon(child));
            _treeItemToSlot[item] = child;
            _slotToTreeItem[child] = item;

            if (child.ChildCount > 0)
            {
                item.Collapsed = true; // Start collapsed by default
                BuildTreeRecursive(item, child, depth + 1);
            }
        }
    }

    private Texture2D? GetSlotIcon(Slot slot)
    {
        // Could return different icons based on slot state/components
        return null;
    }

    private void RebuildComponentPanel()
    {
        try
        {
            if (_componentContainer == null) return;

            var selected = Owner.ComponentView.Target;
            var selectedId = selected?.ReferenceID.ToString();

            // Only rebuild if selection actually changed
            if (selectedId == _lastBuiltSelectionId)
                return;

            _lastBuiltSelectionId = selectedId;

            // Clear existing - use RemoveChild + Free for immediate cleanup
            // QueueFree defers deletion which causes crashes when new children are added in the same frame
            var children = _componentContainer.GetChildren();
            for (int i = children.Count - 1; i >= 0; i--)
            {
                var child = children[i];
                _componentContainer.RemoveChild(child);
                child.QueueFree();
            }

            if (selected == null || selected.IsDestroyed)
            {
                var noSelectionLabel = new Label();
                noSelectionLabel.Text = "No slot selected";
                _componentContainer.AddChild(noSelectionLabel);
                return;
            }

            // Slot properties section
            AddSlotPropertiesSection(selected);

            // Components section
            AddComponentsSection(selected);

            UpdateLabels();
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"SceneInspectorHook: RebuildComponentPanel exception: {ex.Message}");
            AquaLogger.Error($"SceneInspectorHook: {ex.StackTrace}");
        }
    }

    private void AddSlotPropertiesSection(Slot slot)
    {
        var header = new Label();
        header.Text = "Slot Properties";
        header.AddThemeFontSizeOverride("font_size", 12);
        header.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        _componentContainer!.AddChild(header);

        // Name
        var nameRow = SyncMemberEditorBuilder.CreateEditorRow(slot.Name, "Name");
        if (nameRow != null) _componentContainer.AddChild(nameRow);

        // Active
        var activeRow = SyncMemberEditorBuilder.CreateEditorRow(slot.ActiveSelf, "Active");
        if (activeRow != null) _componentContainer.AddChild(activeRow);

        // Position
        var posRow = SyncMemberEditorBuilder.CreateEditorRow(slot.LocalPosition, "Position");
        if (posRow != null) _componentContainer.AddChild(posRow);

        // Rotation
        var rotRow = SyncMemberEditorBuilder.CreateEditorRow(slot.LocalRotation, "Rotation");
        if (rotRow != null) _componentContainer.AddChild(rotRow);

        // Scale
        var scaleRow = SyncMemberEditorBuilder.CreateEditorRow(slot.LocalScale, "Scale");
        if (scaleRow != null) _componentContainer.AddChild(scaleRow);

        var separator = new HSeparator();
        _componentContainer.AddChild(separator);
    }

    private void AddComponentsSection(Slot slot)
    {
        // Components header with count
        var componentsHeader = new Label();
        componentsHeader.Text = $"Components ({slot.Components.Count})";
        componentsHeader.AddThemeFontSizeOverride("font_size", 12);
        componentsHeader.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        _componentContainer!.AddChild(componentsHeader);

        if (slot.Components.Count == 0)
        {
            var noComponentsLabel = new Label();
            noComponentsLabel.Text = "No components attached";
            noComponentsLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
            _componentContainer.AddChild(noComponentsLabel);
            return;
        }

        var componentsSnapshot = slot.Components.ToList();
        foreach (var component in componentsSnapshot)
        {
            try
            {
                AddComponentEditor(component);
            }
            catch (Exception ex)
            {
                AquaLogger.Log($"SceneInspectorHook: Error adding editor for {component.GetType().Name}: {ex.Message}");
                var errorLabel = new Label();
                errorLabel.Text = $"[Error: {component.GetType().Name}]";
                errorLabel.AddThemeColorOverride("font_color", new Color(1f, 0.3f, 0.3f));
                _componentContainer.AddChild(errorLabel);
            }
        }
    }

    private void AddComponentEditor(Component component)
    {
        // Use component RefID as unique key for tracking expanded state
        var componentKey = component.ReferenceID.ToString();
        var isExpanded = _expandedComponents.Contains(componentKey);

        // Component header with collapse toggle
        var headerBox = new HBoxContainer();
        headerBox.Name = $"ComponentHeader_{component.GetType().Name}";

        var collapseBtn = new Button();
        collapseBtn.Text = isExpanded ? "v" : ">";
        collapseBtn.CustomMinimumSize = new Vector2(24, 24);
        collapseBtn.Flat = true;
        headerBox.AddChild(collapseBtn);

        var nameLabel = new Label();
        nameLabel.Text = component.GetType().Name;
        nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        nameLabel.AddThemeFontSizeOverride("font_size", 13);
        headerBox.AddChild(nameLabel);

        var removeBtn = new Button();
        removeBtn.Text = "X";
        removeBtn.CustomMinimumSize = new Vector2(24, 24);
        removeBtn.Flat = true;
        removeBtn.TooltipText = "Remove component";
        removeBtn.Pressed += () =>
        {
            _expandedComponents.Remove(componentKey);
            component.Slot.RemoveComponent(component);
            _lastBuiltSelectionId = null; // Force rebuild
            RebuildComponentPanel();
        };
        headerBox.AddChild(removeBtn);

        _componentContainer!.AddChild(headerBox);

        // Component properties container
        var propsContainer = new VBoxContainer();
        propsContainer.Name = $"ComponentProps_{component.GetType().Name}";
        propsContainer.Visible = isExpanded; // Use tracked state

        var shownMembers = new HashSet<ISyncMember>();
        int visiblePropertyCount = 0;

        // Use Worker's built-in sync member iteration (works for both field and property patterns)
        for (int i = 0; i < component.SyncMemberCount; i++)
        {
            try
            {
                var syncMember = component.GetSyncMember(i);
                if (syncMember == null) continue;

                var memberName = component.GetSyncMemberName(i);
                var fieldInfo = component.GetSyncMemberFieldInfo(i);

                // Skip base Component/Worker members unless ShowInherited is true
                if (!Owner.ShowInherited.Value)
                {
                    if (IsBaseMemberType(fieldInfo?.DeclaringType))
                        continue;
                }

                var row = SyncMemberEditorBuilder.CreateEditorRow(syncMember, memberName, fieldInfo);
                if (row != null)
                {
                    propsContainer.AddChild(row);
                    shownMembers.Add(syncMember);
                    visiblePropertyCount++;
                }
            }
            catch (Exception ex)
            {
                AquaLogger.Log($"SceneInspectorHook: Error building editor for member {i} on {component.GetType().Name}: {ex.Message}");
            }
        }

        // Fallback for components that use property-backed Sync members not registered in InitInfo.
        visiblePropertyCount += AddFallbackPropertyEditors(component, propsContainer, shownMembers);

        if (visiblePropertyCount == 0)
        {
            var noPropsLabel = new Label();
            noPropsLabel.Text = "No editable properties";
            noPropsLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
            propsContainer.AddChild(noPropsLabel);
        }

        _componentContainer.AddChild(propsContainer);

        // Toggle collapse - track state in HashSet
        collapseBtn.Pressed += () =>
        {
            propsContainer.Visible = !propsContainer.Visible;
            collapseBtn.Text = propsContainer.Visible ? "v" : ">";

            // Update tracked state
            if (propsContainer.Visible)
                _expandedComponents.Add(componentKey);
            else
                _expandedComponents.Remove(componentKey);
        };

        var separator = new HSeparator();
        _componentContainer.AddChild(separator);
    }

    private int AddFallbackPropertyEditors(Component component, VBoxContainer propsContainer, HashSet<ISyncMember> shownMembers)
    {
        int addedRows = 0;
        var componentType = component.GetType();

        var properties = componentType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var property in properties)
        {
            if (!typeof(ISyncMember).IsAssignableFrom(property.PropertyType)) continue;
            if (property.GetMethod == null || property.GetIndexParameters().Length > 0) continue;

            if (!Owner.ShowInherited.Value && IsBaseMemberType(property.DeclaringType))
                continue;

            ISyncMember? syncMember;
            try
            {
                syncMember = property.GetValue(component) as ISyncMember;
            }
            catch
            {
                continue;
            }

            if (syncMember == null || shownMembers.Contains(syncMember)) continue;

            var row = SyncMemberEditorBuilder.CreateEditorRow(syncMember, property.Name);
            if (row == null) continue;

            propsContainer.AddChild(row);
            shownMembers.Add(syncMember);
            addedRows++;
        }

        var fields = componentType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var field in fields)
        {
            if (!typeof(ISyncMember).IsAssignableFrom(field.FieldType)) continue;
            if (field.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)) continue;

            if (!Owner.ShowInherited.Value && IsBaseMemberType(field.DeclaringType))
                continue;

            var syncMember = field.GetValue(component) as ISyncMember;
            if (syncMember == null || shownMembers.Contains(syncMember)) continue;

            var memberName = field.GetCustomAttribute<NameOverrideAttribute>()?.Name;
            if (string.IsNullOrEmpty(memberName))
            {
                memberName = field.Name;
                if (memberName.EndsWith("_Field", StringComparison.Ordinal) && memberName != "_Field")
                {
                    memberName = memberName[..memberName.LastIndexOf("_Field", StringComparison.Ordinal)];
                }
            }

            var row = SyncMemberEditorBuilder.CreateEditorRow(syncMember, memberName!, field);
            if (row == null) continue;

            propsContainer.AddChild(row);
            shownMembers.Add(syncMember);
            addedRows++;
        }

        return addedRows;
    }

    private static bool IsBaseMemberType(Type? declaringType)
    {
        if (declaringType == null) return false;
        if (declaringType == typeof(Worker)) return true;

        return declaringType.IsGenericType &&
               declaringType.GetGenericTypeDefinition() == typeof(ComponentBase<>);
    }

    private void RefreshUI()
    {
        UpdateLabels();
    }

    public override void ApplyChanges()
    {
        try
        {
            // Process any pending actions from tree interactions (deferred to avoid callback conflicts)
            ProcessPendingActions();

            if (_viewport == null) return;

            var resScale = Owner.ResolutionScale.Value;
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

            if (Owner.Root.GetWasChangedAndClear())
            {
                BindRoot(Owner.Root.Target);
            }

            // Check if component view changed
            if (Owner.ComponentView.GetWasChangedAndClear())
            {
                RebuildComponentPanel();

                // Update tree selection
                var currentSelection = Owner.ComponentView.Target;
                if (currentSelection != null && !currentSelection.IsDestroyed &&
                    _slotToTreeItem.TryGetValue(currentSelection, out var item))
                {
                    item.Select(0);
                }
            }

            if (Owner.SplitRatio.GetWasChangedAndClear() && _splitContainer != null)
            {
                _splitContainer.SplitOffset = (int)(Owner.Size.Value.x * Owner.SplitRatio.Value);
            }
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"SceneInspectorHook: ApplyChanges exception: {ex.Message}");
            AquaLogger.Error($"SceneInspectorHook: {ex.StackTrace}");
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
        if (_rootChangedHandler != null) Owner.OnRootChanged -= _rootChangedHandler;
        if (_selectionChangedHandler != null) Owner.OnSelectionChanged -= _selectionChangedHandler;
        Owner.OnAttachComponentRequested -= OnAttachComponentRequested;

        // Unsubscribe from all observed slots
        UnsubscribeAllSlots();

        // Unsubscribe from component add/remove on the observed slot
        if (_observedComponentSlot != null)
        {
            _observedComponentSlot.OnComponentAdded -= OnComponentListChanged;
            _observedComponentSlot.OnComponentRemoved -= OnComponentListChanged;
            _observedComponentSlot = null;
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
        _mainPanel = null;
        _hierarchyTree = null;
        _componentContainer = null;
        _hierarchyScroll = null;
        _componentScroll = null;
        _titleLabel = null;
        _rootLabel = null;
        _splitContainer = null;
        _collisionArea = null;
        _collisionShape = null;
        _boxShape = null;
        _viewport = null;
        _meshInstance = null;
        _material = null;
        _quadMesh = null;
        _lastBuiltSelectionId = null;
        _lastViewportSize = Vector2I.Zero;
        _isDragging = false;
        _treeItemToSlot.Clear();
        _slotToTreeItem.Clear();
        _rootChangedHandler = null;
        _selectionChangedHandler = null;

        base.Destroy(destroyingWorld);
    }
}
