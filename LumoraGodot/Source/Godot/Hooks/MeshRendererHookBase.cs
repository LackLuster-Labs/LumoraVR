using Godot;
using Lumora.Core;
using Lumora.Core.Components;
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
    }

    protected virtual void OnCleanupRenderer()
    {
    }

    public override void ApplyChanges()
    {
        if (Owner.Mesh.Value != null && IsMeshAssetAvailable())
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
        meshInstance = null;
        MeshRenderer = default(U);
        base.Destroy(destroyingWorld);
    }

    protected virtual bool IsMeshAssetAvailable()
    {
        return Owner.Mesh.Value != null;
    }

    protected virtual Mesh GetGodotMeshFromAsset()
    {
        AquaLogger.Warn("MeshRendererHookBase: GetGodotMeshFromAsset not implemented - using placeholder");
        return new BoxMesh();
    }

    protected virtual void ApplySortingOrder(int sortingOrder)
    {
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
}
