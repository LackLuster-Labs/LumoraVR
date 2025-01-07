using System;
using Aquamarine.Source.Networking;
using Aquamarine.Source.Scene.Assets;
using Aquamarine.Source.Scene.ChildObjects;
using Bones.Core;
using Godot;
using Godot.Collections;
using Avatar = Aquamarine.Source.Scene.RootObjects.Avatar;

namespace Aquamarine.Source.Scene;

public class Prefab
{
    public static readonly System.Collections.Generic.Dictionary<string,Prefab> Cache = new();
    public static void ClearCache() => Cache.Clear();
    public static void SpawnObject(IRootObject root, string prefab)
    {
        if (root is null) return;
        
        //GD.Print("spawn obj");
        
        if (Cache.TryGetValue(prefab, out var p)) AssignPrefab(root, p);
        else
        {
            AssetFetcher.FetchAsset(prefab, bytes =>
            {
                if (Cache.TryGetValue(prefab, out var result))
                {
                    AssignPrefab(root, result);
                    return;
                }
                var parsed = Deserialize(bytes.GetStringFromUtf8());
                Cache[prefab] = parsed;
                AssignPrefab(root, parsed);
            });
        }
    }
    private static void AssignPrefab(IRootObject root, Prefab prefab)
    {
        if (root is null) return;
        if (prefab is null) return;
        if (!prefab.Valid()) return;
        if (!prefab.Type.MatchesObject(root)) return;
        prefab.Instantiate(root);
    }


    public int Version;
    public RootObjectType Type;
    public Dictionary<string, Variant> Data = new();
    public System.Collections.Generic.Dictionary<ushort, PrefabChild> Children = new();
    public System.Collections.Generic.Dictionary<ushort, PrefabAsset> Assets = new();
    public string CachedString { get; private set; }
    public void ClearStringCache() => CachedString = null;
    public static Prefab Deserialize(string json)
    {
        var prefab = new Prefab();
        var collection = Json.ParseString(json);
        if (collection.VariantType is Variant.Type.Dictionary)
        {
            var dict = collection.AsGodotDictionary();
            if (dict.TryGetValue("version", out var v)) prefab.Version = v.AsInt32();
            if (dict.TryGetValue("type", out var t)) prefab.Type = Enum.Parse<RootObjectType>(t.AsString(), true);
            if (dict.TryGetValue("data", out var d)) prefab.Data = d.AsGodotDictionary<string, Variant>();
            if (dict.TryGetValue("children", out var c))
            {
                var childrenDict = c.AsGodotDictionary<ushort, Dictionary>();
                foreach (var pair in childrenDict) prefab.Children[pair.Key] = PrefabChild.Deserialize(pair.Value);
            }
            if (dict.TryGetValue("assets", out var a))
            {
                var childrenDict = a.AsGodotDictionary<ushort, Dictionary>();
                foreach (var pair in childrenDict) prefab.Assets[pair.Key] = PrefabAsset.Deserialize(pair.Value);
            }
        }
        return prefab;
    }
    public IRootObject Instantiate(IRootObject onto = null)
    {
        if (!Valid()) return null;

        var obj = onto ?? Type switch
        {
            RootObjectType.Avatar => new Avatar(),
            _ => throw new ArgumentOutOfRangeException(),
        };

        foreach (var (index, asset) in Assets)
        {
            var a = asset.Instantiate();
            a.Initialize(asset.Data);
            obj.AssetProviders.Add(index, a);
        }
        var children = new System.Collections.Generic.Dictionary<PrefabChild, IChildObject>();
        //instantiate all children
        foreach (var (index, prefabChild) in Children)
        {
            var child = prefabChild.Instantiate();
            child.Root = obj;
            obj.ChildObjects.Add(index, child);
            children.Add(prefabChild, child);
        }
        //parent children
        foreach (var (prefabChild, childObj) in children)
        {
            var parentIndex = prefabChild.Parent;
            if (parentIndex < 0 || !obj.ChildObjects.TryGetValue((ushort)parentIndex, out var parent))
            {
                //GD.Print($"Adding {childObj.GetType()} to root");
                obj.AddChildObject(childObj);
                childObj.Parent = obj;
            }
            else
            {
                parent.AddChildObject(childObj);
                childObj.Parent = parent;
            }
        }
        //apply data to root
        obj.Initialize(Data);
        //apply data to children
        foreach (var (prefabChild, childObj) in children) childObj.Initialize(prefabChild.Data);

        return obj;
    }

    public bool Valid()
    {
        //TODO: more validity checks, like the content of data and the parent hierarchy
        if (!Type.CanInstantiate()) return false;
        return true;
    }
    
    public string Serialize()
    {
        if (CachedString is not null) return CachedString;
        
        var dict = new Dictionary();
        dict["version"] = Version;
        dict["type"] = EnumHelpers<RootObjectType>.ToStringLowerCached(Type);
        dict["data"] = Data;

        var childrenDict = new Dictionary<ushort, Dictionary>();
        foreach (var pair in Children) childrenDict[pair.Key] = pair.Value.Serialize();
        dict["children"] = childrenDict;

        var assetDict = new Dictionary<ushort, Dictionary>();
        foreach (var pair in Assets) assetDict[pair.Key] = pair.Value.Serialize();
        dict["assets"] = assetDict;
        
        CachedString = Json.Stringify(dict);
        
        return CachedString;
    }
}

public class PrefabChild
{
    public int Parent = -1; //-1 if parented to the root object, otherwise the ushort index of the parent
    public ChildObjectType Type;
    public Dictionary<string, Variant> Data = new();
    
    public static PrefabChild Deserialize(Dictionary dict)
    {
        var prefab = new PrefabChild();
        
        if (dict.TryGetValue("p", out var v)) prefab.Parent = v.AsInt32();
        if (dict.TryGetValue("t", out var t)) prefab.Type = Enum.Parse<ChildObjectType>(t.AsString(), true);
        if (dict.TryGetValue("d", out var d)) prefab.Data = d.AsGodotDictionary<string, Variant>();
        
        return prefab;
    }
    public IChildObject Instantiate()
    {
        if (!Valid()) return null;
        return Type switch
        {
            //ChildObjectType.Node => expr,
            ChildObjectType.MeshRenderer => new MeshRenderer(),
            ChildObjectType.Armature => new Armature(),
            ChildObjectType.HeadAndHandsAnimator => new HeadAndHandsAnimator(),
            _ => null,
        };
    }
    public bool Valid()
    {
        //TODO
        if (Type is ChildObjectType.None) return false;
        return true;
    }
    public Dictionary Serialize()
    {
        var dict = new Dictionary();
        dict["p"] = Parent;
        dict["t"] = EnumHelpers<ChildObjectType>.ToStringLowerCached(Type);
        dict["d"] = Data;
        return dict;
    }
}

public class PrefabAsset
{
    public AssetProviderType Type;
    public Dictionary<string, Variant> Data = new();
    
    public static PrefabAsset Deserialize(Dictionary dict)
    {
        var prefab = new PrefabAsset();
        
        if (dict.TryGetValue("t", out var t)) prefab.Type = Enum.Parse<AssetProviderType>(t.AsString(), true);
        if (dict.TryGetValue("d", out var d)) prefab.Data = d.AsGodotDictionary<string, Variant>();
        
        return prefab;
    }
    public IAssetProvider Instantiate()
    {
        if (!Valid()) return null;
        return Type switch
        {
            AssetProviderType.ImageTextureProvider => new ImageTextureProvider(),
            AssetProviderType.NoiseTextureProvider => new NoiseTextureProvider(),
            AssetProviderType.MeshFileProvider => new MeshFileProvider(),
            _ => null,
        };
    }
    public bool Valid()
    {
        //TODO
        return true;
    }
    public Dictionary Serialize()
    {
        var dict = new Dictionary();
        dict["t"] = EnumHelpers<AssetProviderType>.ToStringLowerCached(Type);
        dict["d"] = Data;
        return dict;
    }
}
