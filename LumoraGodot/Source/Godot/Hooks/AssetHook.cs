using Lumora.Core;
using Lumora.Core.Assets;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// Base class for asset hooks.
/// Platform asset hook for Godot.
///
/// Asset hooks bridge LumoraCore assets to Godot resources (textures, meshes, materials, etc.)
/// </summary>
public abstract class AssetHook : IAssetHook
{
    protected IAsset asset;

    /// <summary>
    /// The engine this asset belongs to (currently unused in Lumora).
    /// </summary>
    public object Engine => null; // TODO: Assets don't have World reference yet

    /// <summary>
    /// Initialize the asset hook.
    /// </summary>
    public void Initialize(IAsset asset)
    {
        this.asset = asset;
    }

    /// <summary>
    /// Unload/dispose the asset hook and its Godot resources.
    /// Override this to free Godot resources.
    /// </summary>
    public abstract void Unload();
}
