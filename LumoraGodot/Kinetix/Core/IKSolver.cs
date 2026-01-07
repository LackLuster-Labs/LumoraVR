using Godot;

namespace Aquamarine.Kinetix.Core;

public abstract class IKSolver
{
    public IKChain Chain { get; set; }
    public IKTarget Target { get; set; }
    public int MaxIterations { get; set; } = 10;
    public float Tolerance { get; set; } = 0.001f;

    public abstract bool Solve();

    protected bool IsTargetReachable()
    {
        if (Chain == null || Target == null || !Chain.IsValid())
            return false;

        Vector3 rootPos = Chain.GetBoneGlobalPosition(0);
        float dist = rootPos.DistanceTo(Target.Position);
        return dist <= Chain.TotalLength;
    }

    protected float GetDistanceToTarget()
    {
        if (Chain == null || Target == null || !Chain.IsValid())
            return float.MaxValue;

        int tipIdx = Chain.Bones.Length - 1;
        Vector3 tipPos = Chain.GetBoneGlobalPosition(tipIdx);
        return tipPos.DistanceTo(Target.Position);
    }

    protected bool IsValid()
    {
        return Chain != null && Chain.IsValid() && Target != null;
    }
}
