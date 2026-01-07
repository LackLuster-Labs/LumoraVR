using Aquamarine.Kinetix.Core;
using Godot;

namespace Aquamarine.Kinetix.Solvers;

public class TwoBoneIKSolver : IKSolver
{
    public override bool Solve()
    {
        if (!IsValid()) return false;

        if (Chain.Bones.Length != 3)
        {
            GD.PushError($"TwoBoneIK needs 3 bones, got {Chain.Bones.Length}");
            return false;
        }

        int rootBone = Chain.Bones[0];
        int midBone = Chain.Bones[1];
        int tipBone = Chain.Bones[2];

        Vector3 rootPos = Chain.GetBoneGlobalPosition(0);
        Vector3 midPos = Chain.GetBoneGlobalPosition(1);
        Vector3 tipPos = Chain.GetBoneGlobalPosition(2);

        float upperLength = Chain.BoneLengths[1];
        float lowerLength = Chain.BoneLengths[2];
        Vector3 targetPos = Target.Position;

        // Clamp target to reachable range
        Vector3 toTarget = targetPos - rootPos;
        float targetDistance = toTarget.Length();
        float maxReach = upperLength + lowerLength - 0.001f;
        float minReach = Mathf.Abs(upperLength - lowerLength) + 0.001f;

        bool clamped = false;
        if (targetDistance > maxReach)
        {
            targetDistance = maxReach;
            clamped = true;
        }
        else if (targetDistance < minReach)
        {
            targetDistance = minReach;
            clamped = true;
        }

        if (clamped)
            targetPos = rootPos + toTarget.Normalized() * targetDistance;

        float midAngle = KinetixMath.LawOfCosines(upperLength, lowerLength, targetDistance);
        Vector3 poleDir = CalculatePoleDirection(rootPos, targetPos, midPos);
        ApplyTwoBoneRotations(rootBone, midBone, tipBone, rootPos, targetPos, poleDir, midAngle);

        return GetDistanceToTarget() < Tolerance;
    }

    private Vector3 CalculatePoleDirection(Vector3 rootPos, Vector3 targetPos, Vector3 currentMidPos)
    {
        Vector3 chainDir = (targetPos - rootPos).Normalized();

        if (Target.PolePosition.HasValue)
        {
            Vector3 midPoint = (rootPos + targetPos) * 0.5f;
            Vector3 toPole = Target.PolePosition.Value - midPoint;
            toPole = KinetixMath.ProjectOnPlane(toPole, chainDir);

            if (toPole.LengthSquared() < 0.0001f)
            {
                toPole = currentMidPos - midPoint;
                toPole = KinetixMath.ProjectOnPlane(toPole, chainDir);
            }

            return toPole.Normalized();
        }
        else
        {
            Vector3 midPoint = (rootPos + targetPos) * 0.5f;
            Vector3 toMid = currentMidPos - midPoint;
            toMid = KinetixMath.ProjectOnPlane(toMid, chainDir);

            if (toMid.LengthSquared() < 0.0001f)
            {
                Vector3 perp = chainDir.Cross(Vector3.Up);
                if (perp.LengthSquared() < 0.0001f)
                    perp = chainDir.Cross(Vector3.Right);
                return perp.Normalized();
            }

            return toMid.Normalized();
        }
    }

    private void ApplyTwoBoneRotations(int rootBone, int midBone, int tipBone,
                                       Vector3 rootPos, Vector3 targetPos,
                                       Vector3 poleDir, float midAngle)
    {
        var skeleton = Chain.Skeleton;
        Vector3 toTarget = (targetPos - rootPos).Normalized();

        // Root bone: point towards target with pole twist
        Transform3D rootGlobal = skeleton.GetBoneGlobalPose(rootBone);
        Vector3 rootForward = rootGlobal.Basis.Z.Normalized();
        Quaternion alignRot = KinetixMath.FromToRotation(rootForward, toTarget);

        // Apply pole twist
        Vector3 currentRight = rootGlobal.Basis.X.Normalized();
        Vector3 desiredRight = toTarget.Cross(poleDir).Normalized();

        if (desiredRight.LengthSquared() > 0.0001f)
        {
            Vector3 alignedRight = alignRot * currentRight;
            Quaternion twistRot = KinetixMath.FromToRotation(alignedRight, desiredRight);
            alignRot = twistRot * alignRot;
        }

        Quaternion parentRot = Quaternion.Identity;
        int parentBone = skeleton.GetBoneParent(rootBone);
        if (parentBone != -1)
            parentRot = skeleton.GetBoneGlobalPose(parentBone).Basis.GetRotationQuaternion();

        Quaternion newGlobalRot = alignRot * rootGlobal.Basis.GetRotationQuaternion();
        Quaternion newPoseRot = parentRot.Inverse() * newGlobalRot;
        Chain.SetBoneRotation(0, newPoseRot);

        // Mid bone: apply bend angle
        Transform3D midGlobal = skeleton.GetBoneGlobalPose(midBone);
        Vector3 bendAxis = toTarget.Cross(poleDir).Normalized();
        Quaternion bendRot = new Quaternion(bendAxis, Mathf.Pi - midAngle);

        Quaternion midParentRot = skeleton.GetBoneGlobalPose(rootBone).Basis.GetRotationQuaternion();
        Quaternion newMidGlobalRot = bendRot * midGlobal.Basis.GetRotationQuaternion();
        Quaternion newMidPoseRot = midParentRot.Inverse() * newMidGlobalRot;
        Chain.SetBoneRotation(1, newMidPoseRot);

        // Tip bone: optional rotation constraint
        if (Target.Rotation.HasValue)
        {
            Quaternion tipParentRot = skeleton.GetBoneGlobalPose(midBone).Basis.GetRotationQuaternion();
            Quaternion newTipPoseRot = tipParentRot.Inverse() * Target.Rotation.Value;
            Chain.SetBoneRotation(2, newTipPoseRot);
        }
    }
}
