using System;
using Godot;
using Godot.Collections;

namespace LumoraVR.Source.Scene.Assets;

public class NoiseTextureProvider : ITextureProvider
{
    public Texture2D Texture => _texture;
    private NoiseTexture2D _texture = new();
    public bool AssetReady => true;
    public void Initialize(IRootObject owner, Dictionary<string, Variant> data)
    {
        throw new System.NotImplementedException();
    }
    public void Set(Action<Texture2D> setAction) => setAction(_texture);
}
