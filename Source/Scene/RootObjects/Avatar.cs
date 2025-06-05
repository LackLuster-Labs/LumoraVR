using System;
using System.Collections.Generic;
using LumoraVR.Source.Helpers;
using LumoraVR.Source.Scene.Assets;
using Godot;

namespace LumoraVR.Source.Scene.RootObjects;

public partial class Avatar : Node3D, IRootObject
{
    public Node Self => this;
    public IDictionary<ushort, IChildObject> ChildObjects { get; } = new Dictionary<ushort, IChildObject>();
    public IDictionary<ushort, IAssetProvider> AssetProviders { get; } = new Dictionary<ushort, IAssetProvider>();
    public DirtyFlags64 DirtyFlags;

    public ICharacterController Parent;

    public void SetPlayerAuthority(int id)
    {

    }
    public void Initialize(Godot.Collections.Dictionary<string, Variant> data)
    {

    }
    public void AddChildObject(ISceneObject obj) => AddChild(obj.Self);
}
