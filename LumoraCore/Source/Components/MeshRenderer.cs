using System;
using Lumora.Core;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Renders a 3D mesh.
/// </summary>
[ComponentCategory("Rendering")]
public class MeshRenderer : ImplementableComponent
{
    /// <summary>
    /// The mesh to render.
    /// </summary>
    public Sync<object> Mesh { get; private set; }

    /// <summary>
    /// Shadow casting mode (Off, On, ShadowOnly, DoubleSided).
    /// </summary>
    public Sync<ShadowCastMode> ShadowCastMode { get; private set; }

    /// <summary>
    /// Sorting order for transparent rendering (lower values render first).
    /// </summary>
    public Sync<int> SortingOrder { get; private set; }

    public override void OnAwake()
    {
        base.OnAwake();

        Mesh = new Sync<object>(this, default);
        ShadowCastMode = new Sync<ShadowCastMode>(this, Components.ShadowCastMode.On);
        SortingOrder = new Sync<int>(this, 0);

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
