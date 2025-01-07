using Aquamarine.Source.Helpers;
using Aquamarine.Source.Scene.Assets;
using Godot;
using Godot.Collections;

namespace Aquamarine.Source.Scene.ChildObjects;

public partial class MeshRenderer : MeshInstance3D, IChildObject
{
    public IMeshProvider MeshProvider
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (value == null) Mesh = null;
            else
                value.Set(m =>
                {
                    if (MeshProvider != value) return;
                    Mesh = m.Mesh;
                    Skin = m.Skin;
                });
        }
    }
    public Armature Armature
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Skeleton = value == null ? null : GetPathTo(field);
            //Skin = value == null ? null : field.Skin;
        }
    }

    public Node Self => this;
    public void SetPlayerAuthority(int id)
    {
        
    }
    public void Initialize(Dictionary<string, Variant> data)
    {
        if (data.TryGetValue("transform", out var pos) && pos.VariantType is Variant.Type.PackedFloat32Array)
        {
            Transform = pos.AsFloat32Array().ToTransform3D();
        }
        if (data.TryGetValue("armature", out var index) && index.TryGetInt32(out var skeletonIndex))
        {
            if (skeletonIndex >= 0 && Root.ChildObjects.TryGetValue((ushort)skeletonIndex, out var v) && v is Armature armature)
            {
                Armature = armature;
            }
        }
        if (data.TryGetValue("mesh", out var mIndex))
        {
            var meshIndex = mIndex.AsInt32();
            if (meshIndex >= 0 && Root.AssetProviders.TryGetValue((ushort)meshIndex, out var v) && v is IMeshProvider meshProvider) MeshProvider = meshProvider;
        }
    }
    public void AddChildObject(ISceneObject obj) => AddChild(obj.Self);
    public bool Dirty { get; }
    public IRootObject Root { get; set; }
    public ISceneObject Parent { get; set; }
}
