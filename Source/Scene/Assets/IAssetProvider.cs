using System;
using System.Collections.Generic;
using LumoraVR.Source.Assets;
using LumoraVR.Source.Networking;
using Godot;

namespace LumoraVR.Source.Scene.Assets;

public enum AssetProviderType
{
    //textures
    ImageTextureProvider,
    NoiseTextureProvider,

    //meshes
    MeshFileProvider,

    //materials
    BasicMaterialProvider,
}
public interface IAssetProvider
{
    public bool AssetReady { get; }
    public void Initialize(IRootObject owner, Godot.Collections.Dictionary<string, Variant> data);
}

public interface IAssetProvider<T> : IAssetProvider
{
    public void Set(Action<T> setAction);
}

public interface IFileAssetProvider<T> : IAssetProvider<T>
{
    public string Path { get; set; }
    public T Asset { get; set; }

    public T ParseAsset(byte[] data);
}

public static class SimpleAssetCache
{
    public static void ClearAllCaches()
    {
        SimpleAssetCache<MeshAsset>.Cache.Clear();
        SimpleAssetCache<Texture2D>.Cache.Clear();
    }
}
public static class SimpleAssetCache<T>
{
    public static readonly Dictionary<string, T> Cache = new();

    public static void DoInitialize(IFileAssetProvider<T> provider, Godot.Collections.Dictionary<string, Variant> data)
    {
        if (!data.TryGetValue("path", out var p)) return;

        var path = p.AsString().Trim();
        provider.Path = path;

        //if we have already cached the texture, pull it from the cache, otherwise start fetching it
        if (Cache.TryGetValue(path, out var result)) provider.Asset = result;
        else provider.Set(_ => { });
    }
    public static void DoSet(IFileAssetProvider<T> provider, Action<T> setAction)
    {
        // Add this check to prevent callbacks on disposed objects
        if (provider is GodotObject gObj && !GodotObject.IsInstanceValid(gObj))
        {
            return; // Don't proceed if the provider has been disposed
        }

        if (provider.AssetReady) setAction(provider.Asset);
        else if (Cache.TryGetValue(provider.Path, out var cache))
        {
            provider.Asset = cache;
            setAction(provider.Asset);
        }
        else
        {
            var path = provider.Path;
            AssetFetcher.FetchAsset(path, bytes =>
            {
                // Add another check here to validate before callback
                if (provider is GodotObject gObj2 && !GodotObject.IsInstanceValid(gObj2))
                {
                    return; // Provider was disposed while waiting for asset
                }

                if (Cache.TryGetValue(path, out var result))
                {
                    provider.Asset = result;
                    setAction(result);
                    return;
                }

                var parsed = provider.ParseAsset(bytes);

                provider.Asset = parsed;
                Cache[path] = parsed;

                setAction(parsed);
            });
        }
    }
}
