// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Source.Input;
using Godot;
using Lumora.Core;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using Lumora.Godot.UI;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks.GodotUI;

#nullable enable

/// <summary>
/// Godot hook for ContextMenuSystem.
///
/// Bridges the Lumora ContextMenuSystem component to a Godot SubViewport+QuadMesh
/// that renders the radial arc menu near the user's left hand in VR.
///
/// Responsibilities:
///   1. Subscribe to ContextMenuSystem.MenuOpened/MenuClosed/PageChanged events.
///   2. On MenuOpened: build the SubViewport → QuadMesh, load ContextMenu.tscn,
///      position the quad at the user's left hand.
///   3. In ApplyChanges(): poll the menu-toggle button (left secondary / A/X)
///      and call Owner.Toggle().
///   4. Forward item selections / close requests from ContextMenuView → Owner.
/// </summary>
public class ContextMenuHook : ComponentHook<ContextMenuSystem>
{
    // ── Configuration ──────────────────────────────────────────────────────────

    private const string SCENE_PATH = "res://Scenes/UI/ContextMenu/ContextMenu.tscn";
    private const int VIEWPORT_PX = 512;    // SubViewport size in pixels
    private const float QUAD_METERS = 0.30f;  // World-space size of the quad (meters)
    private const float HAND_UP_OFFSET = 0.08f; // Offset above hand anchor (meters)

    // ── Godot nodes ────────────────────────────────────────────────────────────

    private SubViewport? _viewport;
    private MeshInstance3D? _meshInstance;
    private QuadMesh? _quadMesh;
    private StandardMaterial3D? _material;
    private Area3D? _collisionArea;
    private CollisionShape3D? _collisionShape;
    private BoxShape3D? _boxShape;
    private ContextMenuView? _view;

    // ── Button edge-detection ──────────────────────────────────────────────────

    private bool _prevToggleButton = false;
    private bool _prevMiddleButton = false;

    // ── Hook boilerplate ───────────────────────────────────────────────────────

    public static IHook<ContextMenuSystem> Constructor() => new ContextMenuHook();

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    public override void Initialize()
    {
        base.Initialize(); // sets slotHook + attachedNode

        // Subscribe to Lumora-side events
        Owner.MenuOpened += OnMenuOpened;
        Owner.MenuClosed += OnMenuClosed;
        Owner.PageChanged += OnPageChanged;

        LumoraLogger.Log("ContextMenuHook: Initialized.");
    }

    public override void ApplyChanges()
    {
        // ── VR: left secondary (A/X) button ────────────────────────────────────
        bool toggleNow = IInputProvider.LeftSecondaryInput;
        if (toggleNow && !_prevToggleButton)
            Owner.Toggle();
        _prevToggleButton = toggleNow;

        // ── Desktop: middle mouse button ────────────────────────────────────────
        bool middleNow = global::Godot.Input.IsMouseButtonPressed(MouseButton.Middle);
        if (middleNow && !_prevMiddleButton)
            Owner.Toggle();
        _prevMiddleButton = middleNow;
    }

    public override void Destroy(bool destroyingWorld)
    {
        Owner.MenuOpened -= OnMenuOpened;
        Owner.MenuClosed -= OnMenuClosed;
        Owner.PageChanged -= OnPageChanged;

        if (!destroyingWorld)
            DestroyViewport();

        base.Destroy(destroyingWorld);
    }

    // ── ContextMenuSystem event handlers ───────────────────────────────────────

    private void OnMenuOpened(ContextMenuPage page)
    {
        EnsureViewport();
        ShowPage(page);
        PositionAtHand();
    }

    private void OnMenuClosed()
    {
        _view?.AnimateClose();
        if (_meshInstance != null)
            _meshInstance.Visible = false;
        if (_collisionArea != null)
            _collisionArea.Monitorable = false;
    }

    private void OnPageChanged(ContextMenuPage page)
    {
        ShowPage(page);
    }

    // ── Viewport management ────────────────────────────────────────────────────

    /// <summary>
    /// Create the SubViewport → QuadMesh pair. Called once on first MenuOpened.
    /// Mirrors DashboardPanelHook.InitializeVRMode().
    /// </summary>
    private void EnsureViewport()
    {
        if (_viewport != null) return;

        // ── SubViewport ───────────────────────────────────────────────────────
        _viewport = new SubViewport
        {
            Name = "ContextMenuViewport",
            Size = new Vector2I(VIEWPORT_PX, VIEWPORT_PX),
            TransparentBg = true,
            HandleInputLocally = true,
            GuiDisableInput = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible,
            CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Linear,
        };
        attachedNode.AddChild(_viewport);

        // ── Load ContextMenu.tscn into the viewport ───────────────────────────
        var packed = GD.Load<PackedScene>(SCENE_PATH);
        if (packed == null)
        {
            LumoraLogger.Error($"ContextMenuHook: Failed to load '{SCENE_PATH}'");
        }
        else
        {
            _view = packed.Instantiate<ContextMenuView>();
            _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _viewport.AddChild(_view);

            _view.ItemSelected += OnItemSelected;
            _view.CloseRequested += OnCloseRequested;
        }

        // ── Quad mesh ─────────────────────────────────────────────────────────
        _quadMesh = new QuadMesh { Size = new Vector2(QUAD_METERS, QUAD_METERS) };
        _meshInstance = new MeshInstance3D
        {
            Name = "ContextMenuQuad",
            Mesh = _quadMesh,
            Visible = false,
        };

        _material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
            AlbedoTexture = _viewport.GetTexture(),
        };
        _meshInstance.MaterialOverride = _material;
        attachedNode.AddChild(_meshInstance);

        // ── Collision area for laser pointer interaction ────────────────────────
        _collisionArea = new Area3D
        {
            Name = "ContextMenuCollision",
            Monitorable = false,
            Monitoring = false,
            CollisionLayer = 1u << 3, // UI layer (layer 4)
            CollisionMask = 0,
        };
        _boxShape = new BoxShape3D { Size = new Vector3(QUAD_METERS, QUAD_METERS, 0.01f) };
        _collisionShape = new CollisionShape3D { Shape = _boxShape };
        _collisionArea.AddChild(_collisionShape);
        attachedNode.AddChild(_collisionArea);
    }

    private void DestroyViewport()
    {
        if (_view != null)
        {
            _view.ItemSelected -= OnItemSelected;
            _view.CloseRequested -= OnCloseRequested;
        }

        _collisionArea?.QueueFree();
        _viewport?.QueueFree();
        _meshInstance?.QueueFree();
        _material?.Dispose();
        _quadMesh?.Dispose();
        _boxShape?.Dispose();

        _view = null;
        _viewport = null;
        _meshInstance = null;
        _material = null;
        _quadMesh = null;
        _collisionArea = null;
        _collisionShape = null;
        _boxShape = null;
    }

    // ── Page display ───────────────────────────────────────────────────────────

    /// <summary>
    /// Convert a ContextMenuPage into ContextMenuViewData records and hand them
    /// to the view.
    /// </summary>
    private void ShowPage(ContextMenuPage page)
    {
        if (_view == null || _meshInstance == null) return;

        var viewData = new List<ContextMenuViewData>(page.Items.Count);
        foreach (var item in page.Items)
        {
            viewData.Add(new ContextMenuViewData(
                item.Label,
                item.FillColor,
                item.OutlineColor,
                item.LabelColor,
                item.IconPath,
                item.IsEnabled,
                item.IsToggled,
                item.AngleStart,
                item.ArcLength,
                item.RadiusStart,
                item.Thickness
            ));
        }

        _view.SetItems(viewData, page.Title);
        _view.AnimateOpen();
        _meshInstance.Visible = true;
        if (_collisionArea != null)
            _collisionArea.Monitorable = true;
    }

    // ── Hand positioning ───────────────────────────────────────────────────────

    /// <summary>
    /// Position the menu quad near the user's left hand, tilted to face the user.
    /// Mirrors DashboardPanelHook.PositionInFrontOfUser() but uses the hand limb.
    /// </summary>
    private void PositionAtHand()
    {
        // Read left-hand world position and rotation from the input provider
        var handPos = IInputProvider.LimbPosition(IInputProvider.InputLimb.LeftHand);
        var handRot = IInputProvider.LimbRotation(IInputProvider.InputLimb.LeftHand);

        // Place the quad slightly above the hand anchor
        var menuPos = handPos + Vector3.Up * HAND_UP_OFFSET;

        // Face toward the head (yaw only, so the panel stays upright)
        var headPos = IInputProvider.LimbPosition(IInputProvider.InputLimb.Head);
        var lookDir = (headPos - menuPos).Normalized();
        lookDir.Y = 0f;
        if (lookDir.LengthSquared() < 0.001f)
            lookDir = -Vector3.Forward;

        var panelRot = Quaternion.FromEuler(new Vector3(0f, Mathf.Atan2(lookDir.X, lookDir.Z), 0f));

        // Write back through the Lumora slot so the position is networked/serialised
        Owner.Slot.GlobalPosition = new float3(menuPos.X, menuPos.Y, menuPos.Z);
        Owner.Slot.GlobalRotation = new floatQ(panelRot.X, panelRot.Y, panelRot.Z, panelRot.W);
    }

    // ── Item interaction ───────────────────────────────────────────────────────

    private void OnItemSelected(int index)
    {
        var page = Owner.CurrentPage;
        if (page == null || index < 0 || index >= page.Items.Count) return;
        Owner.SelectItem(page.Items[index]);
    }

    private void OnCloseRequested()
    {
        Owner.Close();
    }
}
