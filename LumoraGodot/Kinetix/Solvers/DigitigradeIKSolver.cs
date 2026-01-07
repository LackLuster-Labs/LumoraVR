using Aquamarine.Kinetix.Core;
using Godot;

namespace Aquamarine.Kinetix.Solvers;

// Digitigrade IK: 4-bone chain for animal legs (hip → thigh → shin → foot)
public class DigitigradeIKSolver : IKSolver
{
    public float FootBendRatio { get; set; } = 0.7f;

    public override bool Solve()
    {
        if (!IsValid()) return false;

        if (Chain.Bones.Length != 4)
        {
            GD.PushError($"DigitigradeIK needs 4 bones, got {Chain.Bones.Length}");
            return false;
        }

        // Split into two problems: main leg (hip→shin) + foot extension (shin→toe)
        var mainChain = new IKChain
        {
            Skeleton = Chain.Skeleton,
            Bones = new[] { Chain.Bones[0], Chain.Bones[1], Chain.Bones[2] }
        };
        mainChain.CacheBoneLengths();

        Vector3 shinTarget = CalculateShinTarget();

        var mainSolver = new TwoBoneIKSolver
        {
            Chain = mainChain,
            Target = new IKTarget
            {
                Position = shinTarget,
                PolePosition = Target.PolePosition,
                PoleTwist = Target.PoleTwist
            },
            MaxIterations = MaxIterations,
            Tolerance = Tolerance
        };

        bool mainSolved = mainSolver.Solve();
        SolveFootExtension();

        return mainSolved && GetDistanceToTarget() < Tolerance;
    }

    private Vector3 CalculateShinTarget()
    {
        Vector3 toeTarget = Target.Position;
        float footLength = Chain.BoneLengths[3];
        Vector3 footDir = GetFootDirection();
        return toeTarget - footDir * footLength * FootBendRatio;
    }

    private Vector3 GetFootDirection()
    {
        Vector3 currentShinPos = Chain.GetBoneGlobalPosition(2);
        Vector3 currentFootPos = Chain.GetBoneGlobalPosition(3);
        Vector3 currentFootDir = (currentFootPos - currentShinPos).Normalized();

        // Blend with default (slightly forward and down)
        Vector3 defaultDir = new Vector3(0.5f, -0.7f, 0).Normalized();
        return currentFootDir.Lerp(defaultDir, 0.3f).Normalized();
    }

    private void SolveFootExtension()
    {
        int shinBone = Chain.Bones[2];
        int footBone = Chain.Bones[3];

        Vector3 shinPos = Chain.GetBoneGlobalPosition(2);
        Vector3 toeTarget = Target.Position;
        Vector3 toTarget = (toeTarget - shinPos).Normalized();

        Transform3D footGlobal = Chain.Skeleton.GetBoneGlobalPose(footBone);
        Vector3 footForward = footGlobal.Basis.Z.Normalized();
        Quaternion rotation = KinetixMath.FromToRotation(footForward, toTarget);

        Quaternion footParentRot = Chain.Skeleton.GetBoneGlobalPose(shinBone).Basis.GetRotationQuaternion();
        Quaternion newFootGlobalRot = rotation * footGlobal.Basis.GetRotationQuaternion();
        Quaternion newFootPoseRot = footParentRot.Inverse() * newFootGlobalRot;

        Chain.SetBoneRotation(3, newFootPoseRot);

        if (Target.Rotation.HasValue)
        {
            Quaternion tipPoseRot = footParentRot.Inverse() * Target.Rotation.Value;
            Chain.SetBoneRotation(3, tipPoseRot);
        }
    }
}
