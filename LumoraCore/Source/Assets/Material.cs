using System;

namespace Lumora.Core.Assets;

/// <summary>
/// Material asset - represents a shader with properties.
/// Combines a shader, properties (colors, textures, parameters), and render settings.
/// </summary>
public class Material : SharedMaterialBase<IMaterialHook>
{
    private bool? _lastInstancing;

    // ===== MATERIAL-SPECIFIC OPERATIONS =====

    /// <summary>
    /// Enable or disable GPU instancing for this material.
    /// Instancing allows efficient rendering of many objects with the same material.
    /// </summary>
    public void SetInstancing(bool enabled)
    {
        _lastInstancing = enabled;
        Hook?.SetInstancing(enabled);
    }

    /// <summary>
    /// Set the render queue for this material.
    /// Controls when the material is rendered (opaque first, then transparent, etc.).
    /// Lower values render first. Standard queues:
    /// - Background: 1000
    /// - Geometry (Opaque): 2000
    /// - AlphaTest: 2450
    /// - Transparent: 3000
    /// - Overlay: 4000
    /// </summary>
    public void SetRenderQueue(int renderQueue)
    {
        Hook?.SetRenderQueue(renderQueue);
    }

    /// <summary>
    /// Set a material tag (e.g., RenderType = "Opaque").
    /// Tags control shader behavior and rendering passes.
    /// </summary>
    public void SetTag(MaterialTag tag, string value)
    {
        Hook?.SetTag(tag, value);
    }

    /// <summary>
    /// Update instancing state only if it changed.
    /// Avoids redundant hook calls.
    /// </summary>
    public void UpdateInstancing(bool enabled)
    {
        if (enabled != _lastInstancing)
        {
            SetInstancing(enabled);
        }
    }

    /// <summary>
    /// Update render queue from a Sync field.
    /// NOTE: Commented out because Sync<T> doesn't have WasChanged, it has IsDirty.
    /// Use field.OnChanged event instead to call SetRenderQueue directly.
    /// </summary>
    /*
	public void UpdateRenderQueue(Core.Sync<int> field)
	{
		if (field.IsDirty)
		{
			field.IsDirty = false;
			SetRenderQueue(field.Value);
		}
	}
	*/
}
