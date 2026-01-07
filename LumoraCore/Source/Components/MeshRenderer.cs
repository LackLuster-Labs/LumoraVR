using System;
using Lumora.Core;
using Lumora.Core.Assets;
using AquaLogger = Lumora.Core.Logging.Logger;
using LumoraMaterial = Lumora.Core.Assets.Material;

namespace Lumora.Core.Components;

/// <summary>
/// Renders a 3D mesh with materials.
/// </summary>
[ComponentCategory("Rendering")]
public class MeshRenderer : ImplementableComponent
{
    // ===== SYNC FIELDS (Standard Sync Fields) =====

    /// <summary>
    /// The mesh to render.
    /// </summary>
    public Sync<object> Mesh { get; private set; }

    /// <summary>
    /// Materials for each submesh.
    /// </summary>
    public SyncAssetList<LumoraMaterial> Materials { get; private set; }

    /// <summary>
    /// Material property block overrides for per-instance properties.
    /// </summary>
    public SyncAssetList<MaterialPropertyBlock> MaterialPropertyBlocks { get; private set; }

    /// <summary>
    /// Shadow casting mode (Off, On, ShadowOnly, DoubleSided).
    /// </summary>
    public Sync<ShadowCastMode> ShadowCastMode { get; private set; }

    /// <summary>
    /// Motion vector generation mode for motion blur and temporal effects.
    /// </summary>
    public Sync<MotionVectorMode> MotionVectorMode { get; private set; }

    /// <summary>
    /// Sorting order for transparent rendering (lower values render first).
    /// </summary>
    public Sync<int> SortingOrder { get; private set; }

    // ===== CHANGE TRACKING =====

    /// <summary>
    /// Flag indicating materials list has changed.
    /// </summary>
    public bool MaterialsChanged { get; set; }

    /// <summary>
    /// Flag indicating material property blocks list has changed.
    /// </summary>
    public bool MaterialPropertyBlocksChanged { get; set; }

    // ===== LIFECYCLE =====

    public override void OnAwake()
    {
        base.OnAwake();

        Mesh = new Sync<object>(this, default);
        Materials = new SyncAssetList<LumoraMaterial>(this);
        MaterialPropertyBlocks = new SyncAssetList<MaterialPropertyBlock>(this);
        ShadowCastMode = new Sync<ShadowCastMode>(this, Components.ShadowCastMode.On);
        MotionVectorMode = new Sync<MotionVectorMode>(this, Components.MotionVectorMode.Object);
        SortingOrder = new Sync<int>(this, 0);

        Materials.OnChanged += Materials_Changed;
        MaterialPropertyBlocks.OnChanged += MaterialPropertyBlocks_Changed;

        AquaLogger.Log($"MeshRenderer: Awake on slot '{Slot.SlotName.Value}'");
    }

    /// <summary>
    /// Handle materials list changes.
    /// </summary>
    private void Materials_Changed(SyncAssetList<LumoraMaterial> list)
    {
        MaterialsChanged = true;
    }

    /// <summary>
    /// Handle material property blocks list changes.
    /// </summary>
    private void MaterialPropertyBlocks_Changed(SyncAssetList<MaterialPropertyBlock> list)
    {
        MaterialPropertyBlocksChanged = true;
    }

    public override void OnStart()
    {
        base.OnStart();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        MaterialsChanged = false;
        MaterialPropertyBlocksChanged = false;
    }

    public override void OnDestroy()
    {
        // Unsubscribe from events
        Materials.OnChanged -= Materials_Changed;
        MaterialPropertyBlocks.OnChanged -= MaterialPropertyBlocks_Changed;

        base.OnDestroy();
        AquaLogger.Log($"MeshRenderer: Destroyed on slot '{Slot?.SlotName.Value}'");
    }

    // ===== PUBLIC API (Standard Public API) =====

    /// <summary>
    /// Convenience accessor for single-material meshes.
    /// Gets or sets Materials[0].
    /// </summary>
    public AssetRef<LumoraMaterial> Material
    {
        get
        {
            if (Materials.Count == 0)
            {
                return Materials.Add();
            }
            return Materials.GetElement(0);
        }
    }

    /// <summary>
    /// Check if all assets (mesh + materials) are loaded.
    /// </summary>
    public bool IsLoaded
    {
        get
        {
            // Check all materials are loaded
            foreach (IAssetProvider<LumoraMaterial> material in Materials)
            {
                if (material != null && !material.IsAssetAvailable)
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Replace all materials in the list with a single material.
    /// </summary>
    /// <param name="material">The material provider to use for all submeshes</param>
    public void ReplaceAllMaterials(IAssetProvider<LumoraMaterial> material)
    {
        for (int i = 0; i < Materials.Count; i++)
        {
            Materials[i] = material;
        }
    }
}

/// <summary>
/// Shadow casting modes for MeshRenderer.
/// </summary>
public enum ShadowCastMode
{
    /// <summary>No shadow casting</summary>
    Off = 0,

    /// <summary>Standard shadow casting (default)</summary>
    On = 1,

    /// <summary>Only render shadows, not the mesh itself (useful for invisible shadow casters)</summary>
    ShadowOnly = 2,

    /// <summary>Cast shadows from both front and back faces</summary>
    DoubleSided = 3
}

/// <summary>
/// Motion vector generation modes for motion blur and temporal effects.
/// </summary>
public enum MotionVectorMode
{
    /// <summary>Motion vectors based on camera movement only</summary>
    Camera = 0,

    /// <summary>Motion vectors based on object movement (default for dynamic objects)</summary>
    Object = 1,

    /// <summary>No motion vectors generated (for static objects)</summary>
    NoMotion = 2
}
