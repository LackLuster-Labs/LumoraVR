using System.Collections.Generic;
using LumoraVR.Source.Scene.Assets;

namespace LumoraVR.Source.Scene;

public enum RootObjectType
{
    None,
    Avatar,
    PlayerCharacterController,
}
public interface IRootObject : ISceneObject
{
    //public bool Dirty { get; set; }
    public IDictionary<ushort, IChildObject> ChildObjects { get; }
    public IDictionary<ushort, IAssetProvider> AssetProviders { get; }

    //public void SendChanges();
    //public void ReceiveChanges(byte[] data);

    //public void InitializeChildren();
}