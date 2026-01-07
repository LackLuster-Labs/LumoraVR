using System;

namespace Lumora.Core.Assets;

/// <summary>
/// Base class for dynamic assets that have engine-specific hooks.
/// Runtime-generated assets that bridge to engine implementations through hooks.
/// </summary>
public abstract class DynamicImplementableAsset<C> : DynamicAsset where C : class, IAssetHook
{
    /// <summary>
    /// The engine-specific hook that implements this asset.
    /// For materials, this would be a Godot ShaderMaterial wrapper.
    /// </summary>
    public C Hook { get; private set; }

    /// <summary>
    /// Create the hook instance for this asset.
    /// Override to provide custom hook instantiation.
    /// </summary>
    protected virtual C InstantiateHook()
    {
        // TODO: Implement hook registration system for asset hook registration
        // For now, derived classes should override this
        Type hookType = GetHookType();
        if (hookType == null)
        {
            return null;
        }
        return (C)Activator.CreateInstance(hookType);
    }

    /// <summary>
    /// Get the hook type for this asset.
    /// Override in derived classes or use a registration system.
    /// </summary>
    protected virtual Type GetHookType()
    {
        // Will be replaced with AssetInitializer system later
        return null;
    }

    public override void InitializeDynamic()
    {
        base.InitializeDynamic();
        InitializeHook();
    }

    private void InitializeHook()
    {
        Hook = InstantiateHook();
        Hook?.Initialize(this);
    }

    public override void Unload()
    {
        Hook?.Unload();
    }
}
