// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Input;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Renders a skeletal hand visualization on a tracked VR controller slot.
///
/// Displays sphere joints and cylinder bones driven by InputInterface body node
/// positions each frame when hand tracking is active. The skeleton is always visible;
/// when hand tracking is unavailable, a stable controller-relative rest pose is shown.
///
/// Intended usage:
///   Attach to the same slot as a TrackedDevicePositioner.
///   Set HandSide before the component reaches OnStart.
/// </summary>
[ComponentCategory("XR/Visuals")]
public sealed class ControllerHandVisual : Component
{
    // ===== SYNC FIELDS =====

    /// <summary>Which hand side this visual represents.</summary>
    public readonly Sync<Chirality> HandSide;

    /// <summary>Radius of the sphere rendered at each finger joint, in metres.</summary>
    public readonly Sync<float> JointRadius;

    /// <summary>Radius of the cylinder rendered along each finger bone, in metres.</summary>
    public readonly Sync<float> BoneRadius;

    /// <summary>Uniform scale applied to controller-relative hand rest pose and mesh thickness.</summary>
    public readonly Sync<float> HandScale;

    // ===== INNER TYPES =====

    private readonly struct JointEntry
    {
        public readonly BodyNode Node;
        public readonly Slot VisualSlot;
        public JointEntry(BodyNode node, Slot slot) { Node = node; VisualSlot = slot; }
    }

    private readonly struct BoneEntry
    {
        public readonly BodyNode NodeA;
        public readonly BodyNode NodeB;
        public readonly Slot VisualSlot;
        public readonly CylinderMesh Cylinder;
        public BoneEntry(BodyNode a, BodyNode b, Slot slot, CylinderMesh mesh)
        { NodeA = a; NodeB = b; VisualSlot = slot; Cylinder = mesh; }
    }

    // ===== PRIVATE STATE =====

    private Slot _handSkeletonRoot;
    private PBS_Metallic _handMaterial;
    private List<JointEntry> _joints;
    private List<BoneEntry> _bones;
    private Dictionary<BodyNode, float3> _jointWorldPositions;
    private Dictionary<BodyNode, float3> _restPoseLocalPositions;

    // ===== FINGER TOPOLOGY =====

    // Thumb has no Intermediate segment; all other fingers have five segments.
    private static readonly FingerType[] AllFingers = new[]
    {
        FingerType.Thumb,
        FingerType.Index,
        FingerType.Middle,
        FingerType.Ring,
        FingerType.Pinky,
    };

    private static readonly FingerSegmentType[] ThumbSegments = new[]
    {
        FingerSegmentType.Metacarpal,
        FingerSegmentType.Proximal,
        FingerSegmentType.Distal,
        FingerSegmentType.Tip,
    };

    private static readonly FingerSegmentType[] FingerSegments = new[]
    {
        FingerSegmentType.Metacarpal,
        FingerSegmentType.Proximal,
        FingerSegmentType.Intermediate,
        FingerSegmentType.Distal,
        FingerSegmentType.Tip,
    };

    // ===== LIFECYCLE =====

    public override void OnInit()
    {
        base.OnInit();
        HandSide.Value = Chirality.Right;
        JointRadius.Value = 0.0105f;
        BoneRadius.Value = 0.0065f;
        HandScale.Value = 1.12f;
    }

    public override void OnStart()
    {
        base.OnStart();
        _joints = new List<JointEntry>();
        _bones = new List<BoneEntry>();
        _jointWorldPositions = new Dictionary<BodyNode, float3>(32);
        _restPoseLocalPositions = BuildRestPoseLocalPositions(HandSide.Value, HandScale.Value);
        BuildHandSkeleton();
        RefreshVisuals();
        LumoraLogger.Log($"ControllerHandVisual: Initialized for {HandSide.Value} hand on '{Slot.SlotName.Value}'");
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        RefreshVisuals();
    }

    public override void OnDestroy()
    {
        // Child slots are destroyed with the parent slot; just release list references.
        _joints = null;
        _bones = null;
        _jointWorldPositions = null;
        _restPoseLocalPositions = null;
        base.OnDestroy();
    }

    // ===== VISUAL CONSTRUCTION =====

    private void BuildHandSkeleton()
    {
        _handSkeletonRoot = Slot.AddSlot("HandSkeleton");

        // One shared material for all joint spheres and bone cylinders.
        var matSlot = _handSkeletonRoot.AddSlot("HandSkeletonMaterial");
        _handMaterial = matSlot.AttachComponent<PBS_Metallic>();
        _handMaterial.AlbedoColor.Value = new colorHDR(0.82f, 0.82f, 0.86f, 1f);
        _handMaterial.EmissiveColor.Value = new colorHDR(0.06f, 0.06f, 0.10f, 1f);
        _handMaterial.Metallic.Value = 0.25f;
        _handMaterial.Smoothness.Value = 0.55f;
        _handMaterial.RenderQueue.Value = 60;

        Chirality side = HandSide.Value;
        float scale = MathF.Max(HandScale.Value, 0.2f);
        float jRadius = JointRadius.Value * scale;
        float bRadius = BoneRadius.Value * scale;

        BodyNode palmNode = side == Chirality.Left ? BodyNode.LeftPalm : BodyNode.RightPalm;

        // Palm joint sphere.
        CreateJointVisual(palmNode, jRadius);

        foreach (var finger in AllFingers)
        {
            BodyNode[] nodes = GetFingerNodes(finger, side);

            // Joint sphere at every segment node.
            foreach (var node in nodes)
                CreateJointVisual(node, jRadius);

            // Bone connecting palm to this finger's metacarpal.
            CreateBoneVisual(palmNode, nodes[0], bRadius);

            // Bones connecting each adjacent pair of segments within the finger.
            for (int i = 0; i < nodes.Length - 1; i++)
                CreateBoneVisual(nodes[i], nodes[i + 1], bRadius);
        }

        _handSkeletonRoot.ActiveSelf.Value = true;
    }

    private void CreateJointVisual(BodyNode node, float radius)
    {
        var slot = _handSkeletonRoot.AddSlot($"Joint_{node}");
        var sphere = slot.AttachComponent<SphereMesh>();
        sphere.Radius.Value = radius;
        sphere.Segments.Value = 8;
        sphere.Rings.Value = 6;

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = sphere;
        renderer.Material.Target = _handMaterial;
        renderer.SortingOrder.Value = 60;

        _joints.Add(new JointEntry(node, slot));
    }

    private void CreateBoneVisual(BodyNode nodeA, BodyNode nodeB, float radius)
    {
        var slot = _handSkeletonRoot.AddSlot($"Bone_{nodeA}_{nodeB}");
        var cylinder = slot.AttachComponent<CylinderMesh>();
        cylinder.Radius.Value = radius;
        cylinder.Height.Value = 0.03f; // Overwritten every frame once tracking is active.
        cylinder.Segments.Value = 6;

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = cylinder;
        renderer.Material.Target = _handMaterial;
        renderer.SortingOrder.Value = 60;

        _bones.Add(new BoneEntry(nodeA, nodeB, slot, cylinder));
    }

    // ===== PER-FRAME UPDATE =====

    private void RefreshVisuals()
    {
        var input = Engine.Current?.InputInterface;
        _jointWorldPositions.Clear();

        // Update joint sphere world positions. Prefer tracked node poses, but fall back
        // to a controller-relative hand rest pose when tracking is unavailable.
        foreach (var joint in _joints)
        {
            float3 jointWorldPos;

            if (input != null)
            {
                var device = input.GetBodyNode(joint.Node);
                if (device != null && device.IsTracking)
                {
                    jointWorldPos = device.Position;
                }
                else
                {
                    jointWorldPos = GetFallbackJointWorldPosition(joint.Node);
                }
            }
            else
            {
                jointWorldPos = GetFallbackJointWorldPosition(joint.Node);
            }

            joint.VisualSlot.GlobalPosition = jointWorldPos;
            joint.VisualSlot.ActiveSelf.Value = true;
            _jointWorldPositions[joint.Node] = jointWorldPos;
        }

        // Orient and size each bone cylinder between its two joint positions.
        float bRadius = BoneRadius.Value * MathF.Max(HandScale.Value, 0.2f);
        foreach (var bone in _bones)
        {
            if (!_jointWorldPositions.TryGetValue(bone.NodeA, out var posA) ||
                !_jointWorldPositions.TryGetValue(bone.NodeB, out var posB))
            {
                bone.VisualSlot.ActiveSelf.Value = false;
                continue;
            }

            float3 diff = new float3(posB.x - posA.x, posB.y - posA.y, posB.z - posA.z);
            float dist = diff.Length;

            if (dist < 0.0005f)
            {
                bone.VisualSlot.ActiveSelf.Value = false;
                continue;
            }

            bone.VisualSlot.ActiveSelf.Value = true;
            bone.VisualSlot.GlobalPosition = new float3(
                (posA.x + posB.x) * 0.5f,
                (posA.y + posB.y) * 0.5f,
                (posA.z + posB.z) * 0.5f);
            bone.VisualSlot.GlobalRotation = AlignYToDirection(diff);
            bone.Cylinder.Height.Value = dist;
            bone.Cylinder.Radius.Value = bRadius;
        }
    }

    private float3 GetFallbackJointWorldPosition(BodyNode node)
    {
        if (_restPoseLocalPositions != null && _restPoseLocalPositions.TryGetValue(node, out var local))
        {
            return Slot.GlobalPosition + (Slot.GlobalRotation * local);
        }

        return Slot.GlobalPosition;
    }

    private static Dictionary<BodyNode, float3> BuildRestPoseLocalPositions(Chirality side, float scale)
    {
        static float3 MirrorForSide(float3 rightHandPos, Chirality chirality)
        {
            return chirality == Chirality.Left
                ? new float3(-rightHandPos.x, rightHandPos.y, rightHandPos.z)
                : rightHandPos;
        }

        static void AddChain(
            Dictionary<BodyNode, float3> map,
            Chirality chirality,
            FingerType finger,
            IReadOnlyList<float3> rightHandPoints)
        {
            FingerSegmentType[] segments = finger == FingerType.Thumb
                ? ThumbSegments
                : FingerSegments;

            for (int i = 0; i < segments.Length; i++)
            {
                BodyNode node = finger.ComposeFinger(segments[i], chirality);
                map[node] = MirrorForSide(rightHandPoints[i], chirality);
            }
        }

        var map = new Dictionary<BodyNode, float3>(32);
        float poseScale = MathF.Max(scale, 0.2f);
        BodyNode palmNode = side == Chirality.Left ? BodyNode.LeftPalm : BodyNode.RightPalm;
        map[palmNode] = MirrorForSide(new float3(0.0000f, -0.0150f, -0.0350f) * poseScale, side);

        AddChain(map, side, FingerType.Thumb, new[]
        {
            new float3(0.0240f, -0.0100f, -0.0320f) * poseScale,
            new float3(0.0360f, -0.0040f, -0.0430f) * poseScale,
            new float3(0.0440f,  0.0000f, -0.0560f) * poseScale,
            new float3(0.0500f,  0.0040f, -0.0680f) * poseScale,
        });

        AddChain(map, side, FingerType.Index, new[]
        {
            new float3(0.0160f, -0.0060f, -0.0480f) * poseScale,
            new float3(0.0170f, -0.0030f, -0.0670f) * poseScale,
            new float3(0.0180f,  0.0010f, -0.0850f) * poseScale,
            new float3(0.0190f,  0.0040f, -0.1030f) * poseScale,
            new float3(0.0200f,  0.0070f, -0.1190f) * poseScale,
        });

        AddChain(map, side, FingerType.Middle, new[]
        {
            new float3(0.0060f, -0.0060f, -0.0470f) * poseScale,
            new float3(0.0060f, -0.0020f, -0.0680f) * poseScale,
            new float3(0.0060f,  0.0020f, -0.0890f) * poseScale,
            new float3(0.0060f,  0.0060f, -0.1090f) * poseScale,
            new float3(0.0060f,  0.0100f, -0.1260f) * poseScale,
        });

        AddChain(map, side, FingerType.Ring, new[]
        {
            new float3(-0.0040f, -0.0070f, -0.0450f) * poseScale,
            new float3(-0.0050f, -0.0030f, -0.0640f) * poseScale,
            new float3(-0.0060f,  0.0000f, -0.0820f) * poseScale,
            new float3(-0.0070f,  0.0030f, -0.0990f) * poseScale,
            new float3(-0.0080f,  0.0060f, -0.1130f) * poseScale,
        });

        AddChain(map, side, FingerType.Pinky, new[]
        {
            new float3(-0.0140f, -0.0090f, -0.0410f) * poseScale,
            new float3(-0.0160f, -0.0060f, -0.0570f) * poseScale,
            new float3(-0.0180f, -0.0030f, -0.0720f) * poseScale,
            new float3(-0.0200f,  0.0000f, -0.0860f) * poseScale,
            new float3(-0.0220f,  0.0030f, -0.0980f) * poseScale,
        });

        return map;
    }

    // ===== STATIC HELPERS =====

    /// <summary>
    /// Returns the ordered sequence of BodyNodes for a single finger on the given side.
    /// Thumb uses a four-node sequence (no Intermediate segment).
    /// All other fingers use a five-node sequence.
    /// </summary>
    private static BodyNode[] GetFingerNodes(FingerType finger, Chirality chirality)
    {
        FingerSegmentType[] segments = finger == FingerType.Thumb ? ThumbSegments : FingerSegments;
        var nodes = new BodyNode[segments.Length];
        for (int i = 0; i < segments.Length; i++)
            nodes[i] = finger.ComposeFinger(segments[i], chirality);
        return nodes;
    }

    /// <summary>
    /// Returns a rotation whose local Y-axis points along <paramref name="direction"/>.
    /// CylinderMesh geometry extends along the local Y-axis, so this orients bone
    /// cylinders correctly between two joint positions.
    /// </summary>
    private static floatQ AlignYToDirection(float3 direction)
    {
        float len = direction.Length;
        if (len < 0.0001f) return floatQ.Identity;

        float3 dir = new float3(direction.x / len, direction.y / len, direction.z / len);

        // Rotation axis = cross(Up, dir). Angle = acos(Up · dir).
        float3 axis = float3.Cross(float3.Up, dir);
        float axisLen = axis.Length;

        if (axisLen < 0.001f)
        {
            // Direction is nearly parallel or anti-parallel to world Up.
            return float3.Dot(float3.Up, dir) > 0f
                ? floatQ.Identity
                : floatQ.AxisAngle(float3.Forward, MathF.PI);
        }

        float3 normAxis = new float3(axis.x / axisLen, axis.y / axisLen, axis.z / axisLen);
        float angle = MathF.Acos(System.Math.Clamp(float3.Dot(float3.Up, dir), -1f, 1f));
        return floatQ.AxisAngle(normAxis, angle);
    }
}
