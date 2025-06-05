using System;
using LumoraVR.Source.Assets;
using Godot;
using Godot.Collections;

namespace LumoraVR.Source.Scene.Assets;

public class MeshFileProvider : IMeshProvider, IFileAssetProvider<MeshAsset>
{
    public void Set(Action<MeshAsset> setAction) => SimpleAssetCache<MeshAsset>.DoSet(this, setAction);
    public bool AssetReady => Asset is not null;
    public void Initialize(IRootObject owner, Dictionary<string, Variant> data) => SimpleAssetCache<MeshAsset>.DoInitialize(this, data);
    public string Path { get; set; }
    public MeshAsset Asset { get; set; }
    public MeshAsset ParseAsset(byte[] data) => AssetParser.ParseMesh(data);
}
