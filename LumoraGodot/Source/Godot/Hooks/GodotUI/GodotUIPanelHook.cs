// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Godot;
using Lumora.Core;
using Lumora.Core.GodotUI;
using Lumora.Core.GodotUI.Wizards;
using Lumora.Core.Math;
using Lumora.Godot.UI;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks.GodotUI;

#nullable enable

/// <summary>
/// Universal Godot hook for UI panels that render to a 3D quad.
/// Parses loaded .tscn scenes and creates Lumora components for each UI element.
/// Includes collision shape for laser pointer interaction.
/// </summary>
public class GodotUIPanelHook : ComponentHook<GodotUIPanel>
{
    private SubViewport? _viewport;
    private MeshInstance3D? _meshInstance;
    private QuadMesh? _quadMesh;
    private StandardMaterial3D? _material;
    private Node? _loadedScene;

    // Collision for touch interaction
    private Area3D? _collisionArea;
    private CollisionShape3D? _collisionShape;
    private BoxShape3D? _boxShape;
    private Vector2I _lastViewportSize = Vector2I.Zero;

    // Registry of scene node paths to their Control instances
    private readonly Dictionary<string, Control> _nodeRegistry = new();
    private string? _lastUserListSignature;

    // Static registry for hooks (so element hooks can find their nodes)
    private static readonly Dictionary<GodotUIPanel, GodotUIPanelHook> _panelHooks = new();

    // Secondary registry for non-GodotUIPanelHook panels (e.g. SceneInspectorHook)
    private static readonly Dictionary<GodotUIPanel, Func<string, Control?>> _customNodeProviders = new();

    public static void RegisterNodeProvider(GodotUIPanel panel, Func<string, Control?> provider)
        => _customNodeProviders[panel] = provider;

    public static void UnregisterNodeProvider(GodotUIPanel panel)
        => _customNodeProviders.Remove(panel);

    public static Control? FindNodeInCustomProvider(GodotUIPanel panel, string path)
        => _customNodeProviders.TryGetValue(panel, out var p) ? p(path) : null;

    public static IHook<GodotUIPanel> Constructor()
    {
        return new GodotUIPanelHook();
    }

    /// <summary>
    /// Get a Control node by path from this panel's scene.
    /// </summary>
    public Control? GetNodeByPath(string path)
    {
        return _nodeRegistry.TryGetValue(path, out var node) ? node : null;
    }

    /// <summary>
    /// Find the panel hook for a given panel component.
    /// </summary>
    public static GodotUIPanelHook? FindHookForPanel(GodotUIPanel? panel)
    {
        if (panel == null) return null;
        return _panelHooks.TryGetValue(panel, out var hook) ? hook : null;
    }

    public override void Initialize()
    {
        base.Initialize();

        _panelHooks[Owner] = this;

        var resScale = UIReadability.GetReadableResolutionScale(Owner.ResolutionScale.Value);

        // Create SubViewport
        _viewport = new SubViewport();
        _viewport.Name = "UIPanelViewport";
        _viewport.Size = new Vector2I(
            (int)(Owner.Size.Value.x * resScale),
            (int)(Owner.Size.Value.y * resScale));
        _lastViewportSize = _viewport.Size;
        _viewport.TransparentBg = true;
        _viewport.HandleInputLocally = true;
        _viewport.GuiDisableInput = false;
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible;
        _viewport.CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Linear;
        _viewport.Msaa2D = Viewport.Msaa.Disabled;

        // Create mesh for 3D display
        _meshInstance = new MeshInstance3D();
        _meshInstance.Name = "UIPanelQuad";

        _quadMesh = new QuadMesh();
        UpdateQuadSize();
        _meshInstance.Mesh = _quadMesh;

        // Create material
        _material = new StandardMaterial3D();
        _material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        _material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        _material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        _material.TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear;
        _material.AlbedoTexture = _viewport.GetTexture();
        _meshInstance.MaterialOverride = _material;

        attachedNode.AddChild(_viewport);
        attachedNode.AddChild(_meshInstance);

        // Create collision area for touch interaction
        CreateCollisionArea();

        LoadScene();

        Owner.OnDataRefresh += RefreshUIData;
        if (Owner is GodotUserInspector userInspector)
        {
            userInspector.OnUserListChanged += RefreshUserInspector;
            userInspector.OnUserSelectionChanged += OnUserSelectionChanged;
        }

        LumoraLogger.Log($"GodotUIPanelHook: Initialized for {Owner.GetType().Name}");
    }

    private void LoadScene()
    {
        var scenePath = Owner.ScenePath.Value;
        if (string.IsNullOrEmpty(scenePath))
        {
            LumoraLogger.Warn("GodotUIPanelHook: No scene path specified");
            return;
        }

        var packedScene = GD.Load<PackedScene>(scenePath);
        if (packedScene == null)
        {
            LumoraLogger.Warn($"GodotUIPanelHook: Failed to load scene '{scenePath}'");
            return;
        }

        _loadedScene = packedScene.Instantiate();
        if (_loadedScene == null)
        {
            LumoraLogger.Warn("GodotUIPanelHook: Failed to instantiate scene");
            return;
        }

        _viewport?.AddChild(_loadedScene);

        if (_loadedScene is Control loadedControl)
        {
            loadedControl.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            UIReadability.ApplyToTree(loadedControl);
        }

        // Build node registry and create Lumora components
        // Start from children of root (skip root node name in paths)
        _nodeRegistry.Clear();
        foreach (var child in _loadedScene.GetChildren())
        {
            ParseSceneNode(child, "", Owner.Slot);
        }

        // Notify component
        Owner.NotifySceneLoaded(new List<string>(_nodeRegistry.Keys));

        RefreshUIData();
        ResetScrollPositions();

        LumoraLogger.Log($"GodotUIPanelHook: Scene loaded, created {_nodeRegistry.Count} UI element components");
    }

    private void ParseSceneNode(Node node, string parentPath, Slot parentSlot)
    {
        var nodeName = node.Name.ToString();
        var nodePath = string.IsNullOrEmpty(parentPath) ? nodeName : $"{parentPath}/{nodeName}";

        // Skip non-Control nodes
        if (node is not Control control)
        {
            // Still recurse into children
            foreach (var child in node.GetChildren())
            {
                ParseSceneNode(child, nodePath, parentSlot);
            }
            return;
        }

        // Register this control
        _nodeRegistry[nodePath] = control;

        // Create child slot for this UI element
        var elementSlot = parentSlot.AddSlot(nodeName);

        // Create appropriate component based on Control type
        GodotUIElement? element = null;

        if (control is Label)
        {
            element = elementSlot.AttachComponent<GodotLabel>();
        }
        else if (control is Button)
        {
            element = elementSlot.AttachComponent<GodotButton>();
            // Wire up button press
            var btn = (Button)control;
            var capturedPath = nodePath;
            btn.Pressed += () => Owner.HandleButtonPress(capturedPath);
        }
        else if (control is PanelContainer or Panel)
        {
            element = elementSlot.AttachComponent<GodotPanel>();
        }
        else if (control is ScrollContainer)
        {
            element = elementSlot.AttachComponent<GodotScrollContainer>();
        }
        else
        {
            // Generic UI element for other control types (VBox, HBox, etc.)
            element = elementSlot.AttachComponent<GodotUIElement>();
        }

        if (element != null)
        {
            // Set node path so hook can find the existing Godot node
            element.SceneNodePath.Value = nodePath;
            element.ParentPanel.Target = Owner;

            // Copy initial properties from Godot node to component
            SyncPropertiesFromNode(element, control);
        }

        // Recurse into children
        foreach (var child in node.GetChildren())
        {
            ParseSceneNode(child, nodePath, elementSlot);
        }
    }

    private void SyncPropertiesFromNode(GodotUIElement element, Control control)
    {
        // Sync visibility
        element.Visible.Value = control.Visible;

        // Sync modulate
        var mod = control.Modulate;
        element.Modulate.Value = new Lumora.Core.Math.color(mod.R, mod.G, mod.B, mod.A);

        // Sync min size
        var minSize = control.CustomMinimumSize;
        element.MinSize.Value = new Lumora.Core.Math.float2(minSize.X, minSize.Y);

        // Sync size flags
        element.SizeFlagsHorizontal.Value = (int)control.SizeFlagsHorizontal;
        element.SizeFlagsVertical.Value = (int)control.SizeFlagsVertical;

        // Sync specific properties for label
        if (element is GodotLabel label && control is Label godotLabel)
        {
            label.Text.Value = godotLabel.Text;
            // Font size would need theme lookup, skip for now
        }

        // Sync specific properties for button
        if (element is GodotButton button && control is Button godotButton)
        {
            button.Text.Value = godotButton.Text;
        }
    }

    private void ResetScrollPositions()
    {
        foreach (var (_, control) in _nodeRegistry)
        {
            if (control is ScrollContainer scroll)
            {
                scroll.ScrollVertical = 0;
                scroll.ScrollHorizontal = 0;
            }
        }
    }

    private void RefreshUIData()
    {
        if (_loadedScene == null) return;

        var data = Owner.GetUIData();
        var colors = Owner.GetUIColors();

        foreach (var (path, value) in data)
        {
            if (_nodeRegistry.TryGetValue(path, out var control) && control is Label label)
            {
                label.Text = value;
                AlignMetricValueLabel(path, label);
            }
        }

        foreach (var (path, coreColor) in colors)
        {
            if (_nodeRegistry.TryGetValue(path, out var control) && control is Label label)
            {
                label.AddThemeColorOverride("font_color", new Color(coreColor.r, coreColor.g, coreColor.b, coreColor.a));
            }
        }

        if (Owner is GodotUserInspector userInspector)
        {
            RefreshUserList(userInspector);
        }
    }

    private static void AlignMetricValueLabel(string path, Label label)
    {
        if (!path.EndsWith("/Value", StringComparison.Ordinal) &&
            !path.EndsWith("/Count", StringComparison.Ordinal))
        {
            return;
        }

        if (IsMetricValuePath(path))
        {
            label.HorizontalAlignment = global::Godot.HorizontalAlignment.Right;
            label.CustomMinimumSize = new Vector2(Mathf.Max(label.CustomMinimumSize.X, 112f), label.CustomMinimumSize.Y);
            return;
        }

        label.HorizontalAlignment = global::Godot.HorizontalAlignment.Left;
    }

    private static bool IsMetricValuePath(string path)
    {
        if (path.EndsWith("/Count", StringComparison.Ordinal))
        {
            return true;
        }

        return path.Contains("/Performance/", StringComparison.Ordinal) ||
               path.Contains("/Memory/", StringComparison.Ordinal) ||
               path.Contains("/WorldStats/", StringComparison.Ordinal);
    }

    private void RefreshUserInspector()
    {
        _lastUserListSignature = null;
        RefreshUIData();
    }

    private void OnUserSelectionChanged(User? _)
    {
        _lastUserListSignature = null;
        World?.RunInUpdates(1, RefreshUIData);
    }

    private void RefreshUserList(GodotUserInspector userInspector)
    {
        if (_nodeRegistry.TryGetValue("MainPanel/VBox/Content/LeftPanel/VBox/UserList/UserListContent", out var control) &&
            control is VBoxContainer list)
        {
            var users = userInspector.GetUsers();
            var selected = userInspector.SelectedUser.Target;
            var signature = $"{selected?.ReferenceID.ToString() ?? "none"}|";

            foreach (var user in users)
            {
                signature += $"{user.ReferenceID}:{user.UserName.Value}:{user.IsLocal}:{user.PresentInWorld.Value};";
            }

            if (_lastUserListSignature == signature)
            {
                return;
            }

            _lastUserListSignature = signature;
            foreach (var child in list.GetChildren())
            {
                child.QueueFree();
            }

            if (users.Count == 0)
            {
                var empty = new Label
                {
                    Text = "No users",
                    HorizontalAlignment = global::Godot.HorizontalAlignment.Center,
                    CustomMinimumSize = new Vector2(0, 32)
                };
                empty.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.82f));
                empty.AddThemeFontSizeOverride("font_size", 14);
                list.AddChild(empty);
                return;
            }

            foreach (var user in users)
            {
                var isSelected = user == selected;
                var isLocal = user.IsLocal;
                var isPresent = user.PresentInWorld.Value;
                var label = isLocal
                    ? $"ME  {user.UserName.Value ?? "Unknown"}"
                    : $"{(isPresent ? "ON" : "OFF")}  {user.UserName.Value ?? "Unknown"}";

                var button = new Button
                {
                    Text = label,
                    CustomMinimumSize = new Vector2(0, 34),
                    Alignment = global::Godot.HorizontalAlignment.Left,
                    TooltipText = $"{user.UserName.Value ?? "Unknown"} ({user.ReferenceID})"
                };

                var bg = isSelected
                    ? new Color(0.18f, 0.35f, 0.5f, 0.95f)
                    : isLocal
                        ? new Color(0.08f, 0.24f, 0.28f, 0.9f)
                        : isPresent
                            ? new Color(0.1f, 0.18f, 0.12f, 0.85f)
                            : new Color(0.2f, 0.09f, 0.09f, 0.75f);
                var border = isSelected || isLocal
                    ? new Color(0.38f, 0.85f, 1f, 1f)
                    : isPresent
                        ? new Color(0.36f, 0.8f, 0.45f, 0.85f)
                        : new Color(0.95f, 0.36f, 0.36f, 0.8f);

                button.AddThemeStyleboxOverride("normal", MakeUserButtonStyle(bg, border));
                button.AddThemeStyleboxOverride("hover", MakeUserButtonStyle(bg.Lightened(0.12f), border));
                button.AddThemeStyleboxOverride("pressed", MakeUserButtonStyle(bg.Darkened(0.08f), border));
                button.AddThemeColorOverride("font_color", isLocal ? new Color(0.55f, 0.95f, 1f) : new Color(0.92f, 0.94f, 0.98f));
                button.AddThemeFontSizeOverride("font_size", 14);

                var capturedUser = user;
                button.Pressed += () =>
                {
                    userInspector.SelectUser(capturedUser);
                    World?.RunInUpdates(1, RefreshUIData);
                };

                list.AddChild(button);
            }
        }
    }

    private static StyleBoxFlat MakeUserButtonStyle(Color bg, Color border)
    {
        return new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
            CornerRadiusBottomRight = 5,
            CornerRadiusBottomLeft = 5,
            ContentMarginLeft = 8,
            ContentMarginRight = 8
        };
    }

    private void UpdateQuadSize()
    {
        if (_quadMesh == null) return;

        var size = Owner.Size.Value;
        var ppu = Owner.PixelsPerUnit.Value;

        _quadMesh.Size = new Vector2(size.x / ppu, size.y / ppu);

        // Update collision shape to match
        UpdateCollisionSize();
    }

    private void CreateCollisionArea()
    {
        // Create Area3D for collision detection
        _collisionArea = new Area3D();
        _collisionArea.Name = "UIPanelCollision";
        _collisionArea.Monitorable = true;
        _collisionArea.Monitoring = false;

        // Set collision layer to UI layer (layer 4 = bit 3)
        _collisionArea.CollisionLayer = 1u << 3;
        _collisionArea.CollisionMask = 0;

        // Create collision shape
        _collisionShape = new CollisionShape3D();
        _boxShape = new BoxShape3D();
        UpdateCollisionSize();
        _collisionShape.Shape = _boxShape;

        _collisionArea.AddChild(_collisionShape);
        attachedNode.AddChild(_collisionArea);

        LumoraLogger.Log($"GodotUIPanelHook: Created collision area for touch interaction");
    }

    private void UpdateCollisionSize()
    {
        if (_boxShape == null) return;

        var size = Owner.Size.Value;
        var ppu = Owner.PixelsPerUnit.Value;

        // Box shape is same size as quad, but very thin
        _boxShape.Size = new Vector3(size.x / ppu, size.y / ppu, 0.01f);
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
    }

    private void UnloadScene()
    {
        _nodeRegistry.Clear();
        _loadedScene?.QueueFree();
        _loadedScene = null;
    }

    public override void Destroy(bool destroyingWorld)
    {
        _panelHooks.Remove(Owner);
        Owner.OnDataRefresh -= RefreshUIData;
        if (Owner is GodotUserInspector userInspector)
        {
            userInspector.OnUserListChanged -= RefreshUserInspector;
            userInspector.OnUserSelectionChanged -= OnUserSelectionChanged;
        }

        if (!destroyingWorld)
        {
            UnloadScene();
            _collisionArea?.QueueFree();
            _viewport?.QueueFree();
            _meshInstance?.QueueFree();
            _material?.Dispose();
            _quadMesh?.Dispose();
            _boxShape?.Dispose();
        }

        _collisionArea = null;
        _collisionShape = null;
        _boxShape = null;
        _viewport = null;
        _meshInstance = null;
        _material = null;
        _quadMesh = null;
        _lastViewportSize = Vector2I.Zero;

        base.Destroy(destroyingWorld);
    }
}
