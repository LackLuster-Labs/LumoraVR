using System.Collections.Generic;
using LumoraVR.Source.Helpers;
using LumoraVR.Source.Scene.Assets;
using Godot;

namespace LumoraVR.Source.Scene.ChildObjects;

public partial class MeshRenderer : MeshInstance3D, IChildObject
{
    public IMeshProvider MeshProvider
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (value == null)
            {
                var count = GetSurfaceOverrideMaterialCount();
                for (var i = 0; i < count; i++) SetSurfaceOverrideMaterial(i, null);
                Mesh = null;
                Skin = null;
            }
            else
                value.Set(m =>
                {
                    // Check if this object is still valid
                    if (!GodotObject.IsInstanceValid(this)) return;
                    if (MeshProvider != value) return;

                    Mesh = m.Mesh;
                    Skin = m.Skin;

                    var count = Mathf.Min(GetSurfaceOverrideMaterialCount(), _materials.Length);
                    for (var i = 0; i < count; i++)
                    {
                        var index = i;
                        if (_materials[i] != null)
                        {
                            _materials[i].Set(mat =>
                            {
                                // Check again if still valid
                                if (GodotObject.IsInstanceValid(this))
                                {
                                    SetSurfaceOverrideMaterial(index, mat);
                                }
                            });
                        }
                    }
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
        }
    }
    private IMaterialProvider[] _materials = [];

    private void SetMaterial(int index, IMaterialProvider mat)
    {
        if (index < 0 || index >= _materials.Length) return;
        _materials[index] = mat;
        if (mat is not null) mat.Set(m => SetSurfaceOverrideMaterial(index, m));
        else SetSurfaceOverrideMaterial(index, null);
    }

    public Node Self => this;
    public void SetPlayerAuthority(int id)
    {

    }
    public void Initialize(Godot.Collections.Dictionary<string, Variant> data)
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
        if (data.TryGetValue("materials", out var mats) && mats.TryGetInt32Array(out var materialArray))
        {
            var count = materialArray.Length;
            _materials = new IMaterialProvider[materialArray.Length];
            for (var i = 0; i < count; i++)
            {
                var ind = materialArray[i];
                if (ind >= 0 && Root.AssetProviders.TryGetValue((ushort)ind, out var v) && v is IMaterialProvider mat)
                {
                    _materials[i] = mat;
                }
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
