using Godot;
using Lumora.Core.Components;
using Lumora.Core.Math;
using Aquamarine.Kinetix.Components;
using System.Collections.Generic;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// Hook for KinetixVRIK component → Kinetix IK solvers.
/// Creates and manages Kinetix TwoBoneIK instances for full-body IK.
/// Architecture:
///   - Creates Skeleton3D from SkeletonBuilder
///   - Creates TwoBoneIK solvers for arms and legs
///   - Updates IK targets from tracking data
///   - Solves IK every frame
///   - Syncs results back to slots via SkeletonHook
/// </summary>
public class KinetixVRIKHook : ComponentHook<KinetixVRIK>
{
    // ===== IK SOLVERS =====

    private KinetixTwoBoneIK _leftArmIK;
    private KinetixTwoBoneIK _rightArmIK;
    private KinetixTwoBoneIK _leftLegIK;
    private KinetixTwoBoneIK _rightLegIK;

    // ===== IK TARGETS =====

    private Node3D _headTarget;
    private Node3D _leftHandTarget;
    private Node3D _rightHandTarget;
    private Node3D _leftFootTarget;
    private Node3D _rightFootTarget;

    // ===== POLE TARGETS (for elbow/knee bending) =====

    private Node3D _leftElbowPole;
    private Node3D _rightElbowPole;
    private Node3D _leftKneePole;
    private Node3D _rightKneePole;

    // ===== SKELETON REFERENCE =====

    private Skeleton3D _skeleton;
    private SkeletonHook _skeletonHook;

    // ===== INITIALIZATION STATE =====

    private bool _isInitialized = false;
    private bool _ikSetupComplete = false;

    public override void Initialize()
    {
        base.Initialize();

        AquaLogger.Log($"KinetixVRIKHook: Initializing for slot '{Owner.Slot.SlotName.Value}'");

        TryResolveSkeleton();

        // Defer full setup until skeleton exists
        if (_skeleton == null)
        {
            AquaLogger.Warn("KinetixVRIKHook: Skeleton not ready yet, will retry in ApplyChanges");
            return;
        }

        CreateIKTargets();
        SetupIKSolvers();
        _isInitialized = true;

        AquaLogger.Log($"KinetixVRIKHook: Initialized successfully");
    }

    // ApplyChanges call counter for logging
    private int _applyChangesCallCount = 0;

    public override void ApplyChanges()
    {
        _applyChangesCallCount++;
        bool shouldLogApply = _applyChangesCallCount == 1 || _applyChangesCallCount % 60 == 0;

        if (shouldLogApply)
        {
            AquaLogger.Log($"KinetixVRIKHook: ApplyChanges called (#{_applyChangesCallCount}), skeleton={_skeleton != null}, ikSetup={_ikSetupComplete}");
        }

        if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton))
        {
            TryResolveSkeleton();
        }

        if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton))
            return;

        // If we deferred initialization until the skeleton existed, do it now
        if (!_isInitialized && _skeleton.GetBoneCount() > 0)
        {
            AquaLogger.Log($"KinetixVRIKHook: Late init starting - creating IK targets and solvers");
            CreateIKTargets();
            SetupIKSolvers();
            _isInitialized = true;
            AquaLogger.Log("KinetixVRIKHook: Late initialization complete");
        }

        // Setup IK if not done yet (needs to wait for skeleton to be built)
        if (!_ikSetupComplete && _skeleton.GetBoneCount() > 0)
        {
            AquaLogger.Log($"KinetixVRIKHook: Attempting IK setup with {_skeleton.GetBoneCount()} bones");
            bool success = SetupIKSolvers();
            if (success)
            {
                _ikSetupComplete = true;
                AquaLogger.Log("KinetixVRIKHook: IK setup complete - avatar should now track!");
            }
            else
            {
                AquaLogger.Warn("KinetixVRIKHook: IK setup failed - check bone names");
            }
        }

        if (!_ikSetupComplete || !Owner.Enabled.Value)
            return;

        // Update IK targets from tracking
        UpdateIKTargets();

        // Solve IK (happens in KinetixTwoBoneIK._Process automatically)
        // The IK solvers will update bone poses in the Skeleton3D
    }

    /// <summary>
    /// Create Node3D targets for IK.
    /// </summary>
    private void CreateIKTargets()
    {
        // Create target container
        var targetContainer = new Node3D();
        targetContainer.Name = "IK_Targets";
        attachedNode.AddChild(targetContainer);

        // Head target
        _headTarget = new Node3D();
        _headTarget.Name = "HeadTarget";
        targetContainer.AddChild(_headTarget);

        // Hand targets
        _leftHandTarget = new Node3D();
        _leftHandTarget.Name = "LeftHandTarget";
        targetContainer.AddChild(_leftHandTarget);

        _rightHandTarget = new Node3D();
        _rightHandTarget.Name = "RightHandTarget";
        targetContainer.AddChild(_rightHandTarget);

        // Foot targets
        _leftFootTarget = new Node3D();
        _leftFootTarget.Name = "LeftFootTarget";
        targetContainer.AddChild(_leftFootTarget);

        _rightFootTarget = new Node3D();
        _rightFootTarget.Name = "RightFootTarget";
        targetContainer.AddChild(_rightFootTarget);

        // Pole targets (for elbow/knee bending direction)
        _leftElbowPole = new Node3D();
        _leftElbowPole.Name = "LeftElbowPole";
        targetContainer.AddChild(_leftElbowPole);

        _rightElbowPole = new Node3D();
        _rightElbowPole.Name = "RightElbowPole";
        targetContainer.AddChild(_rightElbowPole);

        _leftKneePole = new Node3D();
        _leftKneePole.Name = "LeftKneePole";
        targetContainer.AddChild(_leftKneePole);

        _rightKneePole = new Node3D();
        _rightKneePole.Name = "RightKneePole";
        targetContainer.AddChild(_rightKneePole);

        AquaLogger.Log("KinetixVRIKHook: Created IK targets");
    }

    /// <summary>
    /// Setup TwoBoneIK solvers for arms and legs.
    /// </summary>
    private bool SetupIKSolvers()
    {
        if (_skeleton == null || _skeleton.GetBoneCount() == 0)
        {
            AquaLogger.Warn("KinetixVRIKHook: Skeleton not ready, deferring IK setup");
            return false;
        }

        // Create IK container
        var ikContainer = new Node3D();
        ikContainer.Name = "IK_Solvers";
        attachedNode.AddChild(ikContainer);

        // Left arm: LeftShoulder -> LeftUpperArm -> LeftLowerArm -> LeftHand
        _leftArmIK = CreateArmIK("LeftHand", _leftHandTarget, _leftElbowPole, ikContainer);

        // Right arm: RightShoulder -> RightUpperArm -> RightLowerArm -> RightHand
        _rightArmIK = CreateArmIK("RightHand", _rightHandTarget, _rightElbowPole, ikContainer);

        // Left leg: LeftUpperLeg -> LeftLowerLeg -> LeftFoot
        _leftLegIK = CreateLegIK("LeftFoot", _leftFootTarget, _leftKneePole, ikContainer);

        // Right leg: RightUpperLeg -> RightLowerLeg -> RightFoot
        _rightLegIK = CreateLegIK("RightFoot", _rightFootTarget, _rightKneePole, ikContainer);

        bool allCreated = _leftArmIK != null && _rightArmIK != null && _leftLegIK != null && _rightLegIK != null;

        if (allCreated)
        {
            AquaLogger.Log("KinetixVRIKHook: All IK solvers created successfully");
        }
        else
        {
            AquaLogger.Warn($"KinetixVRIKHook: Some IK solvers failed to create (L-Arm:{_leftArmIK != null}, R-Arm:{_rightArmIK != null}, L-Leg:{_leftLegIK != null}, R-Leg:{_rightLegIK != null})");
        }

        return allCreated;
    }

    /// <summary>
    /// Create a TwoBoneIK solver for an arm.
    /// </summary>
    private KinetixTwoBoneIK CreateArmIK(string handBoneName, Node3D target, Node3D poleTarget, Node parent)
    {
        if (_skeleton.FindBone(handBoneName) == -1)
        {
            AquaLogger.Warn($"KinetixVRIKHook: Bone '{handBoneName}' not found in skeleton");
            return null;
        }

        var ik = new KinetixTwoBoneIK();
        ik.Name = $"IK_{handBoneName}";
        ik.Skeleton = _skeleton;
        ik.Target = target;
        ik.PoleTarget = poleTarget;
        ik.TipBoneName = handBoneName;
        ik.AutoSolve = true;
        ik.ProcessPriority = 1;
        parent.AddChild(ik);

        AquaLogger.Log($"KinetixVRIKHook: Created arm IK for '{handBoneName}'");
        return ik;
    }

    /// <summary>
    /// Create a TwoBoneIK solver for a leg.
    /// </summary>
    private KinetixTwoBoneIK CreateLegIK(string footBoneName, Node3D target, Node3D poleTarget, Node parent)
    {
        if (_skeleton.FindBone(footBoneName) == -1)
        {
            AquaLogger.Warn($"KinetixVRIKHook: Bone '{footBoneName}' not found in skeleton");
            return null;
        }

        var ik = new KinetixTwoBoneIK();
        ik.Name = $"IK_{footBoneName}";
        ik.Skeleton = _skeleton;
        ik.Target = target;
        ik.PoleTarget = poleTarget;
        ik.TipBoneName = footBoneName;
        ik.AutoSolve = true;
        ik.ProcessPriority = 1;
        parent.AddChild(ik);

        AquaLogger.Log($"KinetixVRIKHook: Created leg IK for '{footBoneName}'");
        return ik;
    }

    // Debug: Only log once per second to avoid spam
    private float _lastLogTime = 0;
    private const float LOG_INTERVAL = 1.0f;

    /// <summary>
    /// Update IK target positions from tracking data.
    /// </summary>
    private void UpdateIKTargets()
    {
        bool shouldLog = (Time.GetTicksMsec() / 1000f) - _lastLogTime > LOG_INTERVAL;
        if (shouldLog)
        {
            _lastLogTime = Time.GetTicksMsec() / 1000f;
        }

        // Update head target
        if (_headTarget != null && Owner.HeadTarget.Target != null)
        {
            var headPos = Owner.GetHeadTargetPosition();
            var headRot = Owner.GetHeadTargetRotation();
            _headTarget.GlobalPosition = new Vector3(headPos.x, headPos.y, headPos.z);
            _headTarget.GlobalBasis = new Basis(new Quaternion(headRot.x, headRot.y, headRot.z, headRot.w));

            if (shouldLog)
            {
                AquaLogger.Log($"KinetixVRIKHook: HeadTarget pos={headPos} slot={Owner.HeadTarget.Target.SlotName.Value}");
            }
        }

        // Update hand targets
        if (_leftHandTarget != null && Owner.LeftHandTarget.Target != null)
        {
            var pos = Owner.GetLeftHandTargetPosition();
            var rot = Owner.GetLeftHandTargetRotation();
            _leftHandTarget.GlobalPosition = new Vector3(pos.x, pos.y, pos.z);
            _leftHandTarget.GlobalBasis = new Basis(new Quaternion(rot.x, rot.y, rot.z, rot.w));

            // Update elbow pole (position it behind and to the left of hand)
            if (_leftElbowPole != null)
            {
                _leftElbowPole.GlobalPosition = _leftHandTarget.GlobalPosition + new Vector3(-0.2f, 0f, -0.3f);
            }

            if (shouldLog)
            {
                AquaLogger.Log($"KinetixVRIKHook: LeftHandTarget pos={pos} slot={Owner.LeftHandTarget.Target.SlotName.Value}");
            }
        }

        if (_rightHandTarget != null && Owner.RightHandTarget.Target != null)
        {
            var pos = Owner.GetRightHandTargetPosition();
            var rot = Owner.GetRightHandTargetRotation();
            _rightHandTarget.GlobalPosition = new Vector3(pos.x, pos.y, pos.z);
            _rightHandTarget.GlobalBasis = new Basis(new Quaternion(rot.x, rot.y, rot.z, rot.w));

            // Update elbow pole (position it behind and to the right of hand)
            if (_rightElbowPole != null)
            {
                _rightElbowPole.GlobalPosition = _rightHandTarget.GlobalPosition + new Vector3(0.2f, 0f, -0.3f);
            }

            if (shouldLog)
            {
                AquaLogger.Log($"KinetixVRIKHook: RightHandTarget pos={pos} slot={Owner.RightHandTarget.Target.SlotName.Value}");
            }
        }

        // Update foot targets
        if (_leftFootTarget != null)
        {
            var pos = Owner.GetLeftFootTargetPosition();
            _leftFootTarget.GlobalPosition = new Vector3(pos.x, pos.y, pos.z);

            // Update knee pole (position it forward of foot)
            if (_leftKneePole != null)
            {
                _leftKneePole.GlobalPosition = _leftFootTarget.GlobalPosition + new Vector3(0f, 0.5f, 0.5f);
            }
        }

        if (_rightFootTarget != null)
        {
            var pos = Owner.GetRightFootTargetPosition();
            _rightFootTarget.GlobalPosition = new Vector3(pos.x, pos.y, pos.z);

            // Update knee pole (position it forward of foot)
            if (_rightKneePole != null)
            {
                _rightKneePole.GlobalPosition = _rightFootTarget.GlobalPosition + new Vector3(0f, 0.5f, 0.5f);
            }
        }
    }

    // Throttle skeleton search logging
    private int _skeletonSearchAttempts = 0;

    private void TryResolveSkeleton()
    {
        _skeletonSearchAttempts++;
        bool shouldLog = _skeletonSearchAttempts <= 5 || _skeletonSearchAttempts % 60 == 0; // Log first 5, then every 60 frames

        // Strategy 1: Get skeleton from SkeletonBuilder component hook
        if (Owner.Skeleton.Target != null)
        {
            if (shouldLog) AquaLogger.Log($"KinetixVRIKHook: Strategy 1 - SkeletonBuilder exists, checking hook...");
            _skeletonHook = Owner.Skeleton.Target.Hook as SkeletonHook;
            if (_skeletonHook != null)
            {
                _skeleton = _skeletonHook.GetSkeleton();
                if (_skeleton != null && GodotObject.IsInstanceValid(_skeleton) && _skeleton.GetBoneCount() > 0)
                {
                    AquaLogger.Log($"KinetixVRIKHook: Found skeleton via SkeletonBuilder hook with {_skeleton.GetBoneCount()} bones");
                    LogSkeletonBones();
                    return;
                }
                else if (shouldLog)
                {
                    AquaLogger.Log($"KinetixVRIKHook: Strategy 1 failed - skeleton={_skeleton != null}, valid={_skeleton != null && GodotObject.IsInstanceValid(_skeleton)}, bones={_skeleton?.GetBoneCount() ?? 0}");
                }
            }
            else if (shouldLog)
            {
                AquaLogger.Log($"KinetixVRIKHook: Strategy 1 failed - SkeletonHook is null. Hook type: {Owner.Skeleton.Target.Hook?.GetType().Name ?? "null"}");
            }
        }
        else if (shouldLog)
        {
            AquaLogger.Log($"KinetixVRIKHook: Strategy 1 skipped - Owner.Skeleton.Target is null");
        }

        // Strategy 2: Search under the attached node hierarchy
        if (shouldLog) AquaLogger.Log($"KinetixVRIKHook: Strategy 2 - Searching under attachedNode '{attachedNode?.Name}'...");
        _skeleton = FindSkeletonNode(attachedNode);
        if (_skeleton != null && GodotObject.IsInstanceValid(_skeleton) && _skeleton.GetBoneCount() > 0)
        {
            AquaLogger.Log($"KinetixVRIKHook: Found skeleton under attachedNode with {_skeleton.GetBoneCount()} bones");
            LogSkeletonBones();
            return;
        }

        // Strategy 3: Search parent hierarchy (the skeleton might be a sibling)
        if (shouldLog) AquaLogger.Log($"KinetixVRIKHook: Strategy 3 - Searching parent hierarchy...");
        var parent = attachedNode?.GetParent();
        while (parent != null)
        {
            _skeleton = FindSkeletonNode(parent);
            if (_skeleton != null && GodotObject.IsInstanceValid(_skeleton) && _skeleton.GetBoneCount() > 0)
            {
                AquaLogger.Log($"KinetixVRIKHook: Found skeleton in parent hierarchy with {_skeleton.GetBoneCount()} bones");
                LogSkeletonBones();
                return;
            }
            parent = parent.GetParent();
        }

        // Strategy 4: Search the entire UserRoot slot hierarchy
        if (Owner.UserRoot.Target != null && Owner.UserRoot.Target.Slot?.Hook is SlotHook userRootHook)
        {
            if (shouldLog) AquaLogger.Log($"KinetixVRIKHook: Strategy 4 - Searching under UserRoot...");
            var userRootNode = userRootHook.ForceGetNode3D();
            if (userRootNode != null)
            {
                _skeleton = FindSkeletonNode(userRootNode);
                if (_skeleton != null && GodotObject.IsInstanceValid(_skeleton) && _skeleton.GetBoneCount() > 0)
                {
                    AquaLogger.Log($"KinetixVRIKHook: Found skeleton under UserRoot with {_skeleton.GetBoneCount()} bones");
                    LogSkeletonBones();
                    return;
                }
            }
        }

        // Strategy 5: GLOBAL SEARCH - Search entire scene tree from root
        if (shouldLog) AquaLogger.Log($"KinetixVRIKHook: Strategy 5 - GLOBAL scene tree search...");
        var sceneRoot = attachedNode?.GetTree()?.Root;
        if (sceneRoot != null)
        {
            _skeleton = FindSkeletonNode(sceneRoot);
            if (_skeleton != null && GodotObject.IsInstanceValid(_skeleton) && _skeleton.GetBoneCount() > 0)
            {
                AquaLogger.Log($"KinetixVRIKHook: Found skeleton via GLOBAL search with {_skeleton.GetBoneCount()} bones at path: {_skeleton.GetPath()}");
                LogSkeletonBones();
                return;
            }
        }

        if (shouldLog)
        {
            AquaLogger.Warn($"KinetixVRIKHook: Could not find Skeleton3D after {_skeletonSearchAttempts} attempts. " +
                $"SkeletonBuilder={Owner.Skeleton.Target != null}, SkeletonHook={_skeletonHook != null}, " +
                $"attachedNode={attachedNode?.Name ?? "null"}, sceneRoot={sceneRoot?.Name ?? "null"}");
        }
    }

    private void LogSkeletonBones()
    {
        if (_skeleton == null)
            return;

        var boneNames = new List<string>();
        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            boneNames.Add(_skeleton.GetBoneName(i));
        }
        AquaLogger.Log($"KinetixVRIKHook: Skeleton bones: {string.Join(", ", boneNames)}");

        // Log bone indices for IK targets
        int leftHand = _skeleton.FindBone("LeftHand");
        int rightHand = _skeleton.FindBone("RightHand");
        int leftFoot = _skeleton.FindBone("LeftFoot");
        int rightFoot = _skeleton.FindBone("RightFoot");
        AquaLogger.Log($"KinetixVRIKHook: IK bone indices - LeftHand:{leftHand}, RightHand:{rightHand}, LeftFoot:{leftFoot}, RightFoot:{rightFoot}");
    }

    private Skeleton3D FindSkeletonNode(Node node)
    {
        if (node == null)
            return null;

        if (node is Skeleton3D sk && sk.GetBoneCount() > 0)
            return sk;

        foreach (Node child in node.GetChildren())
        {
            var found = FindSkeletonNode(child);
            if (found != null)
                return found;
        }
        return null;
    }

    public override void Destroy(bool destroyingWorld)
    {
        // Clean up IK solvers
        if (!destroyingWorld)
        {
            if (_leftArmIK != null && GodotObject.IsInstanceValid(_leftArmIK))
                _leftArmIK.QueueFree();
            if (_rightArmIK != null && GodotObject.IsInstanceValid(_rightArmIK))
                _rightArmIK.QueueFree();
            if (_leftLegIK != null && GodotObject.IsInstanceValid(_leftLegIK))
                _leftLegIK.QueueFree();
            if (_rightLegIK != null && GodotObject.IsInstanceValid(_rightLegIK))
                _rightLegIK.QueueFree();

            // Targets will be cleaned up automatically as children of attachedNode
        }

        _leftArmIK = null;
        _rightArmIK = null;
        _leftLegIK = null;
        _rightLegIK = null;

        _headTarget = null;
        _leftHandTarget = null;
        _rightHandTarget = null;
        _leftFootTarget = null;
        _rightFootTarget = null;

        _leftElbowPole = null;
        _rightElbowPole = null;
        _leftKneePole = null;
        _rightKneePole = null;

        _skeleton = null;
        _skeletonHook = null;
        _isInitialized = false;
        _ikSetupComplete = false;

        base.Destroy(destroyingWorld);
    }
}
