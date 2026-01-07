using Lumora.Core;
using Lumora.Core.Assets;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Renders a 3D mesh.
/// </summary>
[ComponentCategory("Rendering")]
public class MeshRenderer : ImplementableComponent
{
    /// <summary>
    /// The mesh to render (ProceduralMesh or MeshDataAsset component).
    /// Uses SyncRef to properly sync component references over network.
    /// </summary>
    public readonly SyncRef<Component> Mesh;

    /// <summary>
    /// The material to use for rendering.
    /// </summary>
    public readonly AssetRef<MaterialAsset> Material;

    /// <summary>
    /// Shadow casting mode (Off, On, ShadowOnly, DoubleSided).
    /// </summary>
    public readonly Sync<ShadowCastMode> ShadowCastMode;

    /// <summary>
    /// Sorting order for transparent rendering (lower values render first).
    /// </summary>
    public readonly Sync<int> SortingOrder;

    public MeshRenderer()
    {
        Mesh = new SyncRef<Component>(this);
        Material = new AssetRef<MaterialAsset>(this);
        ShadowCastMode = new Sync<ShadowCastMode>(this, Components.ShadowCastMode.On);
        SortingOrder = new Sync<int>(this, 0);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        AquaLogger.Log($"MeshRenderer: Awake on slot '{Slot.SlotName.Value}'");
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        AquaLogger.Log($"MeshRenderer: Destroyed on slot '{Slot?.SlotName.Value}'");
    }
}

/// <summary>
/// Shadow casting modes for MeshRenderer.
/// </summary>
public enum ShadowCastMode
{
    Off = 0,
    On = 1,
    ShadowOnly = 2,
    DoubleSided = 3
}

/// <summary>
/// Motion vector generation modes for motion blur and temporal effects.
/// </summary>
public enum MotionVectorMode
{
    Camera = 0,
    Object = 1,
    NoMotion = 2
}
