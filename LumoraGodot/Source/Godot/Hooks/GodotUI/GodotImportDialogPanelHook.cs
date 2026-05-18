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
/// Godot hook for an in-world import dialog panel.
/// </summary>
public sealed class GodotImportDialogPanelHook : ComponentHook<GodotImportDialogPanel>
{
    private SubViewport? _viewport;
    private MeshInstance3D? _meshInstance;
    private QuadMesh? _quadMesh;
    private StandardMaterial3D? _material;
    private Node? _loadedScene;
    private Vector2I _lastViewportSize = Vector2I.Zero;

    private Area3D? _collisionArea;
    private CollisionShape3D? _collisionShape;
    private BoxShape3D? _boxShape;

    private ImportDialog? _dialog;
    private string _lastConfiguredFilePath = string.Empty;
    private Slot? _lastConfiguredTarget;
    private object? _lastConfiguredLocalDb;
    private bool _lastConfiguredHasSpawn;
    private float3 _lastConfiguredSpawn;

    public static IHook<GodotImportDialogPanel> Constructor()
    {
        return new GodotImportDialogPanelHook();
    }

    public override void Initialize()
    {
        base.Initialize();

        var resScale = UIReadability.GetReadableResolutionScale(Owner.ResolutionScale.Value);
        _viewport = new SubViewport
        {
            Name = "ImportDialogViewport",
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

        _meshInstance = new MeshInstance3D { Name = "ImportDialogQuad" };
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
        ConfigureDialog(force: true);
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
            ConfigureDialog(force: true);
            return;
        }

        var changed = Owner.FilePath.GetWasChangedAndClear() ||
                      Owner.TargetSlot.GetWasChangedAndClear() ||
                      Owner.HasImportSpawnPosition.GetWasChangedAndClear() ||
                      Owner.ImportSpawnPosition.GetWasChangedAndClear();
        if (changed)
        {
            ConfigureDialog(force: true);
            return;
        }

        ConfigureDialog(force: false);
    }

    private void LoadScene()
    {
        var scenePath = Owner.ScenePath.Value;
        if (string.IsNullOrEmpty(scenePath))
        {
            LumoraLogger.Warn("GodotImportDialogPanelHook: No scene path specified");
            return;
        }

        UnbindDialog();

        if (_loadedScene != null && GodotObject.IsInstanceValid(_loadedScene))
        {
            _loadedScene.QueueFree();
            _loadedScene = null;
        }

        var packedScene = GD.Load<PackedScene>(scenePath);
        if (packedScene == null)
        {
            LumoraLogger.Warn($"GodotImportDialogPanelHook: Failed to load scene '{scenePath}'");
            return;
        }

        _loadedScene = packedScene.Instantiate();
        if (_loadedScene == null)
        {
            LumoraLogger.Warn("GodotImportDialogPanelHook: Failed to instantiate scene");
            return;
        }

        _viewport?.AddChild(_loadedScene);

        if (_loadedScene is Control control)
        {
            control.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            UIReadability.ApplyToTree(control);
        }

        _dialog = _loadedScene as ImportDialog ?? _loadedScene.GetNodeOrNull<ImportDialog>("ImportDialog");
        if (_dialog == null)
        {
            LumoraLogger.Warn("GodotImportDialogPanelHook: Loaded scene is missing ImportDialog script root");
            return;
        }

        _dialog.DialogClosed += OnDialogClosed;
    }

    private void ConfigureDialog(bool force)
    {
        if (_dialog == null)
        {
            return;
        }

        var filePath = Owner.FilePath.Value ?? string.Empty;
        var targetSlot = Owner.TargetSlot.Target;
        var localDb = Owner.LocalDB;
        var hasSpawn = Owner.HasImportSpawnPosition.Value;
        var spawnPosition = Owner.ImportSpawnPosition.Value;
        if (string.IsNullOrWhiteSpace(filePath) || targetSlot == null)
        {
            return;
        }

        if (!force &&
            string.Equals(_lastConfiguredFilePath, filePath, StringComparison.Ordinal) &&
            ReferenceEquals(_lastConfiguredTarget, targetSlot) &&
            ReferenceEquals(_lastConfiguredLocalDb, localDb) &&
            _lastConfiguredHasSpawn == hasSpawn &&
            _lastConfiguredSpawn == spawnPosition)
        {
            return;
        }

        _lastConfiguredFilePath = filePath;
        _lastConfiguredTarget = targetSlot;
        _lastConfiguredLocalDb = localDb;
        _lastConfiguredHasSpawn = hasSpawn;
        _lastConfiguredSpawn = spawnPosition;
        _dialog.ShowForFile(filePath, targetSlot, localDb, hasSpawn ? spawnPosition : null);
    }

    private void OnDialogClosed()
    {
        if (Owner.AutoDestroyOnClose.Value)
        {
            Owner.Close();
        }
    }

    private void UnbindDialog()
    {
        if (_dialog != null && GodotObject.IsInstanceValid(_dialog))
        {
            _dialog.DialogClosed -= OnDialogClosed;
        }

        _dialog = null;
    }

    private void CreateCollisionArea()
    {
        _collisionArea = new Area3D
        {
            Name = "ImportDialogCollision",
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
        UnbindDialog();

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
        _collisionArea = null;
        _collisionShape = null;
        _boxShape = null;
        _viewport = null;
        _meshInstance = null;
        _material = null;
        _quadMesh = null;
        _lastViewportSize = Vector2I.Zero;
        _lastConfiguredFilePath = string.Empty;
        _lastConfiguredTarget = null;
        _lastConfiguredLocalDb = null;
        _lastConfiguredHasSpawn = false;
        _lastConfiguredSpawn = float3.Zero;

        base.Destroy(destroyingWorld);
    }
}
