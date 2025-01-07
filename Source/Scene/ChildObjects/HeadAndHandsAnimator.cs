using System.Linq;
using Aquamarine.Source.Helpers;
using Aquamarine.Source.Input;
using Aquamarine.Source.Scene.RootObjects;
using Godot;
using Godot.Collections;

namespace Aquamarine.Source.Scene.ChildObjects;

public partial class HeadAndHandsAnimator : Node, IChildObject
{
    public Node Self => this;

    public Armature Armature 
    { 
        get; 
        set 
        { 
            field = value;
            CallDeferred(MethodName.UpdateBoneIndices);
        } 
    }
    public ICharacterController CharacterController;

    public bool Valid;
    
    public string HeadBone;
    public string LeftHandBone;
    public string RightHandBone;

    public Transform3D HeadBoneOffset = Transform3D.Identity;
    public Transform3D LeftHandBoneOffset = Transform3D.Identity;
    public Transform3D RightHandBoneOffset = Transform3D.Identity;

    public int HeadBoneIndex;
    public int LeftHandBoneIndex;
    public int RightHandBoneIndex;
    
    
    public void SetPlayerAuthority(int id)
    {
        
    }
    public void Initialize(Dictionary<string, Variant> data)
    {
        //GD.Print(data);
        
        if (data.TryGetValue("headBone", out var bone)) HeadBone = bone.AsString();
        if (data.TryGetValue("leftHandBone", out var lbone)) LeftHandBone = lbone.AsString();
        if (data.TryGetValue("rightHandBone", out var rbone)) RightHandBone = rbone.AsString();
        if (data.TryGetValue("headOffset", out var offset) && offset.TryGetFloat32Array(out var offsetArray)) HeadBoneOffset = offsetArray.ToTransform3D();
        if (data.TryGetValue("leftHandOffset", out var loffset) && loffset.TryGetFloat32Array(out var loffsetArray)) LeftHandBoneOffset = loffsetArray.ToTransform3D();
        if (data.TryGetValue("rightHandOffset", out var roffset) && roffset.TryGetFloat32Array(out var roffsetArray)) RightHandBoneOffset = roffsetArray.ToTransform3D();
        
        if (Root is not Avatar avi || avi.Parent is not ICharacterController charController) return;
        CharacterController = charController;
        
        if (data.TryGetValue("armature", out var index) && index.TryGetInt32(out var skeletonIndex))
        {
            if (skeletonIndex >= 0 && Root.ChildObjects.TryGetValue((ushort)skeletonIndex, out var v) && v is Armature armature) Armature = armature;
        }
    }
    private void UpdateBoneIndices()
    {
        HeadBoneIndex = Armature.FindBone(HeadBone);
        LeftHandBoneIndex = Armature.FindBone(LeftHandBone);
        RightHandBoneIndex = Armature.FindBone(RightHandBone);

        var count = Armature.GetBoneCount();
        
        if (HeadBoneIndex < 0 || HeadBoneIndex >= count || LeftHandBoneIndex < 0 || LeftHandBoneIndex >= count || RightHandBoneIndex < 0 || RightHandBoneIndex >= count) return;
        Valid = true;
    }
    public override void _Process(double delta)
    {
        base._Process(delta);
        
        if (!Valid) return;
        
        var headPose = CharacterController.GetLimbTransform(IInputProvider.InputLimb.Head);
        var leftHandPose = CharacterController.GetLimbTransform(IInputProvider.InputLimb.LeftHand);
        var rightHandPose = CharacterController.GetLimbTransform(IInputProvider.InputLimb.RightHand);
            
        Armature.SetBoneGlobalPose(HeadBoneIndex, new Transform3D(new Basis(headPose.rot), headPose.pos) * HeadBoneOffset);
        Armature.SetBoneGlobalPose(LeftHandBoneIndex, new Transform3D(new Basis(leftHandPose.rot), leftHandPose.pos) * LeftHandBoneOffset);
        Armature.SetBoneGlobalPose(RightHandBoneIndex, new Transform3D(new Basis(rightHandPose.rot), rightHandPose.pos) * RightHandBoneOffset);
    }
    public void AddChildObject(ISceneObject obj)
    {
        AddChild(obj.Self);
    }
    public bool Dirty => false;
    public IRootObject Root { get; set; }
    public ISceneObject Parent { get; set; }
}
