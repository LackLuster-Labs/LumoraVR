using System.Collections.Generic;
using Godot;

namespace Aquamarine.Kinetix.Core;

public class IKChain
{
    public Skeleton3D Skeleton { get; set; }
    public int[] Bones { get; set; }
    public float[] BoneLengths { get; private set; }
    public float TotalLength { get; private set; }

    public static IKChain FromTipBone(Skeleton3D skeleton, string tipBoneName, int chainLength)
    {
        if (skeleton == null || string.IsNullOrEmpty(tipBoneName))
            return null;

        int tipBone = skeleton.FindBone(tipBoneName);
        if (tipBone == -1)
        {
            GD.PushWarning($"Kinetix: Bone '{tipBoneName}' not found");
            return null;
        }

        var bones = new List<int>();
        int current = tipBone;

        for (int i = 0; i < chainLength && current != -1; i++)
        {
            bones.Insert(0, current);
            current = skeleton.GetBoneParent(current);
        }

        if (bones.Count != chainLength)
        {
            GD.PushWarning($"Kinetix: Chain too short ({bones.Count}/{chainLength})");
            return null;
        }

        var chain = new IKChain { Skeleton = skeleton, Bones = bones.ToArray() };
        chain.CacheBoneLengths();
        return chain;
    }

    public static IKChain FromBoneNames(Skeleton3D skeleton, params string[] boneNames)
    {
        if (skeleton == null || boneNames == null || boneNames.Length == 0)
            return null;

        var bones = new List<int>();
        foreach (var name in boneNames)
        {
            int idx = skeleton.FindBone(name);
            if (idx == -1)
            {
                GD.PushWarning($"Kinetix: Bone '{name}' not found");
                return null;
            }
            bones.Add(idx);
        }

        var chain = new IKChain { Skeleton = skeleton, Bones = bones.ToArray() };
        chain.CacheBoneLengths();
        return chain;
    }

    public void CacheBoneLengths()
    {
        if (Bones == null || Bones.Length == 0) return;

        BoneLengths = new float[Bones.Length];
        TotalLength = 0;

        for (int i = 0; i < Bones.Length; i++)
        {
            BoneLengths[i] = Skeleton.GetBoneRest(Bones[i]).Origin.Length();
            TotalLength += BoneLengths[i];
        }
    }

    public Vector3 GetBoneGlobalPosition(int boneIndex)
    {
        if (boneIndex < 0 || boneIndex >= Bones.Length)
            return Vector3.Zero;
        return Skeleton.GetBoneGlobalPose(Bones[boneIndex]).Origin;
    }

    public Quaternion GetBoneGlobalRotation(int boneIndex)
    {
        if (boneIndex < 0 || boneIndex >= Bones.Length)
            return Quaternion.Identity;
        return Skeleton.GetBoneGlobalPose(Bones[boneIndex]).Basis.GetRotationQuaternion();
    }

    public void SetBoneRotation(int boneIndex, Quaternion rotation)
    {
        if (boneIndex < 0 || boneIndex >= Bones.Length) return;
        Skeleton.SetBonePoseRotation(Bones[boneIndex], rotation);
    }

    public void SetBonePosition(int boneIndex, Vector3 position)
    {
        if (boneIndex < 0 || boneIndex >= Bones.Length) return;
        Skeleton.SetBonePosePosition(Bones[boneIndex], position);
    }

    public string GetBoneName(int boneIndex)
    {
        if (boneIndex < 0 || boneIndex >= Bones.Length)
            return "";
        return Skeleton.GetBoneName(Bones[boneIndex]);
    }

    public bool IsValid()
    {
        return Skeleton != null && Bones != null && Bones.Length > 0 && BoneLengths != null;
    }
}
