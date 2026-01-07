using Godot;
using Lumora.Core;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// Hook for GodotIKAvatar component → Godot native IK system.
/// Uses SkeletonIK3D for inverse kinematics solving.
/// Updates IK targets from tracking data each frame.
/// </summary>
public class GodotIKAvatarHook : ComponentHook<GodotIKAvatar>
{
    // IK solvers for each limb (using deprecated but still functional SkeletonIK3D)
#pragma warning disable CS0618 // SkeletonIK3D is deprecated but still works
    private SkeletonIK3D _leftArmIK;
    private SkeletonIK3D _rightArmIK;
    private SkeletonIK3D _leftLegIK;
    private SkeletonIK3D _rightLegIK;
#pragma warning restore CS0618

    // IK target nodes
    private Node3D _leftHandTarget;
    private Node3D _rightHandTarget;
    private Node3D _leftFootTarget;
    private Node3D _rightFootTarget;

    // Skeleton reference
    private Skeleton3D _skeleton;
    private SkeletonHook _skeletonHook;
    private bool _ikSetup;

    public override void Initialize()
    {
        base.Initialize();
        AquaLogger.Log($"GodotIKAvatarHook: Initialized for '{Owner.Slot.SlotName.Value}'");
    }

    public override void ApplyChanges()
    {
        if (!Owner.Enabled.Value)
            return;

        // Try to setup IK if not done yet
        if (!_ikSetup)
        {
            TrySetupIK();
        }

        if (!_ikSetup || _skeleton == null)
            return;

        // Update IK targets from Lumora tracking
        UpdateIKTargets();
    }

    /// <summary>
    /// Try to setup the IK system once skeleton is ready.
    /// </summary>
    private void TrySetupIK()
    {
        // Get skeleton from GodotIKAvatar's skeleton reference
        var skeletonBuilder = Owner.Skeleton.Target;
        if (skeletonBuilder == null || !skeletonBuilder.IsBuilt.Value)
        {
            return;
        }

        // Get the SkeletonHook to access Godot's Skeleton3D
        _skeletonHook = skeletonBuilder.Hook as SkeletonHook;
        if (_skeletonHook == null)
        {
            return;
        }

        _skeleton = _skeletonHook.GetSkeleton();
        if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton))
        {
            return;
        }

        if (_skeleton.GetBoneCount() == 0)
        {
            return;
        }

        AquaLogger.Log($"GodotIKAvatarHook: Setting up IK with skeleton '{_skeleton.Name}' ({_skeleton.GetBoneCount()} bones)");

        // Create IK target nodes
        CreateIKTargets();

        // Setup IK solvers for each limb
        SetupArmIK("Left", ref _leftArmIK, _leftHandTarget);
        SetupArmIK("Right", ref _rightArmIK, _rightHandTarget);
        SetupLegIK("Left", ref _leftLegIK, _leftFootTarget);
        SetupLegIK("Right", ref _rightLegIK, _rightFootTarget);

        _ikSetup = true;
        AquaLogger.Log("GodotIKAvatarHook: IK setup complete");
    }

    /// <summary>
    /// Create Node3D targets for IK end effectors.
    /// </summary>
    private void CreateIKTargets()
    {
        // Create targets as children of skeleton's parent (world space)
        Node parent = _skeleton.GetParent() ?? _skeleton;

        _leftHandTarget = new Node3D { Name = "LeftHandIKTarget" };
        parent.AddChild(_leftHandTarget);

        _rightHandTarget = new Node3D { Name = "RightHandIKTarget" };
        parent.AddChild(_rightHandTarget);

        _leftFootTarget = new Node3D { Name = "LeftFootIKTarget" };
        parent.AddChild(_leftFootTarget);

        _rightFootTarget = new Node3D { Name = "RightFootIKTarget" };
        parent.AddChild(_rightFootTarget);

        AquaLogger.Log("GodotIKAvatarHook: Created IK target nodes");
    }

    /// <summary>
    /// Setup IK solver for an arm.
    /// </summary>
#pragma warning disable CS0618
    private void SetupArmIK(string side, ref SkeletonIK3D ik, Node3D target)
    {
        string upperArm = $"{side}UpperArm";
        string hand = $"{side}Hand";

        int upperArmIdx = _skeleton.FindBone(upperArm);
        int handIdx = _skeleton.FindBone(hand);

        if (upperArmIdx < 0 || handIdx < 0)
        {
            AquaLogger.Warn($"GodotIKAvatarHook: Could not find bones for {side} arm IK (upperArm={upperArmIdx}, hand={handIdx})");
            return;
        }

        ik = new SkeletonIK3D();
        ik.Name = $"{side}ArmIK";
        ik.RootBone = upperArm;
        ik.TipBone = hand;
        ik.OverrideTipBasis = true;
        ik.Influence = 1.0f;
        ik.MaxIterations = 10;

        _skeleton.AddChild(ik);

        // Set target node after adding to tree
        ik.SetTargetNode(target.GetPath());
        ik.Start();

        AquaLogger.Log($"GodotIKAvatarHook: Setup {side} arm IK ({upperArm} → {hand})");
    }

    /// <summary>
    /// Setup IK solver for a leg.
    /// </summary>
    private void SetupLegIK(string side, ref SkeletonIK3D ik, Node3D target)
    {
        string upperLeg = $"{side}UpperLeg";
        string foot = $"{side}Foot";

        int upperLegIdx = _skeleton.FindBone(upperLeg);
        int footIdx = _skeleton.FindBone(foot);

        if (upperLegIdx < 0 || footIdx < 0)
        {
            AquaLogger.Warn($"GodotIKAvatarHook: Could not find bones for {side} leg IK (upperLeg={upperLegIdx}, foot={footIdx})");
            return;
        }

        ik = new SkeletonIK3D();
        ik.Name = $"{side}LegIK";
        ik.RootBone = upperLeg;
        ik.TipBone = foot;
        ik.OverrideTipBasis = true;
        ik.Influence = 1.0f;
        ik.MaxIterations = 10;

        _skeleton.AddChild(ik);

        // Set target node after adding to tree
        ik.SetTargetNode(target.GetPath());
        ik.Start();

        AquaLogger.Log($"GodotIKAvatarHook: Setup {side} leg IK ({upperLeg} → {foot})");
    }
#pragma warning restore CS0618

    /// <summary>
    /// Update IK targets from Lumora tracking slots.
    /// </summary>
    private void UpdateIKTargets()
    {
        // Update hand targets
        if (_leftHandTarget != null && GodotObject.IsInstanceValid(_leftHandTarget))
        {
            float3 pos = Owner.GetLeftHandTargetPosition();
            floatQ rot = Owner.GetLeftHandTargetRotation();
            _leftHandTarget.GlobalPosition = new Vector3(pos.x, pos.y, pos.z);
            _leftHandTarget.Quaternion = new Quaternion(rot.x, rot.y, rot.z, rot.w);
        }

        if (_rightHandTarget != null && GodotObject.IsInstanceValid(_rightHandTarget))
        {
            float3 pos = Owner.GetRightHandTargetPosition();
            floatQ rot = Owner.GetRightHandTargetRotation();
            _rightHandTarget.GlobalPosition = new Vector3(pos.x, pos.y, pos.z);
            _rightHandTarget.Quaternion = new Quaternion(rot.x, rot.y, rot.z, rot.w);
        }

        // Update foot targets
        if (_leftFootTarget != null && GodotObject.IsInstanceValid(_leftFootTarget))
        {
            float3 pos = Owner.GetLeftFootTargetPosition();
            _leftFootTarget.GlobalPosition = new Vector3(pos.x, pos.y, pos.z);
        }

        if (_rightFootTarget != null && GodotObject.IsInstanceValid(_rightFootTarget))
        {
            float3 pos = Owner.GetRightFootTargetPosition();
            _rightFootTarget.GlobalPosition = new Vector3(pos.x, pos.y, pos.z);
        }
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld)
        {
            // Stop and cleanup IK solvers
#pragma warning disable CS0618
            StopAndFreeIK(ref _leftArmIK);
            StopAndFreeIK(ref _rightArmIK);
            StopAndFreeIK(ref _leftLegIK);
            StopAndFreeIK(ref _rightLegIK);
#pragma warning restore CS0618

            // Cleanup targets
            FreeNode(ref _leftHandTarget);
            FreeNode(ref _rightHandTarget);
            FreeNode(ref _leftFootTarget);
            FreeNode(ref _rightFootTarget);
        }

        _skeleton = null;
        _skeletonHook = null;
        _ikSetup = false;

        base.Destroy(destroyingWorld);
    }

#pragma warning disable CS0618
    private void StopAndFreeIK(ref SkeletonIK3D ik)
    {
        if (ik != null && GodotObject.IsInstanceValid(ik))
        {
            ik.Stop();
            ik.QueueFree();
        }
        ik = null;
    }
#pragma warning restore CS0618

    private void FreeNode(ref Node3D node)
    {
        if (node != null && GodotObject.IsInstanceValid(node))
        {
            node.QueueFree();
        }
        node = null;
    }
}
