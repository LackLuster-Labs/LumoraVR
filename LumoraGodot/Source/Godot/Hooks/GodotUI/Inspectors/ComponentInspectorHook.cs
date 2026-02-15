using System.Collections.Generic;
using Godot;
using Lumora.Core;
using Lumora.Core.GodotUI.Inspectors;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks.GodotUI.Inspectors;

#nullable enable

/// <summary>
/// Hook for ComponentInspector that builds property editors dynamically.
/// </summary>
public class ComponentInspectorHook : ComponentHook<ComponentInspector>
{
    private VBoxContainer? _propertyContainer;
    private Label? _headerLabel;
    private Button? _removeButton;
    private Component? _lastComponent;
    private readonly List<Control> _propertyRows = new();

    public static IHook<ComponentInspector> Constructor()
    {
        return new ComponentInspectorHook();
    }

    public override void Initialize()
    {
        base.Initialize();
        BuildDefaultUI();
        UpdateUI();
        AquaLogger.Log($"ComponentInspectorHook: Initialized");
    }

    /// <summary>
    /// Build the default UI programmatically if scene doesn't exist.
    /// </summary>
    private void BuildDefaultUI()
    {
        if (attachedNode == null) return;

        // Check if UI was loaded from scene
        _propertyContainer = attachedNode.GetNodeOrNull<VBoxContainer>("%PropertyContainer");
        if (_propertyContainer != null)
        {
            _headerLabel = attachedNode.GetNodeOrNull<Label>("%HeaderLabel");
            _removeButton = attachedNode.GetNodeOrNull<Button>("%RemoveButton");
            return;
        }

        // Create default UI
        var root = new PanelContainer();
        root.Name = "ComponentInspector";
        root.CustomMinimumSize = new Vector2(350, 400);
        attachedNode.AddChild(root);

        var mainVBox = new VBoxContainer();
        mainVBox.Name = "MainVBox";
        root.AddChild(mainVBox);

        // Header
        var headerBox = new HBoxContainer();
        headerBox.Name = "Header";
        mainVBox.AddChild(headerBox);

        _headerLabel = new Label();
        _headerLabel.Name = "HeaderLabel";
        _headerLabel.Text = "Component";
        _headerLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerBox.AddChild(_headerLabel);

        _removeButton = new Button();
        _removeButton.Name = "RemoveButton";
        _removeButton.Text = "X";
        _removeButton.TooltipText = "Remove Component";
        _removeButton.Pressed += OnRemovePressed;
        headerBox.AddChild(_removeButton);

        // Separator
        var sep = new HSeparator();
        mainVBox.AddChild(sep);

        // Scroll container for properties
        var scroll = new ScrollContainer();
        scroll.Name = "ScrollContainer";
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        mainVBox.AddChild(scroll);

        _propertyContainer = new VBoxContainer();
        _propertyContainer.Name = "PropertyContainer";
        _propertyContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_propertyContainer);
    }

    private void OnRemovePressed()
    {
        Owner.RequestRemove();
    }

    public override void ApplyChanges()
    {
        UpdateUI();
    }

    private void UpdateUI()
    {
        var component = Owner.TargetComponent.Target;

        // Check if component changed
        if (component != _lastComponent)
        {
            _lastComponent = component;
            RebuildPropertyEditors();
        }

        // Update header
        if (_headerLabel != null)
        {
            _headerLabel.Text = Owner.GetComponentTypeName();
            _headerLabel.Visible = Owner.ShowHeader.Value;
        }

        // Update remove button visibility
        if (_removeButton != null)
        {
            _removeButton.Visible = Owner.AllowRemove.Value;
        }
    }

    private void RebuildPropertyEditors()
    {
        if (_propertyContainer == null) return;

        // Clear existing rows
        foreach (var row in _propertyRows)
        {
            row.QueueFree();
        }
        _propertyRows.Clear();

        var component = Owner.TargetComponent.Target;
        if (component == null) return;

        // Get all sync members from the component
        int memberCount = component.SyncMemberCount;
        for (int i = 0; i < memberCount; i++)
        {
            var member = component.GetSyncMember(i);
            var name = component.GetSyncMemberName(i);

            if (member == null || string.IsNullOrEmpty(name)) continue;

            // Skip certain internal properties unless showing inherited
            if (!Owner.ShowInherited.Value)
            {
                if (name == "persistent" || name == "UpdateOrder" || name == "Enabled")
                    continue;
            }

            var row = SyncMemberEditorBuilder.CreateEditorRow(member, name);
            if (row != null)
            {
                _propertyContainer.AddChild(row);
                _propertyRows.Add(row);
            }
        }

        AquaLogger.Log($"ComponentInspectorHook: Built {_propertyRows.Count} property editors for {component.GetType().Name}");
    }

    public override void Destroy(bool destroyingWorld)
    {
        _propertyRows.Clear();
        _propertyContainer = null;
        _headerLabel = null;
        _removeButton = null;
        _lastComponent = null;

        base.Destroy(destroyingWorld);
    }
}
