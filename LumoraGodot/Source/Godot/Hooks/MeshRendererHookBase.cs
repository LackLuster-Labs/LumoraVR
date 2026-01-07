using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Assets;
using System.Collections.Generic;
using AquaLogger = Lumora.Core.Logging.Logger;
using LumoraMaterial = Lumora.Core.Assets.Material;
using LumoraMaterialPropertyBlock = Lumora.Core.Assets.MaterialPropertyBlock;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// Abstract base class for MeshRenderer hooks.
/// Handles mesh asset integration, materials, property blocks, and renderer properties.
/// </summary>
public abstract class MeshRendererHookBase<T, U> : ComponentHook<T>
    where T : MeshRenderer
    where U : Node3D
{
    protected struct RendererData
    {
        public Node3D gameObject;
        public MeshInstance3D meshInstance;
        public U meshRenderer;
    }

    private bool usesMaterialPropertyBlocks;
    private global::Godot.Material[] godotMaterials;
    private MeshInstance3D meshInstance;

    protected abstract bool UseMeshInstance { get; }

    protected bool meshWasChanged { get; private set; }
    protected int materialCount { get; private set; }

    public U MeshRenderer { get; protected set; }

    protected abstract void AssignMesh(U renderer, Mesh mesh);

    protected virtual void OnAttachRenderer()
    {
    }

    protected virtual void OnCleanupRenderer()
    {
    }

    public override void ApplyChanges()
    {
        // Only proceed if mesh asset is available
        if (Owner.Mesh.Value != null && IsMeshAssetAvailable())
        {
            if (MeshRenderer == null)
            {
                // Create separate Node3D container
                var gameObject = new Node3D();
                gameObject.Name = "MeshRenderer";
                attachedNode.AddChild(gameObject);

                // Add MeshInstance3D as child
                if (UseMeshInstance)
                {
                    meshInstance = new MeshInstance3D();
                    gameObject.AddChild(meshInstance);
                }

                MeshRenderer = gameObject as U;
                OnAttachRenderer();
            }

            // Check if mesh changed and update
            meshWasChanged = Owner.Mesh.GetWasChangedAndClear();
            if (meshWasChanged)
            {
                var godotMesh = GetGodotMeshFromAsset();
                if (UseMeshInstance && meshInstance != null)
                {
                    meshInstance.Mesh = godotMesh;
                }
                else
                {
                    AssignMesh(MeshRenderer, godotMesh);
                }
            }

            // Handle material changes
            bool materialArrayChanged = false;
            if (Owner.MaterialsChanged || meshWasChanged)
            {
                Owner.MaterialsChanged = false;
                materialArrayChanged = true;
                materialCount = 1;

                // Get default materials
                global::Godot.Material defaultMaterial = GetDefaultMaterial();

                if (Owner.Materials.Count > 1 || godotMaterials != null)
                {
                    // Multiple materials
                    godotMaterials = ResizeArray(godotMaterials, Owner.Materials.Count);
                    for (int i = 0; i < godotMaterials.Length; i++)
                    {
                        godotMaterials[i] = GetGodotMaterialFromAsset(Owner.Materials[i], defaultMaterial);
                    }

                    if (meshInstance != null)
                    {
                        // Set all materials on mesh instance
                        for (int i = 0; i < godotMaterials.Length; i++)
                        {
                            meshInstance.SetSurfaceOverrideMaterial(i, godotMaterials[i]);
                        }
                    }
                    materialCount = godotMaterials.Length;
                }
                else if (Owner.Materials.Count == 1)
                {
                    // Single material
                    var material = GetGodotMaterialFromAsset(Owner.Materials[0], defaultMaterial);
                    if (meshInstance != null)
                    {
                        meshInstance.MaterialOverride = material;
                    }
                }
                else
                {
                    // No materials - use default
                    if (meshInstance != null)
                    {
                        meshInstance.MaterialOverride = defaultMaterial;
                    }
                }
            }

            // Handle material property blocks
            if (Owner.MaterialPropertyBlocksChanged || materialArrayChanged)
            {
                Owner.MaterialPropertyBlocksChanged = false;
                if (Owner.MaterialPropertyBlocks.Count > 0)
                {
                    // Apply property blocks to materials
                    for (int j = 0; j < materialCount; j++)
                    {
                        if (j < Owner.MaterialPropertyBlocks.Count)
                        {
                            ApplyMaterialPropertyBlock(GetGodotMaterialPropertyBlockFromAsset(Owner.MaterialPropertyBlocks[j]), j);
                        }
                        else
                        {
                            ApplyMaterialPropertyBlock(null, j);
                        }
                    }
                    usesMaterialPropertyBlocks = true;
                }
                else if (usesMaterialPropertyBlocks)
                {
                    // Clear property blocks
                    for (int k = 0; k < materialCount; k++)
                    {
                        ApplyMaterialPropertyBlock(null, k);
                    }
                    usesMaterialPropertyBlocks = false;
                }
            }

            // Handle renderer properties
            bool enabled = Owner.Enabled;
            if (meshInstance != null && meshInstance.Visible != enabled)
            {
                meshInstance.Visible = enabled;
            }

            if (Owner.SortingOrder.GetWasChangedAndClear())
            {
                ApplySortingOrder(Owner.SortingOrder.Value);
            }

            if (Owner.ShadowCastMode.GetWasChangedAndClear())
            {
                ApplyShadowCastMode(Owner.ShadowCastMode.Value);
            }

            if (Owner.MotionVectorMode.GetWasChangedAndClear())
            {
                ApplyMotionVectorMode(Owner.MotionVectorMode.Value);
            }
        }
        else
        {
            CleanupRenderer(destroyingWorld: false);
        }
    }

    private void CleanupRenderer(bool destroyingWorld)
    {
        if (!destroyingWorld && MeshRenderer != null && GodotObject.IsInstanceValid(MeshRenderer))
        {
            ((Node)MeshRenderer).QueueFree();
        }
        OnCleanupRenderer();
    }

    public override void Destroy(bool destroyingWorld)
    {
        CleanupRenderer(destroyingWorld);
        godotMaterials = null;
        meshInstance = null;
        MeshRenderer = default(U);
        base.Destroy(destroyingWorld);
    }

    // Abstract/virtual methods for derived classes to implement

    protected virtual bool IsMeshAssetAvailable()
    {
        // TODO: Implement proper asset availability check when asset system is ready
        return Owner.Mesh.Value != null;
    }

    protected virtual Mesh GetGodotMeshFromAsset()
    {
        // TODO: Implement proper mesh asset conversion when asset system is ready
        // For now, return a placeholder
        AquaLogger.Warn("MeshRendererHookBase: GetGodotMeshFromAsset not implemented - using placeholder");
        return new BoxMesh(); // Temporary placeholder
    }

    protected virtual global::Godot.Material GetDefaultMaterial()
    {
        // Create basic material
        var material = new StandardMaterial3D();
        material.AlbedoColor = new Color(0.8f, 0.8f, 0.8f);
        material.Roughness = 0.7f;
        return material;
    }

    protected virtual void ApplyMaterialPropertyBlock(object propertyBlock, int materialIndex)
    {
        // TODO: Implement material property block application when system is ready
        AquaLogger.Debug($"MeshRendererHookBase: ApplyMaterialPropertyBlock not implemented for index {materialIndex}");
    }

    protected virtual void ApplySortingOrder(int sortingOrder)
    {
        // TODO: Map to Godot's sorting layers when available
        AquaLogger.Debug($"MeshRendererHookBase: ApplySortingOrder({sortingOrder}) not implemented");
    }

    protected virtual void ApplyShadowCastMode(ShadowCastMode shadowCastMode)
    {
        if (meshInstance != null)
        {
            switch (shadowCastMode)
            {
                case ShadowCastMode.Off:
                    meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                    break;
                case ShadowCastMode.On:
                    meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
                    break;
                case ShadowCastMode.ShadowOnly:
                    meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
                    break;
                case ShadowCastMode.DoubleSided:
                    meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.DoubleSided;
                    break;
            }
        }
    }

    protected virtual void ApplyMotionVectorMode(MotionVectorMode motionVectorMode)
    {
        // TODO: Map to Godot's motion vector settings when available
        AquaLogger.Debug($"MeshRendererHookBase: ApplyMotionVectorMode({motionVectorMode}) not implemented");
    }

    // Helper methods

    private global::Godot.Material[] ResizeArray(global::Godot.Material[] array, int newSize)
    {
        if (array == null || array.Length != newSize)
        {
            return new global::Godot.Material[newSize];
        }
        return array;
    }

    private global::Godot.Material GetGodotMaterialFromAsset(IAssetProvider<LumoraMaterial> materialProvider, global::Godot.Material defaultMaterial)
    {
        // TODO: Implement proper material asset conversion when asset system is ready
        if (materialProvider?.Asset != null)
        {
            // For now, return default material until asset system is implemented
            AquaLogger.Debug("MeshRendererHookBase: GetGodotMaterialFromAsset not implemented - using default material");
        }
        return defaultMaterial;
    }

    private object GetGodotMaterialPropertyBlockFromAsset(IAssetProvider<LumoraMaterialPropertyBlock> propertyBlockRef)
    {
        // TODO: Implement proper material property block asset conversion when asset system is ready
        if (propertyBlockRef?.Asset != null)
        {
            // For now, return null until asset system is implemented
            AquaLogger.Debug("MeshRendererHookBase: GetGodotMaterialPropertyBlockFromAsset not implemented");
        }
        return null;
    }
}
