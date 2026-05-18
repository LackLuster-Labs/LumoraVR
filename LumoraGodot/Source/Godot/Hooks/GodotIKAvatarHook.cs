// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Godot;
using Lumora.Core;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

/// <summary>
/// Hook for GodotIKAvatar component -> Godot native IK system.
/// Uses SkeletonIK3D for inverse kinematics solving.
/// Updates IK targets from tracking data each frame.
/// </summary>
public class GodotIKAvatarHook : ComponentHook<GodotIKAvatar>
{
    // IK solvers for each limb (using deprecated but still functional SkeletonIK3D)
#pragma warning disable CS0618
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
        LumoraLogger.Log($"GodotIKAvatarHook: Initialized for '{Owner.Slot.SlotName.Value}'");
    }

    public override void ApplyChanges()
    {
        if (!Owner.Enabled.Value)
            return;

        if (!_ikSetup)
        {
            TrySetupIK();
        }

        if (!_ikSetup || _skeleton == null)
            return;

        UpdateIKTargets();
    }

    /// <summary>
    /// Try to setup the IK system once skeleton is ready.
    /// </summary>
    private void TrySetupIK()
    {
        if (!TryResolveSkeleton(out var skeleton))
        {
            return;
        }

        _skeleton = skeleton;
        if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton))
        {
            return;
        }

        if (_skeleton.GetBoneCount() == 0)
        {
            return;
        }

        LumoraLogger.Log($"GodotIKAvatarHook: Setting up IK with skeleton '{_skeleton.Name}' ({_skeleton.GetBoneCount()} bones)");

        CreateIKTargets();

        bool anySolver =
            SetupArmIK("Left", ref _leftArmIK, _leftHandTarget) |
            SetupArmIK("Right", ref _rightArmIK, _rightHandTarget) |
            SetupLegIK("Left", ref _leftLegIK, _leftFootTarget) |
            SetupLegIK("Right", ref _rightLegIK, _rightFootTarget);

        if (!anySolver)
        {
            LumoraLogger.Warn("GodotIKAvatarHook: Skeleton found but no IK limbs could be mapped");
        }

        // Mark setup complete even if partial, avoids per-frame warning spam.
        _ikSetup = true;
        LumoraLogger.Log("GodotIKAvatarHook: IK setup complete");
    }

    private bool TryResolveSkeleton(out Skeleton3D skeleton)
    {
        skeleton = null;
        _skeletonHook = null;

        // Preferred path: Lumora skeleton builder.
        var skeletonBuilder = Owner.Skeleton.Target;
        if (skeletonBuilder != null && skeletonBuilder.IsBuilt.Value)
        {
            _skeletonHook = skeletonBuilder.Hook as SkeletonHook;
            skeleton = _skeletonHook?.GetSkeleton();
            if (skeleton != null && GodotObject.IsInstanceValid(skeleton))
            {
                return true;
            }
        }

        // Fallback path: imported GLTF/VRM skeleton under avatar root.
        Node searchRoot = attachedNode?.GetParent() ?? attachedNode;
        skeleton = FindFirstSkeleton(searchRoot);
        return skeleton != null && GodotObject.IsInstanceValid(skeleton);
    }

    private static Skeleton3D FindFirstSkeleton(Node root)
    {
        if (root == null)
        {
            return null;
        }

        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is Skeleton3D skeleton && skeleton.GetBoneCount() > 0)
            {
                return skeleton;
            }

            foreach (var child in node.GetChildren())
            {
                if (child is Node childNode)
                {
                    stack.Push(childNode);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Create Node3D targets for IK end effectors.
    /// </summary>
    private void CreateIKTargets()
    {
        Node parent = _skeleton.GetParent() ?? _skeleton;

        _leftHandTarget = new Node3D { Name = "LeftHandIKTarget" };
        parent.AddChild(_leftHandTarget);

        _rightHandTarget = new Node3D { Name = "RightHandIKTarget" };
        parent.AddChild(_rightHandTarget);

        _leftFootTarget = new Node3D { Name = "LeftFootIKTarget" };
        parent.AddChild(_leftFootTarget);

        _rightFootTarget = new Node3D { Name = "RightFootIKTarget" };
        parent.AddChild(_rightFootTarget);

        LumoraLogger.Log("GodotIKAvatarHook: Created IK target nodes");
    }

    /// <summary>
    /// Setup IK solver for an arm.
    /// </summary>
#pragma warning disable CS0618
    private bool SetupArmIK(string side, ref SkeletonIK3D ik, Node3D target)
    {
        string upperArm = FindBoneName(GetUpperArmAliases(side), GetUpperArmTokens(side));
        string hand = FindBoneName(GetHandAliases(side), GetHandTokens(side));

        if (string.IsNullOrWhiteSpace(upperArm) || string.IsNullOrWhiteSpace(hand))
        {
            LumoraLogger.Warn($"GodotIKAvatarHook: Could not resolve {side} arm bones");
            return false;
        }

        ik = new SkeletonIK3D
        {
            Name = $"{side}ArmIK",
            RootBone = upperArm,
            TipBone = hand,
            OverrideTipBasis = true,
            Influence = 1.0f,
            MaxIterations = 10
        };

        _skeleton.AddChild(ik);
        ik.SetTargetNode(target.GetPath());
        ik.Start();

        LumoraLogger.Log($"GodotIKAvatarHook: Setup {side} arm IK ({upperArm} -> {hand})");
        return true;
    }

    /// <summary>
    /// Setup IK solver for a leg.
    /// </summary>
    private bool SetupLegIK(string side, ref SkeletonIK3D ik, Node3D target)
    {
        string upperLeg = FindBoneName(GetUpperLegAliases(side), GetUpperLegTokens(side));
        string foot = FindBoneName(GetFootAliases(side), GetFootTokens(side));

        if (string.IsNullOrWhiteSpace(upperLeg) || string.IsNullOrWhiteSpace(foot))
        {
            LumoraLogger.Warn($"GodotIKAvatarHook: Could not resolve {side} leg bones");
            return false;
        }

        ik = new SkeletonIK3D
        {
            Name = $"{side}LegIK",
            RootBone = upperLeg,
            TipBone = foot,
            OverrideTipBasis = true,
            Influence = 1.0f,
            MaxIterations = 10
        };

        _skeleton.AddChild(ik);
        ik.SetTargetNode(target.GetPath());
        ik.Start();

        LumoraLogger.Log($"GodotIKAvatarHook: Setup {side} leg IK ({upperLeg} -> {foot})");
        return true;
    }
#pragma warning restore CS0618

    private string FindBoneName(string[] aliases, string[] tokens)
    {
        foreach (var alias in aliases)
        {
            int idx = _skeleton.FindBone(alias);
            if (idx >= 0)
            {
                return _skeleton.GetBoneName(idx).ToString();
            }
        }

        var normalizedTokens = new List<string>(tokens.Length);
        foreach (var token in tokens)
        {
            normalizedTokens.Add(NormalizeName(token));
        }

        int boneCount = _skeleton.GetBoneCount();
        for (int i = 0; i < boneCount; i++)
        {
            string candidate = _skeleton.GetBoneName(i).ToString();
            string normalized = NormalizeName(candidate);
            foreach (var token in normalizedTokens)
            {
                if (normalized.Contains(token, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
        }

        return string.Empty;
    }

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[name.Length];
        int outIdx = 0;
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[outIdx++] = char.ToLowerInvariant(c);
            }
        }

        return outIdx == 0 ? string.Empty : new string(buffer[..outIdx]);
    }

    private static string[] GetUpperArmAliases(string side) => side == "Left"
        ? new[] { "LeftUpperArm", "LeftArm", "mixamorig:LeftArm", "J_Bip_L_UpperArm", "upper_arm.L" }
        : new[] { "RightUpperArm", "RightArm", "mixamorig:RightArm", "J_Bip_R_UpperArm", "upper_arm.R" };

    private static string[] GetHandAliases(string side) => side == "Left"
        ? new[] { "LeftHand", "mixamorig:LeftHand", "J_Bip_L_Hand", "hand.L" }
        : new[] { "RightHand", "mixamorig:RightHand", "J_Bip_R_Hand", "hand.R" };

    private static string[] GetUpperLegAliases(string side) => side == "Left"
        ? new[] { "LeftUpperLeg", "LeftLeg", "mixamorig:LeftUpLeg", "J_Bip_L_UpperLeg", "upper_leg.L" }
        : new[] { "RightUpperLeg", "RightLeg", "mixamorig:RightUpLeg", "J_Bip_R_UpperLeg", "upper_leg.R" };

    private static string[] GetFootAliases(string side) => side == "Left"
        ? new[] { "LeftFoot", "mixamorig:LeftFoot", "J_Bip_L_Foot", "foot.L" }
        : new[] { "RightFoot", "mixamorig:RightFoot", "J_Bip_R_Foot", "foot.R" };

    private static string[] GetUpperArmTokens(string side) => side == "Left"
        ? new[] { "leftupperarm", "leftarm", "upperarml", "lupperarm" }
        : new[] { "rightupperarm", "rightarm", "upperarmr", "rupperarm" };

    private static string[] GetHandTokens(string side) => side == "Left"
        ? new[] { "lefthand", "handl", "lhand" }
        : new[] { "righthand", "handr", "rhand" };

    private static string[] GetUpperLegTokens(string side) => side == "Left"
        ? new[] { "leftupperleg", "leftupleg", "leftleg", "upperlegl", "lupleg" }
        : new[] { "rightupperleg", "rightupleg", "rightleg", "upperlegr", "rupleg" };

    private static string[] GetFootTokens(string side) => side == "Left"
        ? new[] { "leftfoot", "footl", "lfoot" }
        : new[] { "rightfoot", "footr", "rfoot" };

    /// <summary>
    /// Update IK targets from Lumora tracking slots, then detect ground under each foot.
    /// </summary>
    private void UpdateIKTargets()
    {
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

        if (_leftFootTarget != null && GodotObject.IsInstanceValid(_leftFootTarget))
        {
            float3 pos = Owner.GetLeftFootTargetPosition();
            floatQ rot = Owner.GetLeftFootTargetRotation();
            _leftFootTarget.GlobalPosition = new Vector3(pos.x, pos.y, pos.z);
            _leftFootTarget.Quaternion = new Quaternion(rot.x, rot.y, rot.z, rot.w);
        }

        if (_rightFootTarget != null && GodotObject.IsInstanceValid(_rightFootTarget))
        {
            float3 pos = Owner.GetRightFootTargetPosition();
            floatQ rot = Owner.GetRightFootTargetRotation();
            _rightFootTarget.GlobalPosition = new Vector3(pos.x, pos.y, pos.z);
            _rightFootTarget.Quaternion = new Quaternion(rot.x, rot.y, rot.z, rot.w);
        }

        // Raycast ground under each foot and write results back to Owner for ProceduralLegs.
        // One-frame lag is imperceptible at runtime.
        UpdateGroundDetection();
    }

    /// <summary>
    /// Uses Godot's physics space to detect ground Y under each foot target.
    /// Results are written into GodotIKAvatar.LeftFootGroundY / RightFootGroundY
    /// so that ProceduralLegs can read them on the next frame.
    /// </summary>
    private void UpdateGroundDetection()
    {
        if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton)) return;

        var spaceState = _skeleton.GetWorld3D()?.DirectSpaceState;
        if (spaceState == null) return;

        float range = Owner.GroundRaycastRange.Value;

        Owner.LeftFootGroundY.Value = RaycastGroundY(spaceState, Owner.GetLeftFootTargetPosition(), range);
        Owner.RightFootGroundY.Value = RaycastGroundY(spaceState, Owner.GetRightFootTargetPosition(), range);
    }

    /// <summary>
    /// Fires a vertical ray at footPos ± range and returns the Y of the first ground hit.
    /// Falls back to footPos.y if nothing is hit.
    /// </summary>
    private static float RaycastGroundY(PhysicsDirectSpaceState3D spaceState, float3 footPos, float range)
    {
        var from = new Vector3(footPos.x, footPos.y + range, footPos.z);
        var to = new Vector3(footPos.x, footPos.y - range, footPos.z);

        var query = PhysicsRayQueryParameters3D.Create(from, to);
        query.CollideWithAreas = false;

        var result = spaceState.IntersectRay(query);
        if (result != null && result.Count > 0 && result.ContainsKey("position"))
            return ((Vector3)result["position"]).Y;

        return footPos.y; // no hit — keep current foot Y
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld)
        {
#pragma warning disable CS0618
            StopAndFreeIK(ref _leftArmIK);
            StopAndFreeIK(ref _rightArmIK);
            StopAndFreeIK(ref _leftLegIK);
            StopAndFreeIK(ref _rightLegIK);
#pragma warning restore CS0618

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
