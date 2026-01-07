using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// Hook for MeshRenderer component → Godot MeshInstance3D.
/// Platform mesh renderer hook for Godot.
/// Now properly uses the asset system instead of hardcoded mesh creation.
/// </summary>
public class MeshRendererHook : MeshRendererHookBase<MeshRenderer, Node3D>
{
    protected override bool UseMeshInstance => true;

    protected override void AssignMesh(Node3D renderer, Mesh mesh)
    {
        // This method is not used since UseMeshInstance = true
        // The base class handles mesh assignment via MeshInstance3D
        throw new System.NotImplementedException("AssignMesh should not be called when UseMeshInstance is true");
    }

    protected override void OnAttachRenderer()
    {
        base.OnAttachRenderer();
        AquaLogger.Log($"MeshRendererHook: Attached renderer for slot '{Owner.Slot.SlotName.Value}'");
    }

    protected override void OnCleanupRenderer()
    {
        base.OnCleanupRenderer();
        AquaLogger.Log($"MeshRendererHook: Cleaned up renderer for slot '{Owner?.Slot?.SlotName.Value}'");
    }

    /// <summary>
    /// Factory method for creating MeshRenderer hooks.
    /// </summary>
    public static IHook<MeshRenderer> Constructor()
    {
        return new MeshRendererHook();
    }
}
