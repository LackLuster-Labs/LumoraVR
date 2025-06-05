using System;
using LumoraVR.Source.Assets;
using Godot;
using Godot.Collections;

namespace LumoraVR.Source.Scene.Assets;

/*
public class ImageTextureProvider : ITextureProvider
{
    public static Dictionary<string,Texture2D> Cache = new();
    
    public string Path;
    private Texture2D _tex;
    
    public bool AssetReady => _tex is not null;
    
    public void Initialize(Godot.Collections.Dictionary<string, Variant> data)
    {
        if (!data.TryGetValue("path", out var p)) return;
        
        Path = p.AsString().Trim();

        //if we have already cached the texture, pull it from the cache, otherwise start fetching it
        if (Cache.TryGetValue(Path, out var tex)) _tex = tex;
        else Set(_ => { });
    }
    /// <summary>
    /// Set a texture property, or queue a property to be set when the asset has loaded
    /// </summary>
    /// <param name="setAction">The method to execute to set the property</param>
    public void Set(Action<Texture2D> setAction)
    {
        if (AssetReady) setAction(_tex);
        else
        {
            AssetFetcher.FetchAsset(Path, bytes =>
            {
                if (Cache.TryGetValue(Path, out _tex))
                {
                    setAction(_tex);
                    return;
                }
                _tex = AssetParser.ParseImage(Path, bytes);
                Cache[Path] = _tex;
                setAction(_tex);
            });
        }
    }
}
*/

public class ImageTextureProvider : ITextureProvider, IFileAssetProvider<Texture2D>
{
    public void Set(Action<Texture2D> setAction) => SimpleAssetCache<Texture2D>.DoSet(this, setAction);
    public bool AssetReady => Asset is not null;
    public void Initialize(IRootObject owner, Dictionary<string, Variant> data) => SimpleAssetCache<Texture2D>.DoInitialize(this, data);
    public string Path { get; set; }
    public Texture2D Asset { get; set; }
    public Texture2D ParseAsset(byte[] data) => AssetParser.ParseImage(Path, data);
}