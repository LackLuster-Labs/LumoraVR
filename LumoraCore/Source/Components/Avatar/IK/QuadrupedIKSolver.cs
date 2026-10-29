// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar.IK;

// Four-legged body solver. A SIBLING of FullBodyIKSolver, never a subclass - that type is sealed and
// every limb slot in it is private, so there is nothing to extend and nothing to reach.
//
// It also cannot simply call the biped's SolveLeg four times, even though that method already takes its
// whole chain as parameters. Two lines inside it select per-limb state by BodyNode identity, so a third
// or fourth leg handed to it would silently inherit the RIGHT leg's captured bend plane and solve into
// the wrong side of the animal. Here that state lives in a LimbChain[4] instead, which is the one
// structural change that turns "two legs" into "any legs".
//
// The body root is positioned; other joints are rotation-driven. Optional limb stretch scales
// local segment offsets, which are restored from bind before each solve.
//
// Lives in LumoraCore because Sync<T>.SetValueSilently is internal, and every bone write goes through
// it. There is no public bone-write path in the engine. -xlinka
public sealed class QuadrupedIKSolver
{
    // All per-limb state in one struct, held as a [4].
    internal struct LimbChain
    {
        public QuadLimbId Id;
        public Slot[] Bones;            // root..paw
        public Slot? Toe;
        public float[] Lengths;         // Bones.Length - 1, re-measured every frame at the live scale
        public float TotalLength;
        public bool RestValid;
        public float3 RestRootToEnd;
        public float3 RestPlaneNormal;
        public float3[] RestOffsets;    // every joint's rest offset from the limb root, not just the mid
        public float MaxBendRadians;    // widest total fold any hinge on this limb may reach; diagnostics only
        public floatQ ToeRel;
        public float GroundClearance;

        // The FABRIK chain is Bones[ChainStart..]. A front limb with a scapula starts at the upper leg: the scapula
        // is a girdle bone that gets a small clamped swing of its own, the way the biped's clavicle does,
        // and the chain is rooted at the humerus it carries. Everything below is sized for that chain.
        public int ChainStart;
        public float ChainLength;
        public float3 ChainRestRootToEnd;
        public float3[] Joints;         // pooled solve buffer, chain-sized
        public float3[] BestJoints;     // pooled best-iterate buffer for the solver
        public float[] ChainLengths;    // Lengths[ChainStart..], refreshed per solve after Stretch
        public FabrikSolver.HingeLimit[] Hinges;
        public float[] FoldAngles;
        public float[] PlanLengths;
        public FabrikSolver.HingeLimit[] PlanLimits;
        public float PlanFold;
        public bool HasFoldPlan;

        // Last-solve diagnostics, read by DescribeLimbConstraints.
        public FabrikSolver.HingeSolveStats LastStats;
        public bool LastReachClamped;
        public float LastScapulaSwing;
        public float LastFoldSeed;
        public float LastRadialShortfall;
        public float LastRenderedError;
    }

    // The one contract between the gait and the solver, one per paw.
    //
    // Field-identical to the biped's Target on purpose: that struct already carries everything a
    // procedural plant needs and not one field of it is biped-specific. Declared separately anyway so
    // nothing here has to reference FullBodyIKSolver.
    public struct LimbTarget
    {
        public bool Valid;
        public float3 Position;
        public floatQ Rotation;
        public float PositionWeight;
        public float RotationWeight;
        public bool HasBendGoal;
        public float3 BendGoal;
        public bool GroundAlign;
        public float3 GroundNormal;
        public float3 GroundForward;
        public bool HasGroundRotation;
        public floatQ GroundRotation;
        public float StepLift;
        public bool HasSupportPosition;
        public float3 SupportPosition;
    }

    public struct HeadTarget
    {
        public bool Valid;
        public float3 Position;
        public floatQ Rotation;
        public float PositionWeight;
        public float RotationWeight;
    }

    // Where the two girdles want to be. The gait fills these from the paws it has planted, so the body
    // pitches and rolls because the legs put it there rather than because something animated it.
    public struct ChassisTarget
    {
        public bool Valid;
        public float3 PelvisPosition;
        public float3 ChestPosition;
        public float FrontRoll;
        public float RearRoll;
    }

    // TUNABLES, pushed every frame by the component.
    public float MaxStretch = 1.06f;
    public float BendGoalWeight = 1f;

    // Minimum root-to-paw span relative to the authored stance.
    public float MinLimbFoldScale = 0.35f;

    // How far the MESH extends below the lowest paw or toe BONE, metres at the live scale. The sole plane
    // is measured from bones because the solver never sees a mesh, and a rig whose paw pads are modelled
    // below the paw bone sat that far above the floor for good: every clearance and stand height was
    // measured from bone, not from pad. The component owns the mesh, so it sets this; zero keeps the
    // bone plane. Positive means pad below bone. -xlinka
    public float SoleOffset;

    // Per-joint hinge sizing. The flexion each joint needs is derived from MinLimbFoldScale in
    // CaptureHinges. Extension is bounded two ways: a joint never gets closer to straight than
    // HingeMinDeflectionRadians on its fold side (the anti-lock margin, and what stops an inversion),
    // and HingeExtendRadians optionally caps how far past REST it may straighten. The cap is off by
    // default: a stride puts the paw ahead of and behind the body at ground height, which needs a leg
    // longer than its rest pose, and a cap tight enough to matter is a paw that cannot reach its plant.
    public float HingeMinDeflectionRadians = 0.1745f;   // 10 degrees
    public float HingeExtendRadians = MathF.PI;         // cap from rest; PI = no cap
    public float StallNudgeRadians = 0.05f;             // ~3 degrees, the deadlock escape step
    public int LimbIterations = 10;
    public float LimbTolerance = 0.0005f;

    // Scapula: a fraction of the limb's swing, clamped. A shoulder blade glides on the ribcage over a
    // small arc; it does not reach.
    public float ScapulaWeight = 0.35f;
    public float MaxScapulaSwing = 0.35f;        // ~20 degrees either way

    public float SpineStiffness = 0.6f;
    public float MaxSpineBendPerJoint = 0.45f;
    public float NeckStiffness = 0.5f;
    public float MaxHeadYaw = 1.4f;
    public float MaxBodyRoll = 0.35f;
    public float3 BodyForward = float3.Backward;
    public float? GroundY;
    public float3? RootAnchor;
    public float3 LocomotionOffset;
    public float TimeSeconds;

    // OUTPUTS
    public float3 RestBodyForward => _restBodyForward;
    public float[] PawGroundClearance { get; } = new float[QuadLimb.Count];
    public float RestChestHeight { get; private set; }
    public float RestPelvisHeight { get; private set; }
    public float RestBodyLength { get; private set; }

    // How far the head can get from the neck root when fully extended. The head target has to be
    // clamped to this or the neck is asked for a distance it cannot cover.
    public float RestNeckReach { get; private set; }

    // A rest capture taken before the equip transforms settled, caught by its own numbers.
    //
    // No four-legged animal is shorter nose-to-tail than a third of one leg. When the capture says it
    // is, the skeleton was still folded or still in bind when it was measured, and every rest value
    // taken from it - body length, stand heights, stance points - is wrong. The spine beam then HOLDS
    // the animal in that shape, so a bad capture is not a transient: it is the pose. -xlinka
    public bool RestLooksCollapsed
    {
        get
        {
            if (!_captured)
                return true;

            float longestLimb = 0f;
            for (int i = 0; i < QuadLimb.Count; i++)
            {
                var lengths = _limbs[i].Lengths;
                if (lengths == null)
                    continue;
                float total = 0f;
                for (int j = 0; j < lengths.Length; j++)
                    total += lengths[j];
                longestLimb = MathF.Max(longestLimb, total);
            }

            return longestLimb > 0.05f && RestBodyLength < longestLimb * 0.35f;
        }
    }
    public float3 RestPelvisPosition { get; private set; }
    public float3 RestChestPosition { get; private set; }
    // The lowest paw or toe BONE in the rest pose. Diagnostics read this; everything that has to meet
    // the floor reads EffectiveSoleY, which is this minus the mesh below it.
    public float RestSoleY => _restSoleY;
    public float EffectiveSoleY => _restSoleY - SoleOffset;

    // Height of each girdle bone above the sole plane in the rest pose, at the live scale. This is
    // the animal's natural standing height, and it is what the chassis target has to put the body at:
    // the girdle's height above its own limb ROOT (what the component used to measure) is a few
    // centimetres and says nothing about how far the paws are below it. Measured to the MESH sole, so
    // a pad modelled below the paw bone lifts the body by its thickness instead of sinking into it.
    public float RestPelvisStandHeight => RestPelvisHeight - EffectiveSoleY;
    public float RestChestStandHeight => RestChestHeight - EffectiveSoleY;

    public int SpineCount => _spine.Count;
    public int NeckCount => _neck.Count;
    public bool IsReady => _captured && _rig != null && !_rig.IsDestroyed;

    // Every bone the solve writes; FixTransforms puts these and only these back. -xlinka
    public IReadOnlyList<Slot> DrivenBones => _bones;

    private QuadrupedRig? _rig;
    private bool _captured;
    private bool _frameBegun;

    private readonly List<Slot> _bones = new();
    private readonly Dictionary<Slot, float3> _bindLocalPos = new();
    private readonly Dictionary<Slot, floatQ> _bindLocalRot = new();
    private readonly Dictionary<Slot, float3> _restDir = new();
    private readonly Dictionary<Slot, floatQ> _restRot = new();

    private LimbChain[] _limbs = new LimbChain[QuadLimb.Count];
    private readonly List<Slot> _spine = new();

    // Which end of _spine is the hierarchy ancestor: 0 when the pelvis carries the chest (the usual
    // case), Count-1 when the rig is built the other way round.
    private int _spineRootIndex;
    private Slot? _bodyRoot;
    private readonly List<Slot> _neck = new();
    private float3[] _spineJoints = Array.Empty<float3>();
    private float[] _spineLengths = Array.Empty<float>();
    private FabrikSolver.JointLimit[] _spineLimits = Array.Empty<FabrikSolver.JointLimit>();
    private float3[] _neckJoints = Array.Empty<float3>();
    private float[] _neckLengths = Array.Empty<float>();

    private float3 _restBodyForward = float3.Backward;
    private float3 _restBodyUp = float3.Up;
    private float3 _curBodyForward = float3.Backward;
    private float _restSoleY;

    // Degrees, converted at the one use. floatQ.AxisAngle takes RADIANS, and this went in raw: 28
    // radians is four and a half turns, so a toe at full step lift was wound to 164 degrees and at half
    // lift to 82, spinning through everything in between as the lift ramped. -xlinka
    private const float ToeCurlAngle = 28f;
    private const float ToeCurlRadians = ToeCurlAngle * (MathF.PI / 180f);

    // SETUP

    public void Initialize(QuadrupedRig rig)
    {
        _rig = rig;
        _captured = false;
        _hasLeft = false;
        if (rig == null || rig.IsDestroyed)
            return;

        _bones.Clear();
        _spine.Clear();
        _neck.Clear();

        // Every Slot reference is cached ONCE here. The rig's map is a synced list underneath, so a
        // lookup is a linear scan - fine at setup, never acceptable in a per-frame path that would do
        // it a few hundred times.
        var pelvis = rig.TryGetBone(QuadNode.Pelvis);
        var chest = rig.TryGetBone(QuadNode.Chest);
        if (IsAncestorOf(pelvis, chest))
            AddHierarchyPath(pelvis!, chest!, _spine);
        else if (IsAncestorOf(chest, pelvis))
        {
            AddHierarchyPath(chest!, pelvis!, _spine);
            _spine.Reverse();
        }

        var spineRoot = _spine.Count > 0 ? (IsAncestorOf(pelvis, chest) ? pelvis : chest) : null;
        var head = rig.TryGetBone(QuadNode.Head);
        _bodyRoot = spineRoot;
        if (spineRoot != null && head != null && !head.IsDestroyed)
        {
            var common = spineRoot;
            while (common != null && common != rig.Slot && common != head && !IsAncestorOf(common, head))
                common = common.Parent;
            if (common != null && (common == head || IsAncestorOf(common, head)))
            {
                if (common != rig.Slot)
                    _bodyRoot = common;
                AddHierarchyPath(common, head, _neck);
                _neck.RemoveAt(0);
                while (_neck.Count > 0 && _spine.Contains(_neck[0]))
                    _neck.RemoveAt(0);
                _bones.Remove(rig.Slot);
            }
        }
        if (_bodyRoot != null && spineRoot != null && _bodyRoot != spineRoot)
            AddHierarchyPath(_bodyRoot, spineRoot, new List<Slot>());

        for (int i = 0; i < QuadLimb.Count; i++)
            _limbs[i] = BuildLimb(rig, (QuadLimbId)i);

        // The tail is deliberately NOT in _bones. It belongs to DynamicBoneChain, which simulates after
        // this solver runs; claiming it here would mean two systems writing the same bones every frame.
        _spineJoints = new float3[System.Math.Max(_spine.Count, 2)];
        _spineLengths = new float[System.Math.Max(_spine.Count - 1, 1)];
        _spineLimits = new FabrikSolver.JointLimit[_spineLengths.Length];
        _neckJoints = new float3[System.Math.Max(_neck.Count, 2)];
        _neckLengths = new float[System.Math.Max(_neck.Count - 1, 1)];

        if (_spine.Count < 2)
        {
            Lumora.Core.Logging.Logger.Warn("QuadrupedIKSolver: rig has no usable spine (need pelvis + chest)");
            return;
        }

        _spineRootIndex = IsAncestorOf(_spine[0], _spine[^1]) ? 0 : _spine.Count - 1;
        if (_spineRootIndex != 0)
        {
            Lumora.Core.Logging.Logger.Debug(
                $"QuadrupedIKSolver: spine hierarchy runs chest-first ('{_spine[^1].SlotName.Value}' carries "
                + $"'{_spine[0].SlotName.Value}'), so the body is placed from the chest end");
        }

        StoreDefaultLocalState();
        _captured = true;
        ReadPose();
    }

    private void AddHierarchyPath(Slot root, Slot end, List<Slot> chain)
    {
        int start = chain.Count;
        for (var bone = end; bone != null; bone = bone.Parent)
        {
            if (bone == root && start > 0 && chain[start - 1] == root)
                break;
            chain.Add(bone);
            if (!_bones.Contains(bone))
                _bones.Add(bone);
            if (bone == root)
                break;
        }
        chain.Reverse(start, chain.Count - start);
    }

    // NO <NOIK> FILTERING ANYWHERE IN HERE. The tag means the opposite of what it means on a person.
    //
    // <NOIK> marks a bone as excluded from the HUMANOID rig. On an animal, the bones an author tags that
    // way ARE the animal: the fox ships <NOIK> ForeShoulder, <NOIK> ForeLeg, <NOIK> Thigh, <NOIK> ForePaw
    // and so on, precisely because none of them belong to a biped. QuadrupedRig already understands this
    // and inverts it - the tagged set is the anatomical set - but this solver still ran the humanoid test
    // and threw every single leg bone away. Each chain came out under three bones, went home empty, and
    // the animal solved with a spine, a neck and no legs at all. The two write helpers below carried the
    // same test, so even a chain that survived would have had its writes refused.
    //
    // This is also the whole difference between a packaged avatar and the same animal imported from a
    // model file: the model file has no author tags, so its legs were never filtered and always worked.
    //
    // Nothing is lost by dropping the test. Anything reaching TryGetLimbBone or TryGetBone was mapped by
    // QuadrupedRig on purpose, and that populate already rejects twist, helper, roll and ik bones by
    // name. -xlinka
    private LimbChain BuildLimb(QuadrupedRig rig, QuadLimbId id)
    {
        var chain = new LimbChain { Id = id, MaxBendRadians = 0f };

        var bones = new List<Slot>();
        bool firstIsScapula = false;
        for (int s = QuadLimb.SegScapula; s <= QuadLimb.SegPaw; s++)
        {
            var bone = rig.TryGetLimbBone(id, s);
            if (bone == null || bone.IsDestroyed)
                continue;
            if (bones.Count == 0 && s == QuadLimb.SegScapula)
                firstIsScapula = true;
            if (bones.Count == 0)
                bones.Add(bone);
            else if (bone != bones[^1] && IsAncestorOf(bones[^1], bone))
                AddHierarchyPath(bones[^1], bone, bones);
        }

        if (bones.Count < 3)
        {
            chain.Bones = Array.Empty<Slot>();
            chain.Lengths = Array.Empty<float>();
            chain.RestOffsets = Array.Empty<float3>();
            chain.Joints = Array.Empty<float3>();
            chain.BestJoints = Array.Empty<float3>();
            chain.ChainLengths = Array.Empty<float>();
            chain.Hinges = Array.Empty<FabrikSolver.HingeLimit>();
            chain.FoldAngles = Array.Empty<float>();
            chain.PlanLengths = Array.Empty<float>();
            chain.PlanLimits = Array.Empty<FabrikSolver.HingeLimit>();
            return chain;
        }

        chain.Bones = bones.ToArray();
        chain.Lengths = new float[bones.Count - 1];
        chain.RestOffsets = new float3[bones.Count];

        // The scapula is split off only when there is still a real chain (3+ bones) below it. A limb
        // that is scapula + upper + paw has no elbow to fold and needs every bone it has.
        int upperIndex = bones.IndexOf(rig.TryGetLimbBone(id, QuadLimb.SegUpper));
        chain.ChainStart = firstIsScapula && upperIndex > 0 && bones.Count - upperIndex >= 3 ? upperIndex : 0;
        int chainCount = bones.Count - chain.ChainStart;
        chain.Joints = new float3[chainCount];
        chain.BestJoints = new float3[chainCount];
        chain.ChainLengths = new float[chainCount - 1];
        chain.Hinges = new FabrikSolver.HingeLimit[chainCount - 1];
        chain.FoldAngles = new float[chainCount - 1];
        chain.PlanLengths = new float[chainCount - 1];
        chain.PlanLimits = new FabrikSolver.HingeLimit[chainCount - 1];

        var toe = rig.TryGetLimbBone(id, QuadLimb.SegToe);
        if (toe != null && !toe.IsDestroyed)
            chain.Toe = toe;

        for (int i = 0; i < chain.Bones.Length; i++)
        {
            if (!_bones.Contains(chain.Bones[i]))
                _bones.Add(chain.Bones[i]);
        }
        if (chain.Toe != null && !_bones.Contains(chain.Toe))
            _bones.Add(chain.Toe);

        return chain;
    }

    private static bool IsAncestorOf(Slot? ancestor, Slot? descendant)
    {
        if (ancestor == null || descendant == null)
            return false;
        for (var s = descendant.Parent; s != null; s = s.Parent)
        {
            if (ReferenceEquals(s, ancestor))
                return true;
        }
        return false;
    }

    private void StoreDefaultLocalState()
    {
        _bindLocalPos.Clear();
        _bindLocalRot.Clear();
        for (int i = 0; i < _bones.Count; i++)
        {
            var b = _bones[i];
            if (b == null || b.IsDestroyed)
                continue;
            _bindLocalPos[b] = b.LocalPosition.Value;
            _bindLocalRot[b] = b.LocalRotation.Value;
        }
    }

    public void ResetToDefaultPose() => FixTransforms();

    // What the last solve left on every bone, so a later look can tell this solver's writes from a write that
    // came from somewhere else.
    private float3[] _leftPos = Array.Empty<float3>();
    private floatQ[] _leftRot = Array.Empty<floatQ>();
    private bool _hasLeft;

    private void RecordLeft()
    {
        if (_leftPos.Length != _bones.Count)
        {
            _leftPos = new float3[_bones.Count];
            _leftRot = new floatQ[_bones.Count];
        }
        for (int i = 0; i < _bones.Count; i++)
        {
            var b = _bones[i];
            if (b == null || b.IsDestroyed)
                continue;
            _leftPos[i] = b.LocalPosition.Value;
            _leftRot[i] = b.LocalRotation.Value;
        }
        _hasLeft = true;
    }

    // Bones still holding what the last solve left go back to rest; a bone written since keeps that write, so a
    // re-capture takes it as the new rest.
    public void ReturnUntouchedToRest()
    {
        if (!_hasLeft || _leftPos.Length != _bones.Count)
        {
            FixTransforms();
            return;
        }
        for (int i = 0; i < _bones.Count; i++)
        {
            var b = _bones[i];
            if (b == null || b.IsDestroyed)
                continue;
            if (b.LocalPosition.Value == _leftPos[i] && _bindLocalPos.TryGetValue(b, out var p))
                b.LocalPosition.SetValueSilently(p, change: true);
            if (b.LocalRotation.Value == _leftRot[i] && _bindLocalRot.TryGetValue(b, out var r))
                b.LocalRotation.SetValueSilently(r, change: true);
        }
        _hasLeft = false;
    }

    // PER-FRAME

    private void FixTransforms()
    {
        for (int i = 0; i < _bones.Count; i++)
        {
            var b = _bones[i];
            if (b == null || b.IsDestroyed)
                continue;
            if (_bindLocalPos.TryGetValue(b, out var p))
                b.LocalPosition.SetValueSilently(p, change: true);
            if (_bindLocalRot.TryGetValue(b, out var r))
                b.LocalRotation.SetValueSilently(r, change: true);
        }
    }

    // Re-measure everything from the bones FixTransforms just reset. They carry the live avatar scale,
    // so every length is right for the avatar as rendered this frame and there is no stored capture
    // scale that can drift away from it.
    private void ReadPose()
    {
        _restDir.Clear();
        _restRot.Clear();

        for (int i = 0; i < _bones.Count; i++)
        {
            var bone = _bones[i];
            if (bone == null || bone.IsDestroyed)
                continue;
            _restRot[bone] = bone.GlobalRotation;
        }

        int step = _spineRootIndex == 0 ? 1 : -1;
        for (int i = _spineRootIndex; i >= 0 && i < _spine.Count; i += step)
        {
            int next = i + step;
            float3 dir = next >= 0 && next < _spine.Count
                ? _spine[next].GlobalPosition - _spine[i].GlobalPosition
                : _spine[i].GlobalPosition - _spine[i - step].GlobalPosition;
            if (dir.LengthSquared > 1e-8f)
                _restDir[_spine[i]] = dir.Normalized;
        }
        CaptureChainDirections(_neck);

        // Body forward, geometric. Never read off a bone rotation - the authored hips rotation is
        // exactly the thing that produces a backward-facing body on rigs with an unusual convention.
        var rig = _rig;
        if (rig != null && !rig.IsDestroyed)
        {
            _restBodyForward = rig.BodyForward;
            if (_restBodyForward.LengthSquared < 1e-8f)
                _restBodyForward = float3.Backward;
        }
        _curBodyForward = BodyForward.LengthSquared > 1e-8f ? BodyForward.Normalized : _restBodyForward;

        if (_spine.Count >= 2)
        {
            RestPelvisPosition = _spine[0].GlobalPosition;
            RestChestPosition = _spine[^1].GlobalPosition;
            RestPelvisHeight = RestPelvisPosition.y;
            RestChestHeight = RestChestPosition.y;
            // Body length is the GIRDLE SEPARATION, not the spine chain's end-to-end distance.
            //
            // A name-based spine chain can measure a vertical stack: the armature root may be called
            // "Hips" but physically sit
            // at the withers, with "Chest" 0.41 directly above it, so the animal's "length" read 0.228
            // while its own legs measured 1.2 to 1.4. Every gait distance scales off this number, so a
            // fox got roughly a fifth of the stride, step height and step threshold it needed and the
            // legs bunched under the body instead of spreading.
            //
            // The girdles cannot lie the same way. They are the midpoints of the four limb ROOTS, which
            // are the bones the legs actually hang from, so their separation IS nose-to-tail distance
            // whatever the spine bones are called or where the armature root was parked. Flattened,
            // because a body axis is horizontal. -xlinka
            RestBodyLength = MeasureGirdleSeparation();
            if (RestBodyLength < 0.05f)
                RestBodyLength = float3.Distance(RestPelvisPosition, RestChestPosition);
            for (int i = 0; i < _spine.Count - 1; i++)
            {
                _spineLengths[i] = float3.Distance(_spine[SpineIndex(i)].GlobalPosition, _spine[SpineIndex(i + 1)].GlobalPosition);
                float bend = MathF.Max(MaxSpineBendPerJoint * (1f - System.Math.Clamp(SpineStiffness, 0f, 1f) * 0.5f), 0.01f);
                if (i > 0)
                {
                    float3 previous = _spine[SpineIndex(i)].GlobalPosition - _spine[SpineIndex(i - 1)].GlobalPosition;
                    float3 next = _spine[SpineIndex(i + 1)].GlobalPosition - _spine[SpineIndex(i)].GlobalPosition;
                    // The bend allowance starts at the authored curve, not at an artificially straight spine.
                    if (previous.LengthSquared > 1e-10f && next.LengthSquared > 1e-10f)
                        bend += MathF.Acos(System.Math.Clamp(float3.Dot(previous.Normalized, next.Normalized), -1f, 1f));
                }
                _spineLimits[i] = FabrikSolver.JointLimit.Cone(bend);
            }
        }
        for (int i = 0; i < _neck.Count - 1; i++)
            _neckLengths[i] = float3.Distance(_neck[i].GlobalPosition, _neck[i + 1].GlobalPosition);
        float neckReach = 0f;
        for (int i = 0; i < _neck.Count - 1; i++)
            neckReach += _neckLengths[i];
        RestNeckReach = neckReach;

        // The sole plane is the lowest point over ALL FOUR paws and their toes. The biped takes the
        // lower of two; taking the lower of two here would put the plane under whichever pair happens
        // to be lower in the authored pose and lift the other pair off the floor for good.
        _restSoleY = float.MaxValue;
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            ref var limb = ref _limbs[i];
            if (limb.Bones == null || limb.Bones.Length == 0)
                continue;
            float y = limb.Bones[^1].GlobalPosition.y;
            if (limb.Toe != null && !limb.Toe.IsDestroyed)
                y = MathF.Min(y, limb.Toe.GlobalPosition.y);
            _restSoleY = MathF.Min(_restSoleY, y);
        }
        if (_restSoleY == float.MaxValue)
            _restSoleY = 0f;

        // Each paw measures its own pad height; a saved mid-stride pose must not raise its target.
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            CaptureLimbRest(ref _limbs[i]);
            ref var limb = ref _limbs[i];
            PawGroundClearance[i] = 0f;
            if (limb.Bones != null && limb.Bones.Length > 0)
            {
                float pawY = limb.Bones[^1].GlobalPosition.y;
                float soleY = limb.Toe is { IsDestroyed: false } ? MathF.Min(pawY, limb.Toe.GlobalPosition.y) : pawY;
                PawGroundClearance[i] = pawY - soleY + SoleOffset;
            }
        }
    }

    private void CaptureChainDirections(List<Slot> chain)
    {
        for (int i = 0; i < chain.Count - 1; i++)
        {
            var bone = chain[i];
            float3 dir = chain[i + 1].GlobalPosition - bone.GlobalPosition;
            if (dir.LengthSquared > 1e-8f)
                _restDir[bone] = dir.Normalized;
        }
    }

    // Girdle separation, measured through the RIG so there is exactly one definition of a girdle.
    //
    // This used to take each chain's first bone, which is not the same bone the rig uses: a front limb
    // starts at the SCAPULA and a rear limb at the thigh, so it compared a shoulder blade against a hip
    // and got a span under 0.1 that fell straight onto the floor clamp. The animal then had a body
    // length of 10 cm driving every stride, step height and step threshold in the gait. The rig's own
    // LimbRoot prefers SegUpper for both ends, which is the honest nose-to-tail comparison. -xlinka
    private float MeasureGirdleSeparation()
    {
        if (_rig == null || _rig.IsDestroyed)
            return 0f;
        if (!_rig.TryGetGirdle(front: true, out float3 front, out _, out _, out _))
            return 0f;
        if (!_rig.TryGetGirdle(front: false, out float3 rear, out _, out _, out _))
            return 0f;

        float3 span = front - rear;
        span.y = 0f;
        return span.Length;
    }

    // Capture the authored bend of a limb - the whole zigzag, not just the mid joint.
    //
    // Storing every joint's offset from the limb root is the part that makes an N-segment digitigrade
    // leg tractable. The biped stores one mid offset and transports it to build a pole vector, which is
    // enough for a 3-joint limb with a single bend. A fox front leg bends forward at the carpus and a
    // fox rear leg bends backward at the hock, on the same animal, across four segments; there is no
    // single pole that describes that, but the authored pose describes it exactly. -xlinka
    // Horizontal distance between the front and rear girdle centres, from the limb roots themselves.
    private void CaptureLimbRest(ref LimbChain limb)
    {
        limb.RestValid = false;
        if (limb.Bones == null || limb.Bones.Length < 3)
            return;

        for (int i = 0; i < limb.Bones.Length; i++)
        {
            if (limb.Bones[i] == null || limb.Bones[i].IsDestroyed)
                return;
        }

        float3 root = limb.Bones[0].GlobalPosition;
        float3 end = limb.Bones[^1].GlobalPosition;
        float3 rootToEnd = end - root;
        if (rootToEnd.LengthSquared < 1e-8f)
            return;

        limb.TotalLength = 0f;
        for (int i = 0; i < limb.Bones.Length - 1; i++)
        {
            limb.Lengths[i] = float3.Distance(limb.Bones[i].GlobalPosition, limb.Bones[i + 1].GlobalPosition);
            limb.TotalLength += limb.Lengths[i];
        }
        if (limb.TotalLength < 1e-6f)
            return;

        for (int i = 0; i < limb.Bones.Length; i++)
            limb.RestOffsets[i] = limb.Bones[i].GlobalPosition - root;

        // Rest direction of every segment, bone to child. ApplySwing derives each bone's write from
        // this table and returns without writing for a bone that has no entry - and the spine and
        // neck were the only chains that filled it. Every leg segment therefore fell through
        // ApplySwing untouched: the paw got its ground rotation, the toe followed it, and the four
        // legs stayed in the bind pose no matter where the targets were. -xlinka
        for (int i = 0; i < limb.Bones.Length - 1; i++)
        {
            float3 dir = limb.Bones[i + 1].GlobalPosition - limb.Bones[i].GlobalPosition;
            if (dir.LengthSquared > 1e-8f)
                _restDir[limb.Bones[i]] = dir.Normalized;
        }

        // Bend plane from the joint that sticks out furthest from the root->end line. On a multi-bend
        // leg that is the joint that actually defines the plane; taking the middle one by index can land
        // on a nearly-colinear joint and produce a normal made of rounding error.
        float3 chainOrigin = limb.RestOffsets[limb.ChainStart];
        float3 chainReach = rootToEnd - chainOrigin;
        float3 limbDir = chainReach.Normalized;
        float3 bestBend = float3.Zero;
        float bestDist = 0f;
        for (int i = limb.ChainStart + 1; i < limb.Bones.Length - 1; i++)
        {
            float3 offset = limb.RestOffsets[i] - chainOrigin;
            float3 perpendicular = offset - limbDir * float3.Dot(offset, limbDir);
            float d = perpendicular.Length;
            if (d > bestDist)
            {
                bestDist = d;
                bestBend = offset;
            }
        }

        float3 normal = float3.Cross(chainReach, bestBend);
        if (normal.LengthSquared < 1e-10f)
            normal = float3.Cross(chainReach, float3.Up);
        if (normal.LengthSquared < 1e-10f)
            normal = float3.Cross(chainReach, float3.Right);
        if (normal.LengthSquared < 1e-10f)
            return;

        limb.RestRootToEnd = rootToEnd;
        limb.RestPlaneNormal = normal.Normalized;

        int start = limb.ChainStart;
        limb.ChainRestRootToEnd = limb.RestOffsets[^1] - limb.RestOffsets[start];
        limb.ChainLength = 0f;
        for (int i = start; i < limb.Lengths.Length; i++)
            limb.ChainLength += limb.Lengths[i];
        if (limb.ChainLength < 1e-6f || limb.ChainRestRootToEnd.LengthSquared < 1e-10f)
            return;

        CaptureHinges(ref limb);

        if (limb.Toe != null && !limb.Toe.IsDestroyed)
            limb.ToeRel = limb.Bones[^1].GlobalRotation.Inverse * limb.Toe.GlobalRotation;

        limb.RestValid = true;
    }

    // One SIGNED hinge per chain joint, measured from the authored rest deflection about the captured
    // bend plane normal, replacing one symmetric cone for the whole limb.
    //
    // The cone was sized 2*acos(MinLimbFoldScale) so a crouch could fold the legs, and that identity is
    // the total turn from STRAIGHT a joint needs to shorten its span. Applied as a cone it permitted
    // that much turn in EVERY direction from the parent bone: at the default fold that is 139 degrees,
    // so a hock was allowed to hyperextend 139 degrees the wrong way, and the only thing keeping it
    // from doing so was the seed happening to sit on the right side. A real crouch is fold-from-REST on
    // the fold side only. So each joint gets its own signed range on its own fold side: flexion is
    // whatever the crouch floor needs measured on this chain's real segment lengths, extension stops at
    // an anti-lock margin short of straight. The fold side is the sign of the authored bend, which is
    // why a fox can bend forward at the carpus and backward at the hock with no pole anywhere.
    //
    // A joint authored nearly straight has no sign of its own; it takes the opposite of its nearest
    // signed neighbour, because a limb is a zigzag and consecutive joints fold opposite ways.
    //
    // Joint 0 of the chain is the shoulder or hip. Against a scapula it is hinged like the rest (the
    // scapula direction is the reference the solver is handed); with no girdle bone in the chain there
    // is nothing to measure against and it stays free, exactly as before. -xlinka
    private void CaptureHinges(ref LimbChain limb)
    {
        int start = limb.ChainStart;
        int m = limb.Bones.Length - start;
        var hinges = limb.Hinges;
        float3 nrm = limb.RestPlaneNormal;

        float restReachRatio = limb.ChainRestRootToEnd.Length / MathF.Max(limb.ChainLength, 1e-6f);
        float fold = System.Math.Clamp(MinLimbFoldScale * restReachRatio, 0.05f, 1f);
        float minDeflection = MathF.Max(0f, HingeMinDeflectionRadians);
        float extendCap = MathF.Max(0f, HingeExtendRadians);
        const float MaxTotalFold = 2.8f;     // ~160 degrees: a joint cannot fold onto itself
        const float MinFlex = 0.15f;         // ~9 degrees: never tighter than a margin past rest
        const float StraightBand = 0.052f;   // 3 degrees: below this the sign is the neighbour's call

        for (int j = 0; j < m - 1; j++)
            hinges[j] = default;

        // Rest deflection of every chain segment against the one before it. Segment 0 is measured
        // against the scapula when there is one.
        for (int j = 0; j < m - 1; j++)
        {
            int boneIndex = start + j;
            float3 cur = limb.Bones[boneIndex + 1].GlobalPosition - limb.Bones[boneIndex].GlobalPosition;
            float3 prev;
            if (j > 0)
                prev = limb.Bones[boneIndex].GlobalPosition - limb.Bones[boneIndex - 1].GlobalPosition;
            else if (start > 0)
                prev = limb.Bones[start].GlobalPosition - limb.Bones[start - 1].GlobalPosition;
            else
                continue;

            prev -= nrm * float3.Dot(prev, nrm);
            cur -= nrm * float3.Dot(cur, nrm);
            if (prev.LengthSquared < 1e-10f || cur.LengthSquared < 1e-10f)
                continue;

            float rest = FabrikSolver.SignedAngle(prev.Normalized, cur.Normalized, nrm);
            hinges[j].Rest = rest;
            hinges[j].Active = true;
            hinges[j].FlexSign = rest > StraightBand ? 1f : rest < -StraightBand ? -1f : 0f;
        }

        // Fill the ambiguous signs from the zigzag, previous neighbour first.
        for (int j = 0; j < m - 1; j++)
        {
            if (!hinges[j].Active || hinges[j].FlexSign != 0f)
                continue;
            float sign = 0f;
            for (int k = j - 1; k >= 0 && sign == 0f; k--)
                if (hinges[k].Active && hinges[k].FlexSign != 0f)
                    sign = -hinges[k].FlexSign;
            for (int k = j + 1; k < m - 1 && sign == 0f; k++)
                if (hinges[k].Active && hinges[k].FlexSign != 0f)
                    sign = -hinges[k].FlexSign;
            hinges[j].FlexSign = sign != 0f ? sign : 1f;
        }

        // Sized in fold-side coordinates: s is the rest deflection on the joint's own fold side, and it
        // is NEGATIVE for a joint that was authored a hair past straight the wrong way (sign borrowed
        // from a neighbour). The extension limit is the anti-lock margin from straight, tightened by the
        // optional cap-from-rest, and never beyond rest for a joint already authored straighter than
        // the margin: that one can fold but not straighten.
        //
        // The flex limit comes from the CHAIN, not from an identity. 2*acos(f) is the per-joint turn
        // that shortens a zigzag of EQUAL links to a fraction f of its length, and a leg is not that:
        // measured on a stifle-hock-cannon of 0.29/0.28/0.18, both joints at the 139 degrees the
        // identity gives for f = 0.35 leave the chain at 0.42 of its length, because the short cannon
        // no longer cancels the thigh's lateral throw. The crouch floor was unreachable by 7% of the
        // leg with every joint pinned at its limit. So the fold each joint needs is found the way the
        // seed finds it: one extra fold on every joint's own side, bisected on the real segment lengths
        // until the span is f times the chain. The ranges and the seed then agree by construction, and
        // MinLimbFoldScale is still the one input. A margin past that so the polish has room. -xlinka
        for (int j = 0; j < m - 1; j++)
        {
            if (!hinges[j].Active)
                continue;
            float s = hinges[j].FlexSign * hinges[j].Rest;
            float extensionLimit = MathF.Min(s, MathF.Max(s - extendCap, minDeflection));
            hinges[j] = FabrikSolver.HingeLimit.Create(hinges[j].Rest, hinges[j].FlexSign, extensionLimit, MaxTotalFold);
        }

        float foldNeed = 0f;
        var scratch = limb.BestJoints;
        if (scratch.Length >= 3 && scratch.Length == m)
        {
            float3 seg0 = limb.Bones[start + 1].GlobalPosition - limb.Bones[start].GlobalPosition;
            seg0 -= nrm * float3.Dot(seg0, nrm);
            float3 reach = limb.ChainRestRootToEnd - nrm * float3.Dot(limb.ChainRestRootToEnd, nrm);
            if (seg0.LengthSquared > 1e-10f && reach.LengthSquared > 1e-10f)
            {
                for (int i = 0; i < m - 1; i++)
                    limb.ChainLengths[i] = limb.Lengths[start + i];
                scratch[0] = float3.Zero;
                scratch[1] = seg0.Normalized;
                float3 rootRef = float3.Zero;
                if (start > 0)
                    rootRef = limb.Bones[start].GlobalPosition - limb.Bones[start - 1].GlobalPosition;
                foldNeed = FabrikSolver.SeedPlanarFold(
                    scratch, limb.ChainLengths, hinges, float3.Zero,
                    reach.Normalized * (fold * limb.ChainLength), nrm,
                    start > 0, rootRef);
            }
        }

        float widest = 0f;
        for (int j = 0; j < m - 1; j++)
        {
            if (!hinges[j].Active)
                continue;
            float s = hinges[j].FlexSign * hinges[j].Rest;
            float flexLimit = MathF.Min(MathF.Max(s + foldNeed, s) + MinFlex, MaxTotalFold);
            flexLimit = MathF.Max(flexLimit, s);
            float extensionLimit = hinges[j].FlexSign * hinges[j].ExtendLimit;
            hinges[j] = FabrikSolver.HingeLimit.Create(hinges[j].Rest, hinges[j].FlexSign, extensionLimit, flexLimit);
            widest = MathF.Max(widest, flexLimit);
        }
        limb.MaxBendRadians = widest;
    }

    // SOLVE

    // Cache one legal deep fold and interpolate its angles from rest. Folding every hinge by the
    // same amount can trap alternating knee/hock bends in a local minimum and cause a visible snap.
    private void EnsureFoldPlan(ref LimbChain limb)
    {
        int count = limb.ChainLengths.Length;
        bool changed = !limb.HasFoldPlan || MathF.Abs(limb.PlanFold - MinLimbFoldScale) > 1e-5f;
        for (int i = 0; i < count && !changed; i++)
        {
            var current = limb.Hinges[i];
            var previous = limb.PlanLimits[i];
            changed = MathF.Abs(limb.ChainLengths[i] / limb.ChainLength - limb.PlanLengths[i]) > 1e-4f
                      || MathF.Abs(current.Rest - previous.Rest) > 1e-4f
                      || MathF.Abs(current.Min - previous.Min) > 1e-4f
                      || MathF.Abs(current.Max - previous.Max) > 1e-4f;
        }
        if (!changed)
            return;

        var normal = limb.RestPlaneNormal;
        var joints = limb.Joints;
        var origin = limb.RestOffsets[limb.ChainStart];
        for (int i = 0; i < joints.Length; i++)
        {
            float3 offset = limb.RestOffsets[limb.ChainStart + i] - origin;
            joints[i] = offset - normal * float3.Dot(offset, normal);
        }
        float3 goal = limb.ChainRestRootToEnd * System.Math.Clamp(MinLimbFoldScale, 0.05f, 1f);
        var shoulder = limb.Hinges[0];
        limb.Hinges[0] = default;
        FabrikSolver.SeedPlanarFold(joints, limb.ChainLengths, limb.Hinges, float3.Zero, goal, normal, false, float3.Zero);
        FabrikSolver.SolveHingeChain(joints, limb.ChainLengths, goal, normal, limb.Hinges,
            false, float3.Zero, limb.BestJoints, out _, endPinMaxError: 0f);
        limb.Hinges[0] = shoulder;
        for (int i = 0; i < count; i++)
        {
            limb.FoldAngles[i] = i == 0 ? 0f : FabrikSolver.SignedAngle(
                (joints[i] - joints[i - 1]).Normalized, (joints[i + 1] - joints[i]).Normalized, normal);
            limb.PlanLengths[i] = limb.ChainLengths[i] / limb.ChainLength;
            limb.PlanLimits[i] = limb.Hinges[i];
        }
        limb.PlanFold = MinLimbFoldScale;
        limb.HasFoldPlan = true;
    }

    private void SeedLimbPose(ref LimbChain limb, float3 root, float3 goal, float3 normal)
    {
        EnsureFoldPlan(ref limb);
        float3 reach = goal - root;
        float distance = reach.Length;
        float3 direction = reach.Normalized;
        float restSpan = BuildLimbPose(ref limb, root, direction, normal, 0f, true);
        bool folding = distance < restSpan;
        float lo = 0f, hi = 1f;
        for (int i = 0; i < 16; i++)
        {
            float mid = (lo + hi) * 0.5f;
            float span = BuildLimbPose(ref limb, root, direction, normal, mid, folding);
            if (folding ? span > distance : span < distance)
                lo = mid;
            else
                hi = mid;
        }
        float blend = (lo + hi) * 0.5f;
        BuildLimbPose(ref limb, root, direction, normal, blend, folding);
        var joints = limb.Joints;
        floatQ aim = FabrikSolver.FromToRotation(joints[^1] - root, reach, normal);
        for (int i = 1; i < joints.Length; i++)
            joints[i] = root + aim * (joints[i] - root);
        limb.LastFoldSeed = 0f;
        for (int i = 1; i < limb.Hinges.Length; i++)
            limb.LastFoldSeed = MathF.Max(limb.LastFoldSeed,
                MathF.Abs(limb.Hinges[i].Rest - (folding ? limb.FoldAngles[i] : limb.Hinges[i].ExtendLimit)) * blend);
    }

    private static float BuildLimbPose(ref LimbChain limb, float3 root, float3 direction, float3 normal, float blend, bool folding)
    {
        var joints = limb.Joints;
        joints[0] = root;
        for (int i = 0; i < limb.ChainLengths.Length; i++)
        {
            if (i > 0)
            {
                var hinge = limb.Hinges[i];
                float endAngle = folding ? limb.FoldAngles[i] : hinge.ExtendLimit;
                float angle = hinge.Rest + (endAngle - hinge.Rest) * blend;
                direction = FabrikSolver.RotateInPlane(direction, normal, angle);
            }
            joints[i + 1] = joints[i] + direction * limb.ChainLengths[i];
        }
        return float3.Distance(root, joints[^1]);
    }

    // Reset to the bind pose and re-measure it. Split out of Solve so the component can build its
    // targets FROM the rest measurements of this frame - stand heights, sole plane, girdle positions -
    // instead of from last frame's solved pose. Solve calls it itself when nobody has.
    public void BeginFrame()
    {
        if (!IsReady)
            return;
        FixTransforms();
        ReadPose();
        _frameBegun = true;
    }

    public void Solve(LimbTarget[] paws, in HeadTarget head, in ChassisTarget chassis)
    {
        if (!IsReady || paws == null || paws.Length < QuadLimb.Count)
            return;

        if (!_frameBegun)
            BeginFrame();
        _frameBegun = false;

        float3 pelvisTarget = _spine[0].GlobalPosition + LocomotionOffset;
        float3 chestTarget = _spine[^1].GlobalPosition + LocomotionOffset;
        if (chassis.Valid)
        {
            pelvisTarget = chassis.PelvisPosition + LocomotionOffset;
            chestTarget = chassis.ChestPosition + LocomotionOffset;
            pelvisTarget = LimitCrouchHeight(pelvisTarget, false, paws);
            chestTarget = LimitCrouchHeight(chestTarget, true, paws);

            // Move the common body ancestor so separate head and spine branches stay together.
            bool pelvisIsRoot = _spineRootIndex == 0;
            var bodyRoot = _bodyRoot ?? _spine[_spineRootIndex];
            float3 shift = (pelvisIsRoot ? pelvisTarget : chestTarget) - _spine[_spineRootIndex].GlobalPosition;
            WriteGlobalPosition(bodyRoot, bodyRoot.GlobalPosition + shift);
        }

        SolveSpineBeam(pelvisTarget, chestTarget, chassis);

        if (head.Valid && _neck.Count > 0)
        {
            SolveNeck(head.Position, head.PositionWeight);
            WriteHeadFacing(_neck[^1], head.Rotation, head.RotationWeight);
        }

        for (int i = 0; i < QuadLimb.Count; i++)
            SolveLimb(ref _limbs[i], paws[i]);
        RecordLeft();
    }

    // Unequal segment lengths impose a minimum reach even at maximum flexion. Keep the body above
    // that limit using planted support positions, so lifting a paw does not lift or tilt the body.
    private float3 LimitCrouchHeight(float3 anchor, bool front, LimbTarget[] paws)
    {
        var bodyBone = front ? _spine[^1] : _spine[0];
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            ref var limb = ref _limbs[i];
            ref var target = ref paws[i];
            if (QuadLimb.IsFront((QuadLimbId)i) != front || !limb.RestValid
                || !target.Valid || !target.GroundAlign || target.PositionWeight <= 1e-4f)
                continue;
            EnsureFoldPlan(ref limb);
            float3 direction = limb.ChainRestRootToEnd.Normalized;
            float minimum = BuildLimbPose(ref limb, float3.Zero, direction, limb.RestPlaneNormal, 1f, true);
            float3 offset = limb.Bones[limb.ChainStart].GlobalPosition - bodyBone.GlobalPosition;
            float3 support = target.HasSupportPosition ? target.SupportPosition : target.Position;
            float3 horizontal = anchor + offset - support;
            horizontal.y = 0f;
            float height = MathF.Sqrt(MathF.Max(minimum * minimum - horizontal.LengthSquared, 0f));
            anchor.y = MathF.Max(anchor.y, support.y + height - offset.y);
        }
        return anchor;
    }

    private int SpineIndex(int index) => _spineRootIndex == 0 ? index : _spine.Count - 1 - index;

    private void SolveSpineBeam(float3 pelvisTarget, float3 chestTarget, in ChassisTarget chassis)
    {
        int n = _spine.Count;
        if (n < 2)
            return;

        float3 rootTarget = _spineRootIndex == 0 ? pelvisTarget : chestTarget;
        float3 endTarget = _spineRootIndex == 0 ? chestTarget : pelvisTarget;
        float3 root = _spine[SpineIndex(0)].GlobalPosition;
        float3 restSpan = _spine[SpineIndex(n - 1)].GlobalPosition - root;
        float3 targetSpan = endTarget - rootTarget;
        floatQ turn = FabrikSolver.FromToRotation(restSpan, targetSpan, float3.Up);
        for (int i = 0; i < n; i++)
            _spineJoints[i] = rootTarget + turn * (_spine[SpineIndex(i)].GlobalPosition - root);

        // Seed from the authored curve in the target frame, then preserve every segment length.
        // The root-to-leaf pass is also the order in which rotations must reach the skeleton.
        FabrikSolver.SolveChain(_spineJoints, _spineLengths, endTarget, _spineLimits, _curBodyForward, iterations: 10,
            tolerance: MathF.Max(RestBodyLength * 0.0001f, 0.00001f));

        float rearRoll = chassis.Valid ? System.Math.Clamp(chassis.RearRoll, -MaxBodyRoll, MaxBodyRoll) : 0f;
        float frontRoll = chassis.Valid ? System.Math.Clamp(chassis.FrontRoll, -MaxBodyRoll, MaxBodyRoll) : 0f;
        float3 beamForward = chestTarget - pelvisTarget;
        if (beamForward.LengthSquared < 1e-8f)
            beamForward = _curBodyForward;
        beamForward = beamForward.Normalized;

        for (int i = 0; i < n; i++)
        {
            int index = SpineIndex(i);
            float3 dir = i < n - 1
                ? _spineJoints[i + 1] - _spineJoints[i]
                : _spineJoints[i] - _spineJoints[i - 1];
            float t = (float)index / (n - 1);
            float roll = rearRoll + (frontRoll - rearRoll) * t;
            float3 up = floatQ.AxisAngleRad(beamForward, roll) * float3.Up;
            ApplySpineSwing(_spine[index], dir, up);
        }
    }
    private void SolveNeck(float3 headPos, float positionWeight)
    {
        int n = _neck.Count;
        if (n < 2)
            return;

        positionWeight = System.Math.Clamp(positionWeight, 0f, 1f);
        if (positionWeight <= 1e-4f)
            return;

        if (n == 2)
        {
            float3 target = float3.Lerp(_neck[1].GlobalPosition, headPos, positionWeight);
            float3 dir = target - _neck[0].GlobalPosition;
            if (dir.LengthSquared > 1e-10f && positionWeight > 1e-3f)
                ApplySwing(_neck[0], dir);
            return;
        }

        for (int i = 0; i < n; i++)
            _neckJoints[i] = _neck[i].GlobalPosition;

        float3 anchor = _neckJoints[0];
        float3 goal = float3.Lerp(_neckJoints[n - 1], headPos, System.Math.Clamp(positionWeight, 0f, 1f));

        float total = 0f;
        for (int i = 0; i < n - 1; i++)
            total += _neckLengths[i];
        float3 toGoal = goal - anchor;
        float dist = toGoal.Length;
        if (dist > total * 0.98f && dist > 1e-6f)
            goal = anchor + toGoal / dist * (total * 0.98f);

        float cone = System.Math.Clamp(1f - NeckStiffness, 0.05f, 1f) * MaxSpineBendPerJoint * 2f;
        FabrikSolver.SolveChain(_neckJoints, _neckLengths, goal, cone, iterations: 6);

        for (int i = 0; i < n - 1; i++)
        {
            float3 dir = _neckJoints[i + 1] - _neckJoints[i];
            if (dir.LengthSquared > 1e-10f)
                ApplySwing(_neck[i], dir);
        }

    }

    // THE DIGITIGRADE LIMB SOLVE
    //
    // A four-segment leg with two bends in opposite directions is not a two-bone problem, and no single
    // pole vector describes it. What does describe it is the pose the artist authored, so this
    // transports that whole pose onto the reach direction and lets FABRIK refine it from there.
    //
    // It stays in the authored bend plane because the solver projects every joint onto that plane in
    // both passes, and it stays on the authored SIDE because every joint carries a signed hinge range
    // sized from its own rest deflection: a hock may fold further than the artist folded it, and may
    // straighten to within a few degrees of straight, and nothing else. The old version relied on the
    // seed's locality for the side and had no plane enforcement past the seed; a stalled solve walked
    // straight through both. -xlinka
    private void SolveLimb(ref LimbChain limb, in LimbTarget target)
    {
        if (!limb.RestValid || limb.Bones == null || limb.Bones.Length < 3 || !target.Valid)
        {
            return;
        }

        var paw = limb.Bones[^1];
        int start = limb.ChainStart;
        float3 limbRoot = limb.Bones[0].GlobalPosition;
        float weight = System.Math.Clamp(target.PositionWeight, 0f, 1f);
        if (weight <= 1e-4f)
        {
            ApplyPawTarget(ref limb, target);
            return;
        }

        // The rest paw position is carried on the LIVE root, not read back from ReadPose: the pelvis
        // write above moved every limb root after the rest pose was measured, and a stale rest end
        // would blend a partial weight toward where the paw was before the body stood up.
        float3 restEnd = paw.GlobalPosition;
        float3 goal = float3.Lerp(restEnd, target.Position, weight);

        // Girdle bone first, chain root second. The scapula takes a small clamped share of the swing
        // and the chain is rooted wherever that leaves the humerus.
        limb.LastScapulaSwing = 0f;
        float3 root = limbRoot;
        float3 rootReference = float3.Zero;
        bool hasRootReference = false;
        if (start > 0)
        {
            root = SolveScapula(ref limb, goal, out rootReference);
            hasRootReference = rootReference.LengthSquared > 1e-10f;
        }

        Stretch(ref limb, float3.Distance(root, goal));

        // Reach is the stretched chain length, not 0.98 of it. The 2% was there so SolveChain's
        // unreachable branch could never fire and straighten the seed; the hinge solver has no such
        // branch, and on a leg near extension 2% of its length is the paw hanging that far above the
        // floor. If the goal is still beyond the stretched reach, the chain solves toward the nearest
        // point it can touch and the paw ROTATION is still planted from the target's ground frame
        // below, so the pad faces the floor even when the leg is an inch short of it. -xlinka
        float3 toGoal = goal - root;
        float dist = toGoal.Length;
        limb.LastReachClamped = false;
        if (dist > limb.ChainLength && dist > 1e-6f)
        {
            goal = root + toGoal / dist * limb.ChainLength;
            toGoal = goal - root;
            dist = limb.ChainLength;
            limb.LastReachClamped = true;
        }
        if (toGoal.LengthSquared < 1e-10f)
            return;

        // The 3-arg overload: a paw swinging past its own root would otherwise flip the whole plane
        // discontinuously at the antiparallel crossing, and during a trot a leg does swing that far.
        floatQ swing = FabrikSolver.FromToRotation(limb.ChainRestRootToEnd, toGoal, limb.RestPlaneNormal);
        float3 planeNormal = swing * limb.RestPlaneNormal;

        // Pooled: these buffers were a fresh float3[n] per limb per frame.
        var joints = limb.Joints;
        int n = joints.Length;
        float3 chainRestBase = limb.RestOffsets[start];
        for (int i = 0; i < n; i++)
            joints[i] = root + swing * (limb.RestOffsets[start + i] - chainRestBase);

        if (planeNormal.LengthSquared > 1e-10f)
        {
            planeNormal = planeNormal.Normalized;
            for (int i = 1; i < n; i++)
            {
                float3 offset = joints[i] - root;
                joints[i] = root + (offset - planeNormal * float3.Dot(offset, planeNormal));
            }
        }

        // A bend goal nudges the plane rather than replacing it, so a hint can steer the knee without
        // ever being able to invert the joint. The solver holds the chain to a plane, so the plane it
        // is handed has to be the NUDGED one or the nudge would be flattened straight back out.
        if (target.HasBendGoal && BendGoalWeight > 1e-3f)
        {
            float3 goalDir = toGoal.Normalized;
            float3 hint = target.BendGoal - root;
            float3 inPlane = hint - goalDir * float3.Dot(hint, goalDir);
            if (inPlane.LengthSquared > 1e-8f)
            {
                inPlane = inPlane.Normalized;
                float blend = System.Math.Clamp(BendGoalWeight, 0f, 1f) * 0.5f;
                for (int i = 1; i < n - 1; i++)
                {
                    float3 offset = joints[i] - root;
                    float along = float3.Dot(offset, goalDir);
                    float bendDist = (offset - goalDir * along).Length;
                    float3 nudged = root + goalDir * along + inPlane * bendDist;
                    joints[i] = float3.Lerp(joints[i], nudged, blend);
                }
                planeNormal = RecoverPlaneNormal(joints, root, goalDir, planeNormal);
            }
        }

        for (int i = 0; i < n - 1; i++)
            limb.ChainLengths[i] = limb.Lengths[start + i];

        // Fold or straighten the seed to the target's span first; the iterative solve only polishes.
        // See SeedPlanarFold for why a crouch cannot be left to the iterations.
        SeedLimbPose(ref limb, root, goal, planeNormal);

        FabrikSolver.SolveHingeChain(
            joints, limb.ChainLengths, goal, planeNormal, limb.Hinges,
            hasRootReference, rootReference, limb.BestJoints, out limb.LastStats,
            iterations: System.Math.Max(LimbIterations, 1), tolerance: MathF.Max(LimbTolerance, 1e-6f),
            stallNudgeRadians: StallNudgeRadians);

        // What the SKELETON will show is not the solved joint list. Bones are written as rotations, so
        // the rendered chain is root + every segment at its exact length along the solved direction,
        // and the end-pin pass deliberately left the residual in the root segment's length. Rebuild
        // that chain, then aim it: one rigid rotation about the root that puts the rendered end on the
        // root-to-goal line. A rigid rotation preserves every hinge angle and the plane, and it turns
        // whatever residual was left into a purely radial one, which the triangle inequality makes no
        // larger than what was there. For a paw the radial error is the only kind that is nearly all
        // vertical, and it is the kind Stretch and the hinge sizing are already there to keep small.
        // -xlinka
        float3 prevOld = joints[0];
        joints[0] = root;
        for (int i = 0; i < n - 1; i++)
        {
            float3 dir = joints[i + 1] - prevOld;
            prevOld = joints[i + 1];
            float len = dir.Length;
            joints[i + 1] = len > 1e-8f ? joints[i] + dir / len * limb.ChainLengths[i] : joints[i];
        }
        float3 rendered = joints[n - 1] - root;
        float renderedLen = rendered.Length;
        limb.LastRadialShortfall = dist - renderedLen;
        limb.LastRenderedError = float3.Distance(joints[n - 1], goal);
        if (renderedLen > 1e-6f && limb.LastRenderedError > LimbTolerance)
        {
            floatQ aim = FabrikSolver.FromToRotation(rendered, toGoal, planeNormal);
            for (int i = 1; i < n; i++)
                joints[i] = root + aim * (joints[i] - root);
            limb.LastRenderedError = float3.Distance(joints[n - 1], goal);
        }

        for (int i = 0; i < n - 1; i++)
        {
            float3 dir = joints[i + 1] - joints[i];
            if (dir.LengthSquared > 1e-10f)
            {
                ApplySwing(limb.Bones[start + i], dir);
            }
        }

        ApplyPawTarget(ref limb, target);
    }

    private void ApplyPawTarget(ref LimbChain limb, in LimbTarget target)
    {
        var paw = limb.Bones[^1];
        float weight = System.Math.Clamp(target.RotationWeight, 0f, 1f);
        if (weight <= 1e-4f)
            return;

        if (_restRot.TryGetValue(paw, out var restPawRot))
        {
            if (target.GroundAlign)
            {
                WriteGlobalRotation(paw, floatQ.Slerp(paw.GlobalRotation, GroundPawRotation(restPawRot, target), weight).Normalized);
            }
            else
            {
                WriteGlobalRotation(paw, floatQ.Slerp(paw.GlobalRotation, target.Rotation, weight).Normalized);
            }
        }

        SolveToe(paw, limb.Toe, limb.ToeRel, target.StepLift);
    }

    // The scapula is a girdle bone, not a leg segment. It glides over the ribcage through a small arc
    // and it never reaches, so it is handled the way the biped handles its clavicle: a fraction of the
    // limb's swing, clamped, about the limb's own bend plane, and then the chain is rooted at the
    // humerus that swing carried. Rotating the LIVE direction rather than the captured one means a
    // scapula with no swing to do is not written at all and keeps following the chest. Returns the
    // humerus position; `solvedDir` is the reference the shoulder hinge is measured against. -xlinka
    private float3 SolveScapula(ref LimbChain limb, float3 goal, out float3 solvedDir)
    {
        var scapula = limb.Bones[0];
        float3 scapPos = scapula.GlobalPosition;
        var upper = limb.Bones[limb.ChainStart];
        float3 liveDir = upper.GlobalPosition - scapPos;
        float len = liveDir.Length;
        solvedDir = liveDir.LengthSquared > 1e-10f ? liveDir.Normalized : float3.Zero;
        if (solvedDir.LengthSquared < 0.5f || len < 1e-6f)
            return upper.GlobalPosition;

        float3 nrm = limb.RestPlaneNormal;
        float3 restReach = limb.RestRootToEnd - nrm * float3.Dot(limb.RestRootToEnd, nrm);
        float3 toGoal = goal - scapPos;
        toGoal -= nrm * float3.Dot(toGoal, nrm);
        if (restReach.LengthSquared > 1e-10f && toGoal.LengthSquared > 1e-10f)
        {
            float swingAngle = FabrikSolver.SignedAngle(restReach.Normalized, toGoal.Normalized, nrm);
            float angle = System.Math.Clamp(swingAngle * ScapulaWeight, -MaxScapulaSwing, MaxScapulaSwing);
            limb.LastScapulaSwing = angle;
            if (MathF.Abs(angle) > 1e-5f)
            {
                float alongNormal = float3.Dot(solvedDir, nrm);
                float3 inPlane = solvedDir - nrm * alongNormal;
                float inPlaneLen = inPlane.Length;
                if (inPlaneLen > 1e-6f)
                {
                    float3 rotated = FabrikSolver.RotateInPlane(inPlane / inPlaneLen, nrm, angle) * inPlaneLen
                                     + nrm * alongNormal;
                    WriteGlobalRotation(scapula, FabrikSolver.FromToRotation(solvedDir, rotated) * scapula.GlobalRotation);
                    solvedDir = rotated.Normalized;
                }
            }
        }

        solvedDir = upper.GlobalPosition - limb.Bones[limb.ChainStart - 1].GlobalPosition;
        return upper.GlobalPosition;
    }

    // Plane normal of a seed that a bend hint has rotated: from the joint that sticks out furthest from
    // the root-to-goal line, signed to agree with the plane it started in.
    private static float3 RecoverPlaneNormal(float3[] joints, float3 root, float3 goalDir, float3 fallback)
    {
        float3 bestOffset = float3.Zero;
        float bestDist = 0f;
        for (int i = 1; i < joints.Length - 1; i++)
        {
            float3 offset = joints[i] - root;
            float d = (offset - goalDir * float3.Dot(offset, goalDir)).Length;
            if (d > bestDist)
            {
                bestDist = d;
                bestOffset = offset;
            }
        }
        float3 normal = float3.Cross(goalDir, bestOffset);
        if (normal.LengthSquared < 1e-10f)
            return fallback;
        normal = normal.Normalized;
        return float3.Dot(normal, fallback) < 0f ? -normal : normal;
    }

    // Let a limb over-extend a hair rather than hard-clamping, eased in with a smoothstep before full
    // reach so there is no derivative jump at exactly full extension (that jump is a visible pop).
    // Measured and applied on the CHAIN: the scapula is not part of the reach and does not stretch.
    private void Stretch(ref LimbChain limb, float dist)
    {
        if (limb.ChainLength < 1e-5f)
            return;
        float ratio = dist / limb.ChainLength;
        const float kStart = 0.9f;
        if (ratio <= kStart)
            return;

        float t = System.Math.Clamp((ratio - kStart) / MathF.Max(MaxStretch - kStart, 1e-4f), 0f, 1f);
        float ease = t * t * (3f - 2f * t);
        float scale = 1f + (MaxStretch - 1f) * ease;

        int start = limb.ChainStart;
        limb.TotalLength = 0f;
        limb.ChainLength = 0f;
        for (int i = 0; i < limb.Lengths.Length; i++)
        {
            if (i >= start)
            {
                var child = limb.Bones[i + 1];
                // Local offsets preserve the armature's scale; BeginFrame restores the bind lengths.
                child.LocalPosition.SetValueSilently(child.LocalPosition.Value * scale, change: true);
                limb.Lengths[i] *= scale;
                limb.ChainLength += limb.Lengths[i];
            }
            limb.TotalLength += limb.Lengths[i];
        }
    }

    // Plant orientation: the paw's AUTHORED rest pose, yawed so it follows the gait facing, then tilted
    // for the slope. The authored pitch and roll are never touched - aligning a bone's +Y to the ground
    // normal instead rolls every paw whose Y runs along the segment sole-backward, which is exactly what
    // a digitigrade rig looks like.
    private floatQ GroundPawRotation(floatQ restPawRot, in LimbTarget target)
    {
        float3 normal = target.GroundNormal.LengthSquared > 1e-6f ? target.GroundNormal.Normalized : float3.Up;

        float3 forward = target.HasGroundRotation
            ? target.GroundRotation * float3.Backward
            : target.GroundForward.LengthSquared > 1e-6f ? target.GroundForward : _curBodyForward;
        forward.y = 0f;
        if (forward.LengthSquared < 1e-6f)
        {
            forward = _curBodyForward;
            forward.y = 0f;
            if (forward.LengthSquared < 1e-6f)
                forward = _restBodyForward;
        }
        forward = forward.LengthSquared > 1e-6f ? forward.Normalized : float3.Backward;

        float3 restBody = _restBodyForward;
        restBody.y = 0f;
        restBody = restBody.LengthSquared > 1e-6f ? restBody.Normalized : float3.Backward;

        floatQ rot = FabrikSolver.FromToRotation(restBody, forward, float3.Up) * restPawRot;

        float upDot = System.Math.Clamp(float3.Dot(float3.Up, normal), -1f, 1f);
        if (upDot < 0.9999f)
        {
            float3 axis = float3.Cross(float3.Up, normal);
            if (axis.LengthSquared > 1e-8f)
            {
                float angle = MathF.Min(MathF.Acos(upDot), MathF.PI * 0.25f);
                rot = floatQ.AxisAngleRad(axis.Normalized, angle) * rot;
            }
        }

        return rot;
    }

    private void SolveToe(Slot paw, Slot? toe, floatQ toeRel, float stepLift)
    {
        if (paw == null || toe == null || toe.IsDestroyed)
            return;

        floatQ pawRot = paw.GlobalRotation;
        floatQ toeRot = pawRot * toeRel;
        if (stepLift > 1e-3f)
        {
            float3 right = pawRot * float3.Right;
            toeRot = floatQ.AxisAngle(right, stepLift * ToeCurlRadians) * toeRot;
        }
        WriteGlobalRotation(toe, toeRot);
    }

    // Clamp view yaw relative to the body while preserving the head's authored axes and headset tilt.
    private void WriteHeadFacing(Slot head, in floatQ lookRotation, float rotationWeight)
    {
        rotationWeight = System.Math.Clamp(rotationWeight, 0f, 1f);
        if (head == null || head.IsDestroyed || rotationWeight <= 1e-4f)
            return;
        if (!_restRot.TryGetValue(head, out var restRot))
        {
            WriteGlobalRotation(head, lookRotation);
            return;
        }

        float3 look = lookRotation * float3.Backward;
        if (look.LengthSquared < 1e-8f)
        {
            WriteGlobalRotation(head, restRot);
            return;
        }
        look = look.Normalized;

        float3 lookFlat = look;
        lookFlat.y = 0f;
        if (lookFlat.LengthSquared < 1e-6f)
            lookFlat = _curBodyForward;
        lookFlat = lookFlat.LengthSquared > 1e-6f ? lookFlat.Normalized : _restBodyForward;

        float3 bodyFlat = _curBodyForward;
        bodyFlat.y = 0f;
        bodyFlat = bodyFlat.LengthSquared > 1e-6f ? bodyFlat.Normalized : _restBodyForward;
        float bodyYaw = MathF.Atan2(bodyFlat.x, bodyFlat.z);
        float lookYaw = MathF.Atan2(lookFlat.x, lookFlat.z);
        float relativeYaw = System.Math.Clamp(WrapPi(lookYaw - bodyYaw), -MaxHeadYaw, MaxHeadYaw);
        float yawCorrection = WrapPi(bodyYaw + relativeYaw - lookYaw);
        floatQ constrainedLook = floatQ.AxisAngleRad(float3.Up, yawCorrection) * lookRotation;
        floatQ aim = FabrikSolver.ViewRotationDelta(_restBodyForward, constrainedLook);
        WriteGlobalRotation(head, floatQ.Slerp(head.GlobalRotation, aim * restRot, rotationWeight).Normalized);
    }

    private void ApplySwing(Slot bone, float3 solvedDir)
    {
        if (bone == null || solvedDir.LengthSquared < 1e-8f)
            return;
        if (!_restDir.TryGetValue(bone, out var restDir) || !_restRot.TryGetValue(bone, out var restRot))
            return;

        var swing = FabrikSolver.FromToRotation(restDir, solvedDir);
        WriteGlobalRotation(bone, swing * restRot);
    }

    // Body up remains perpendicular to a horizontal spine and gives its twist a stable axis.
    private void ApplySpineSwing(Slot bone, float3 solvedDir, float3 bodyUp)
    {
        if (bone == null || solvedDir.LengthSquared < 1e-8f)
            return;
        if (!_restDir.TryGetValue(bone, out var restDir) || !_restRot.TryGetValue(bone, out var restRot))
            return;

        floatQ swing = FabrikSolver.FromToRotation(restDir, solvedDir, _restBodyUp);
        floatQ swung = swing * restRot;
        float3 axis = solvedDir.Normalized;
        float3 transported = swing * _restBodyUp;
        float3 current = transported - axis * float3.Dot(transported, axis);
        float3 goal = bodyUp - axis * float3.Dot(bodyUp, axis);
        if (current.LengthSquared > 0.05f && goal.LengthSquared > 0.05f)
        {
            current = current.Normalized;
            goal = goal.Normalized;
            float angle = MathF.Atan2(float3.Dot(float3.Cross(current, goal), axis), float3.Dot(current, goal));
            swung = floatQ.AxisAngleRad(axis, angle) * swung;
        }
        WriteGlobalRotation(bone, swung);
    }
    // Avoid notifying transform listeners when the rotation has not changed.
    private static void WriteGlobalRotation(Slot bone, in floatQ rotation)
    {
        if (bone == null || bone.IsDestroyed)
            return;

        var parent = bone.Parent;
        var local = parent != null ? parent.GlobalRotationToLocal(rotation) : rotation;
        float dot = floatQ.Dot(local, bone.LocalRotation.Value);
        if (1f - (dot < 0 ? -dot : dot) > 1e-9f)
            bone.LocalRotation.SetValueSilently(local, change: true);
    }

    // Only the common body root receives a global position.
    private static void WriteGlobalPosition(Slot bone, in float3 position)
    {
        if (bone == null || bone.IsDestroyed)
            return;

        var parent = bone.Parent;
        var local = parent != null ? parent.GlobalPointToLocal(position) : position;
        if ((bone.LocalPosition.Value - local).LengthSquared > 1e-12f)
            bone.LocalPosition.SetValueSilently(local, change: true);
    }

    // Live limb root and the furthest a paw can be from it. The component uses this to decide whether a
    // tracked hand or foot is anywhere a paw could actually be before it lets that tracker own the leg.
    public bool TryGetLimbReach(QuadLimbId id, out float3 root, out float reach)
    {
        ref var limb = ref _limbs[(int)id];
        root = float3.Zero;
        reach = 0f;
        if (!limb.RestValid || limb.Bones == null || limb.Bones.Length == 0)
            return false;
        var rootBone = limb.Bones[0];
        if (rootBone == null || rootBone.IsDestroyed)
            return false;
        root = rootBone.GlobalPosition;
        reach = limb.TotalLength * MathF.Max(MaxStretch, 1f);
        return reach > 1e-5f;
    }

    private static float WrapPi(float angle)
    {
        while (angle > MathF.PI) angle -= MathF.PI * 2f;
        while (angle < -MathF.PI) angle += MathF.PI * 2f;
        return angle;
    }

    internal bool TryGetLimbInfo(QuadLimbId id, out int boneCount, out float length, out float bendDegrees)
    {
        ref var limb = ref _limbs[(int)id];
        boneCount = limb.Bones?.Length ?? 0;
        length = limb.TotalLength;
        bendDegrees = limb.MaxBendRadians * (180f / MathF.PI);
        return limb.RestValid;
    }

    // Rest paw position relative to the limb root (Bones[0], the scapula on a front limb that has one),
    // world-oriented as captured this frame. Zero when the limb has no valid rest.
    public float3 RestPawOffset(QuadLimbId id)
    {
        ref var limb = ref _limbs[(int)id];
        if (!limb.RestValid || limb.RestOffsets == null || limb.RestOffsets.Length == 0)
            return float3.Zero;
        return limb.RestOffsets[^1];
    }

    // One line per limb for the solve-state log: every hinge's rest and range in degrees, and what the
    // last solve did with them. Allocates; call it from a diagnostic path only.
    public string DescribeLimbConstraints(QuadLimbId id)
    {
        ref var limb = ref _limbs[(int)id];
        var sb = new System.Text.StringBuilder(256);
        sb.Append(QuadLimb.Name(id));
        if (!limb.RestValid || limb.Bones == null || limb.Bones.Length == 0)
        {
            sb.Append(" rest=INVALID bones=").Append(limb.Bones?.Length ?? 0);
            return sb.ToString();
        }

        const float Deg = 180f / MathF.PI;
        sb.Append(" bones=").Append(limb.Bones.Length);
        sb.Append(" chain=").Append(limb.Joints.Length).Append('@').Append(limb.ChainStart);
        sb.Append(" scapula=").Append(limb.ChainStart > 0 ? "clamped" : "none");
        if (limb.ChainStart > 0)
            sb.Append('(').Append((limb.LastScapulaSwing * Deg).ToString("F1")).Append("deg)");
        sb.Append(" len=").Append(limb.TotalLength.ToString("F3"));
        sb.Append(" chainLen=").Append(limb.ChainLength.ToString("F3"));
        sb.Append(" clearance=").Append(PawGroundClearance[(int)id].ToString("F3"));
        sb.Append(" soleOffset=").Append(SoleOffset.ToString("F3"));
        sb.Append(" normal=").Append(limb.RestPlaneNormal.ToString());

        sb.Append(" hinges=[");
        for (int j = 0; j < limb.Hinges.Length; j++)
        {
            ref var h = ref limb.Hinges[j];
            if (j > 0)
                sb.Append(' ');
            sb.Append(j).Append(':');
            if (!h.Active)
            {
                sb.Append("free");
                continue;
            }
            sb.Append("rest=").Append((h.Rest * Deg).ToString("F0"));
            sb.Append(" range=[").Append((h.Min * Deg).ToString("F0")).Append(',')
              .Append((h.Max * Deg).ToString("F0")).Append(']');
            sb.Append(" fold=").Append(h.FlexSign > 0f ? '+' : '-');
        }
        sb.Append(']');

        ref var st = ref limb.LastStats;
        sb.Append(" last(foldSeed=").Append((limb.LastFoldSeed * Deg).ToString("F1")).Append("deg");
        sb.Append(" iters=").Append(st.Iterations);
        sb.Append(" best=").Append(st.BestIteration);
        sb.Append(" err=").Append(st.EndError == float.MaxValue ? "n/a" : st.EndError.ToString("F4"));
        sb.Append(" rendered=").Append(limb.LastRenderedError.ToString("F4"));
        sb.Append(" radial=").Append(limb.LastRadialShortfall.ToString("F4"));
        sb.Append(st.Converged ? " converged" : st.Stalled ? " STALLED" : " ran-out");
        if (st.EndPinned)
            sb.Append(" end-pinned");
        if (st.Nudges > 0)
            sb.Append(" nudges=").Append(st.Nudges);
        if (limb.LastReachClamped)
            sb.Append(" REACH-CLAMPED");
        sb.Append(')');
        return sb.ToString();
    }
}
