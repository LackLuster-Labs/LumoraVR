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
    /// The engine instance. Assets are global and not tied to a specific world.
    /// </summary>
    public Engine Engine => Lumora.Core.Engine.Current;

    /// <summary>
    /// The currently focused world (convenience accessor).
    /// May be null if no world is focused.
    /// </summary>
    public World FocusedWorld => Engine?.WorldManager?.FocusedWorld;

    /// <summary>
    /// The asset this hook is attached to.
    /// </summary>
    public IAsset Asset => asset;

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
