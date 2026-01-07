using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using LumoraMeshes = Lumora.Core.Components.Meshes;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// Abstract base class for MeshRenderer hooks.
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

    private MeshInstance3D meshInstance;

    protected abstract bool UseMeshInstance { get; }

    protected bool meshWasChanged { get; private set; }

    public U MeshRenderer { get; protected set; }

    protected abstract void AssignMesh(U renderer, Mesh mesh);

    protected virtual void OnAttachRenderer()
    {
        // Hide the source mesh's MeshInstance3D since we're taking over rendering
        HideSourceMeshInstance();
    }

    /// <summary>
    /// Hide the MeshInstance3D from the source mesh (ProceduralMesh) since MeshRenderer handles rendering.
    /// </summary>
    private void HideSourceMeshInstance()
    {
        var meshValue = Owner.Mesh.Target;
        if (meshValue is LumoraMeshes.ProceduralMesh proceduralMesh)
        {
            if (proceduralMesh.Hook is MeshHook meshHook)
            {
                var sourceMeshInstance = meshHook.GetMeshInstance();
                if (sourceMeshInstance != null)
                {
                    sourceMeshInstance.Visible = false;
                    AquaLogger.Debug($"MeshRendererHookBase: Hid source MeshHook's MeshInstance3D");
                }
            }
        }
    }

    protected virtual void OnCleanupRenderer()
    {
    }

    public override void ApplyChanges()
    {
        if (Owner.Mesh.Target != null && IsMeshAssetAvailable())
        {
            if (MeshRenderer == null)
            {
                var gameObject = new Node3D();
                gameObject.Name = "MeshRenderer";
                attachedNode.AddChild(gameObject);

                if (UseMeshInstance)
                {
                    meshInstance = new MeshInstance3D();
                    gameObject.AddChild(meshInstance);
                }

                MeshRenderer = gameObject as U;
                OnAttachRenderer();
            }

            meshWasChanged = Owner.Mesh.GetWasChangedAndClear();
            if (meshWasChanged)
            {
                // Hide the source mesh's MeshInstance3D when mesh changes
                HideSourceMeshInstance();

                var godotMesh = GetGodotMeshFromAsset();
                if (UseMeshInstance && meshInstance != null)
                {
                    meshInstance.Mesh = godotMesh;

                    // Debug: verify mesh has UV data
                    if (godotMesh is ArrayMesh arrayMesh && arrayMesh.GetSurfaceCount() > 0)
                    {
                        var arrays = arrayMesh.SurfaceGetArrays(0);
                        var uvArray = arrays[(int)Mesh.ArrayType.TexUV];
                        bool hasUvs = uvArray.VariantType != Variant.Type.Nil;
                        AquaLogger.Log($"MeshRendererHookBase: Mesh assigned, surface 0 has UV data: {hasUvs}");
                        if (hasUvs)
                        {
                            var uvs = uvArray.AsVector2Array();
                            if (uvs.Length > 0)
                            {
                                AquaLogger.Log($"MeshRendererHookBase: UV sample[0] = {uvs[0]}");
                            }
                        }
                    }
                }
                else
                {
                    AssignMesh(MeshRenderer, godotMesh);
                }
            }

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

            // Apply material if changed
            if (Owner.Material.GetWasChangedAndClear() || meshWasChanged)
            {
                ApplyMaterial();
            }
        }
        else
        {
            CleanupRenderer(destroyingWorld: false);
        }
    }

    /// <summary>
    /// Apply the material to the mesh instance.
    /// </summary>
    protected virtual void ApplyMaterial()
    {
        if (meshInstance == null) return;

        var materialAsset = Owner.Material.Asset;
        if (materialAsset != null && materialAsset.GodotMaterial is Material godotMaterial)
        {
            AquaLogger.Debug($"MeshRendererHookBase.ApplyMaterial: Applying {godotMaterial.GetType().Name} to meshInstance");
            meshInstance.MaterialOverride = godotMaterial;
        }
        else
        {
            AquaLogger.Debug($"MeshRendererHookBase.ApplyMaterial: No material (materialAsset={materialAsset}, GodotMaterial={materialAsset?.GodotMaterial})");
            meshInstance.MaterialOverride = null;
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
        meshInstance = null;
        MeshRenderer = default(U);
        base.Destroy(destroyingWorld);
    }

    protected virtual bool IsMeshAssetAvailable()
    {
        return Owner.Mesh.Target != null;
    }

    protected virtual Mesh GetGodotMeshFromAsset()
    {
        var meshComponent = Owner.Mesh.Target;
        if (meshComponent == null) return null;

        // Handle ProceduralMesh components - get mesh from their hook
        if (meshComponent is LumoraMeshes.ProceduralMesh proceduralMesh)
        {
            if (proceduralMesh.Hook is MeshHook meshHook)
            {
                var meshInstance = meshHook.GetMeshInstance();
                if (meshInstance?.Mesh != null)
                {
                    return meshInstance.Mesh;
                }
            }
            AquaLogger.Debug("MeshRendererHookBase: ProceduralMesh hook not ready");
            return null;
        }

        // TODO: Handle MeshDataAssetProvider components when needed
        AquaLogger.Warn($"MeshRendererHookBase: Unsupported mesh component type {meshComponent.GetType().Name}");
        return null;
    }

    protected virtual void ApplySortingOrder(int sortingOrder)
    {
        if (meshInstance != null)
        {
            // Use sorting offset for depth sorting of transparent objects
            // Higher values render later (on top)
            meshInstance.SortingOffset = sortingOrder;
        }
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
}
