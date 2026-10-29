// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Avatar.IK;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

// Computes avatar calibration reference poses (view, hand grips, feet, pelvis) directly from a
// HumanoidRig, so the avatar can be set up automatically with no manual placement.
//
// The key over a naive "copy the bone's rotation" approach is that bone rotations are arbitrary per
// model - a raw hand-bone rotation makes a useless grip. Instead each reference frame is rebuilt
// from the body geometry: facing from the spine + shoulder line, grips from the forearm + arm-bend
// plane, feet/pelvis flattened to the ground. Results feed AvatarReferencePoints that
// AvatarIK reads.
public static class AvatarCalibration
{
    // A computed reference pose, expressed in the avatar root's local space.
    public struct RefPose
    {
        public bool Valid;
        public float3 LocalPosition;
        public floatQ LocalRotation;
    }

    // Eyes sit a little in front of the head bone.
    private const float ViewForwardOffset = 0.06f;

    // Default tool-anchor offsets, in HAND GRIP local space: the tool point 15cm out along the controller,
    // the grab point 7.5cm out and 2cm under. Forward is local -Z, hence the negative Z.
    //
    // Shared with AvatarStudio on purpose: the studio spawns its draggable anchors at these offsets and
    // the auto path below bakes them at the same place, so an avatar built by dragging the markers
    // without touching the anchors is identical to one built with no markers at all. -xlinka
    public static readonly float3 ToolAnchorGripOffset = new(0f, 0f, -0.15f);
    public static readonly float3 GrabAnchorGripOffset = new(0f, -0.02f, -0.075f);

    // Derive the body's world axes from the rig: up along the spine,
    // forward the facing direction (disambiguated by the head), right
    // the shoulder line. Falls back to world axes / the head's facing when bones are missing.
    public static bool TryComputeBodyAxes(HumanoidRig rig, out float3 up, out float3 right, out float3 forward)
    {
        up = float3.Up;
        right = float3.Right;
        forward = float3.Backward;
        if (rig == null)
            return false;

        var head = rig.TryGetBone(BodyNode.Head);
        if (head == null || head.IsDestroyed)
            return false;

        var hips = rig.TryGetBone(BodyNode.Hips);
        if (hips != null && !hips.IsDestroyed)
        {
            var u = head.GlobalPosition - hips.GlobalPosition;
            if (u.LengthSquared > 1e-6f)
                up = u.Normalized;
        }

        // Forward from the rig's geometric front. GuessForwardAxis builds it from the shoulder line and
        // disambiguates the sign against the toes, so it stays correct even when the rig's left/right arm
        // bones are authored on the swapped physical side - which would otherwise reverse a raw
        // Cross(up, shoulder-line) and place every reference (view/grips/feet) 180 degrees backward. -xlinka
        float3 fwd;
        var geometricFwd = rig.GuessForwardAxis();
        if (geometricFwd.HasValue && geometricFwd.Value.LengthSquared > 1e-6f)
        {
            fwd = geometricFwd.Value;
        }
        else
        {
            var leftUpper = rig.TryGetBone(BodyNode.LeftUpperArm);
            var rightUpper = rig.TryGetBone(BodyNode.RightUpperArm);
            if (leftUpper != null && rightUpper != null && !leftUpper.IsDestroyed && !rightUpper.IsDestroyed)
            {
                var r = rightUpper.GlobalPosition - leftUpper.GlobalPosition;   // toward the avatar's right
                right = r.LengthSquared > 1e-6f ? r.Normalized : float3.Right;
                fwd = float3.Cross(up, right);                                  // points out the front
            }
            else
            {
                fwd = head.GlobalRotation * float3.Backward;
            }
        }
        if (fwd.LengthSquared < 1e-6f)
            fwd = float3.Backward;
        fwd = fwd.Normalized;

        forward = fwd;
        right = float3.Cross(forward, up);
        right = right.LengthSquared > 1e-6f ? right.Normalized : float3.Right;
        return true;
    }

    // Point the avatar root's forward (-Z) at the body's geometric front WITHOUT moving anything visibly:
    // the root frame is yawed onto the mesh front by the exact delta and every direct child is restored to
    // its world pose. Equip resets the root to identity, so the root frame IS the worn facing - this is what
    // makes an avatar walk snout-first regardless of how the model was authored or dropped in the world.
    // Exact-angle (no binary 180 guess), yaw-only (root stays upright), idempotent. Run it before references
    // are baked; re-running after only rewrites the same frame. Returns true when the frame moved. -xlinka
    public static bool AlignAvatarFacing(Slot avatarRoot, HumanoidRig rig)
    {
        if (avatarRoot == null || avatarRoot.IsDestroyed || rig == null || rig.IsDestroyed)
            return false;

        var geom = rig.GuessForwardAxis();
        if (!geom.HasValue || geom.Value.LengthSquared < 1e-6f)
            return false;

        float3 front = geom.Value;
        front.y = 0f;
        float3 rootFwd = avatarRoot.GlobalRotation * float3.Backward;
        rootFwd.y = 0f;
        if (front.LengthSquared < 1e-6f || rootFwd.LengthSquared < 1e-6f)
            return false;
        front = front.Normalized;
        rootFwd = rootFwd.Normalized;

        float delta = System.MathF.Atan2(front.x, front.z) - System.MathF.Atan2(rootFwd.x, rootFwd.z);
        while (delta > System.MathF.PI) delta -= 2f * System.MathF.PI;
        while (delta < -System.MathF.PI) delta += 2f * System.MathF.PI;
        if (System.MathF.Abs(delta) < 0.01f)
            return false;

        // Counter-restore the children so the world appearance is untouched - only the FRAME turns. The
        // references/markers/bones are all read as live transforms, so they stay consistent by construction.
        var restore = new System.Collections.Generic.List<(Slot child, float3 pos, floatQ rot)>();
        foreach (var child in avatarRoot.Children)
        {
            if (child != null && !child.IsDestroyed)
                restore.Add((child, child.GlobalPosition, child.GlobalRotation));
        }

        avatarRoot.GlobalRotation = floatQ.AxisAngleRad(float3.Up, delta) * avatarRoot.GlobalRotation;

        foreach (var (child, pos, rot) in restore)
        {
            child.GlobalPosition = pos;
            child.GlobalRotation = rot;
        }

        Lumora.Core.Logging.Logger.Debug(
            $"[IK-FACING-ALIGN] root '{avatarRoot.SlotName.Value}' frame yawed {delta * 180f / System.MathF.PI:F1} deg " +
            $"onto body front={front} (root forward was {rootFwd}); world appearance unchanged");
        return true;
    }

    public static RefPose ComputeView(Slot avatarRoot, HumanoidRig rig, QuadrupedRig? quadruped = null)
    {
        quadruped ??= avatarRoot?.GetComponent<QuadrupedRig>() ?? avatarRoot?.GetComponentInChildren<QuadrupedRig>();
        var head = quadruped?.TryGetBone(QuadNode.Head) ?? rig?.TryGetBone(BodyNode.Head);
        if (avatarRoot == null || head == null || head.IsDestroyed)
            return default;

        TryComputeBodyAxes(rig!, out _, out _, out var forward);
        if (quadruped != null)
            forward = quadruped.BodyForward;
        float3 flat = Flatten(forward);
        var left = rig?.TryGetBone(BodyNode.LeftEye);
        var right = rig?.TryGetBone(BodyNode.RightEye);
        float3 worldPos;
        if (left is { IsDestroyed: false } && right is { IsDestroyed: false })
            worldPos = (left.GlobalPosition + right.GlobalPosition) * 0.5f;
        else if ((left ?? right) is { IsDestroyed: false } eye)
        {
            var across = float3.Cross(flat, float3.Up);
            worldPos = eye.GlobalPosition - across * float3.Dot(eye.GlobalPosition - head.GlobalPosition, across);
        }
        else
        {
            float scale = System.MathF.Abs(avatarRoot.GlobalScale.y);
            worldPos = head.GlobalPosition + (flat * ViewForwardOffset + float3.Up * 0.07f) * scale;
        }
        return ToLocal(avatarRoot, worldPos, BuildRotation(flat, float3.Up));
    }

    // The view the creator actually set up, when the avatar still carries it.
    //
    // An imported package keeps a head pose proxy whose parent was seated at the headset reference at
    // creation - eye position, level, facing forward - and whose "Target" child was seated at the head
    // bone. The importer re-seats that parent from the bone, so its position is the creator's own eye
    // point, and the geometric guess in ComputeView (an eye midpoint pushed 6 cm out) has no business
    // overriding it. Rotation is the level yaw of the proxy's facing: numerically the proxy faces +Z, the
    // package's forward, and BuildRotation turns that into our -Z facing frame. Only a proxy with a
    // Target child counts; the ones AvatarIK grows for itself have none, so every avatar built here keeps
    // the geometric view. -xlinka
    public static RefPose ComputeViewFromHeadProxy(Slot avatarRoot, HumanoidRig rig)
    {
        var proxy = FindSeatedHeadProxy(avatarRoot);
        if (proxy == null || rig?.TryGetBone(BodyNode.Head) == null)
            return default;

        float3 facing = Flatten(proxy.GlobalRotation * float3.Forward);
        return ToLocal(avatarRoot, proxy.GlobalPosition, BuildRotation(facing, float3.Up));
    }

    private static Slot? FindSeatedHeadProxy(Slot avatarRoot)
    {
        if (avatarRoot == null || avatarRoot.IsDestroyed)
            return null;
        foreach (var driver in avatarRoot.GetComponentsInChildren<AvatarPoseDriver>())
        {
            if (driver.IsDestroyed || driver.Node.Value != BodyNode.Head || driver.Slot == null || driver.Slot.IsDestroyed)
                continue;
            var target = driver.Slot.FindChild("Target", recursive: false);
            if (target != null && !target.IsDestroyed)
                return driver.Slot;
        }
        return null;
    }

    public static RefPose ComputeQuadBone(Slot avatarRoot, QuadrupedRig rig, QuadNode node)
    {
        var bone = rig.TryGetBone(node);
        return bone == null || bone.IsDestroyed ? default
            : ToLocal(avatarRoot, bone.GlobalPosition, BuildRotation(Flatten(rig.BodyForward), float3.Up));
    }

    public static Slot AutoPlaceQuadReferences(Slot avatarRoot, QuadrupedRig rig, HumanoidRig? faceRig)
    {
        var existing = avatarRoot.FindChild("AvatarReferences", recursive: false);
        if (existing != null && !existing.IsDestroyed)
            existing.Destroy();
        var root = avatarRoot.AddSlot("AvatarReferences");
        Place(root, AvatarReferenceKind.View, "View", ComputeView(avatarRoot, faceRig!, rig));
        Place(root, AvatarReferenceKind.Pelvis, "Pelvis", ComputeQuadBone(avatarRoot, rig, QuadNode.Pelvis));
        Place(root, AvatarReferenceKind.FrontLeftPaw, "FrontLeftPaw", ComputeQuadBone(avatarRoot, rig, QuadNode.FrontLeftPaw));
        Place(root, AvatarReferenceKind.FrontRightPaw, "FrontRightPaw", ComputeQuadBone(avatarRoot, rig, QuadNode.FrontRightPaw));
        Place(root, AvatarReferenceKind.RearLeftPaw, "RearLeftPaw", ComputeQuadBone(avatarRoot, rig, QuadNode.RearLeftPaw));
        Place(root, AvatarReferenceKind.RearRightPaw, "RearRightPaw", ComputeQuadBone(avatarRoot, rig, QuadNode.RearRightPaw));
        return root;
    }

    // Canonical hand socket: -Z runs wrist-to-fingers and +Y runs toward the thumb side of the palm,
    // which is the OpenXR grip frame, so a tracked controller maps onto it with no offset.
    // These anatomical axes are independent of the model's authored bone rotations.
    //
    // The roll comes from the knuckle line first (index to pinky proximal: rigid, pose-independent), the
    // thumb bone second (it moves with however the thumb was posed), and for a rig with no finger bones
    // at all the body's FORWARD. A T- or A-pose rig rests with its palms down or in, so its thumbs point
    // at the front; the body's up is the back of such a hand, and a grip rolled onto that wears the
    // controller ninety degrees off with the thumb out sideways. -xlinka
    public static RefPose ComputeHandGrip(Slot avatarRoot, HumanoidRig rig, bool rightSide)
        => ComputeHandGripCore(avatarRoot, rig, rightSide, thumbFirstBodyUp: false);

    // The generator that wrote markers between the palm-normal one and the current one: thumb bone
    // first, knuckle line second, body UP for a fingerless hand. Kept exactly so
    // UpgradeLegacyHandReferences can recognise what it produced; never used for new markers.
    private static RefPose ComputeThumbFirstHandGrip(Slot avatarRoot, HumanoidRig rig, bool rightSide)
        => ComputeHandGripCore(avatarRoot, rig, rightSide, thumbFirstBodyUp: true);

    private static RefPose ComputeHandGripCore(Slot avatarRoot, HumanoidRig rig, bool rightSide, bool thumbFirstBodyUp)
    {
        var side = rightSide ? Chirality.Right : Chirality.Left;
        var hand = rig?.TryGetBone(side.HandNode());
        if (avatarRoot == null || hand == null || hand.IsDestroyed)
            return default;

        var lower = rig!.TryGetBone(rightSide ? BodyNode.RightLowerArm : BodyNode.LeftLowerArm);
        var middle = FirstBone(rig, new[] {
            FingerType.Middle.ComposeFinger(FingerSegmentType.Proximal, side),
            FingerType.Ring.ComposeFinger(FingerSegmentType.Proximal, side) });
        var thumb = FirstBone(rig, new[] {
            FingerType.Thumb.ComposeFinger(FingerSegmentType.Proximal, side),
            FingerType.Thumb.ComposeFinger(FingerSegmentType.Metacarpal, side),
            FingerType.Thumb.ComposeFinger(FingerSegmentType.Distal, side) });
        var index = rig.TryGetBone(FingerType.Index.ComposeFinger(FingerSegmentType.Proximal, side));
        var pinky = rig.TryGetBone(FingerType.Pinky.ComposeFinger(FingerSegmentType.Proximal, side));

        float3 origin = hand.GlobalPosition;
        float3 forward = middle != null ? middle.GlobalPosition - origin
            : lower != null && !lower.IsDestroyed ? origin - lower.GlobalPosition
            : hand.GlobalRotation * float3.Backward;
        if (forward.LengthSquared < 1e-10f)
            forward = hand.GlobalRotation * float3.Backward;
        forward = forward.Normalized;

        float3 knuckleAxis = index != null && pinky != null && !index.IsDestroyed && !pinky.IsDestroyed
            ? Reject(index.GlobalPosition - pinky.GlobalPosition, forward)
            : float3.Zero;
        float3 thumbBoneAxis = thumb != null ? Reject(thumb.GlobalPosition - origin, forward) : float3.Zero;

        float3 thumbAxis;
        if (thumbFirstBodyUp)
        {
            thumbAxis = thumbBoneAxis.LengthSquared >= 1e-10f ? thumbBoneAxis : knuckleAxis;
            if (thumbAxis.LengthSquared < 1e-10f)
            {
                TryComputeBodyAxes(rig, out var up, out _, out _);
                thumbAxis = Reject(up, forward);
            }
        }
        else
        {
            thumbAxis = knuckleAxis.LengthSquared >= 1e-10f ? knuckleAxis : thumbBoneAxis;
            if (thumbAxis.LengthSquared < 1e-10f)
            {
                TryComputeBodyAxes(rig, out _, out _, out var bodyForward);
                thumbAxis = Reject(bodyForward, forward);
            }
        }
        if (thumbAxis.LengthSquared < 1e-10f)
            thumbAxis = avatarRoot.GlobalRotation * float3.Backward;

        // Roll about the pointing axis explicitly. An unconstrained 180-degree FromToRotation
        // can choose a different axis and reverse the fingers on a palm-down rest pose.
        var swing = FabrikSolver.FromToRotation(float3.Backward, forward);
        var currentUp = swing * float3.Up;
        thumbAxis = (thumbAxis - forward * float3.Dot(thumbAxis, forward)).Normalized;
        float roll = System.MathF.Atan2(float3.Dot(forward, float3.Cross(currentUp, thumbAxis)),
            float3.Dot(currentUp, thumbAxis));
        var rotation = (floatQ.AxisAngleRad(forward, roll) * swing).Normalized;
        return ToLocal(avatarRoot, origin, rotation);
    }

    // Two earlier generators wrote grips this one disagrees with: the first used the palm normal as
    // grip up, the second the thumb bone with body up for a fingerless hand. Recognize only their
    // exact generated poses, so existing imports repair themselves on equip without replacing creator
    // adjustments.
    public static void UpgradeLegacyHandReferences(Slot avatarRoot, HumanoidRig rig)
    {
        var root = avatarRoot.FindChild("AvatarReferences", recursive: false);
        if (root == null || root.IsDestroyed)
            return;
        foreach (bool rightSide in new[] { false, true })
        {
            var kind = rightSide ? AvatarReferenceKind.RightHandGrip : AvatarReferenceKind.LeftHandGrip;
            var marker = root.FindChild(kind.ToString(), recursive: false);
            if (marker?.GetComponent<AvatarReferencePoint>()?.Kind.Value != kind)
                continue;
            var oldPose = ComputeLegacyHandGrip(avatarRoot, rig, rightSide);
            if (!Matches(marker, oldPose))
            {
                oldPose = ComputeThumbFirstHandGrip(avatarRoot, rig, rightSide);
                if (!Matches(marker, oldPose))
                    continue;
            }
            var newPose = ComputeHandGrip(avatarRoot, rig, rightSide);
            if (!newPose.Valid || Matches(marker, newPose))
                continue;
            Logging.Logger.Debug($"AvatarCalibration: {(rightSide ? "right" : "left")} hand grip on '{avatarRoot.SlotName.Value}' was a generated marker from an older grip rule; re-placed with the current one");

            UpgradeAnchor(rightSide ? AvatarReferenceKind.RightHandToolAnchor : AvatarReferenceKind.LeftHandToolAnchor, ToolAnchorGripOffset);
            UpgradeAnchor(rightSide ? AvatarReferenceKind.RightHandGrabAnchor : AvatarReferenceKind.LeftHandGrabAnchor, GrabAnchorGripOffset);
            Apply(marker, newPose);

            void UpgradeAnchor(AvatarReferenceKind anchorKind, float3 offset)
            {
                var anchor = root.FindChild(anchorKind.ToString(), recursive: false);
                if (anchor?.GetComponent<AvatarReferencePoint>()?.Kind.Value == anchorKind
                    && Matches(anchor, OffsetInPose(oldPose, offset)))
                    Apply(anchor, OffsetInPose(newPose, offset));
            }
        }

        bool Matches(Slot marker, RefPose pose)
            => pose.Valid
                && (avatarRoot.GlobalPointToLocal(marker.GlobalPosition) - pose.LocalPosition).LengthSquared < 1e-8f
                && System.Math.Abs(floatQ.Dot(avatarRoot.GlobalRotationToLocal(marker.GlobalRotation).Normalized,
                    pose.LocalRotation.Normalized)) > 0.999999f;
        void Apply(Slot marker, RefPose pose)
        {
            marker.GlobalPosition = avatarRoot.LocalPointToGlobal(pose.LocalPosition);
            marker.GlobalRotation = avatarRoot.LocalRotationToGlobal(pose.LocalRotation);
        }
    }

    private static RefPose ComputeLegacyHandGrip(Slot avatarRoot, HumanoidRig rig, bool rightSide)
    {
        var hand = rig?.TryGetBone(rightSide ? BodyNode.RightHand : BodyNode.LeftHand);
        if (avatarRoot == null || hand == null || hand.IsDestroyed)
            return default;

        var lower = rig!.TryGetBone(rightSide ? BodyNode.RightLowerArm : BodyNode.LeftLowerArm);
        var upper = rig.TryGetBone(rightSide ? BodyNode.RightUpperArm : BodyNode.LeftUpperArm);

        float3 handPos = hand.GlobalPosition;
        float3 pointDir;
        if (lower != null && !lower.IsDestroyed)
        {
            var d = handPos - lower.GlobalPosition;
            pointDir = d.LengthSquared > 1e-6f ? d.Normalized : hand.GlobalRotation * float3.Backward;
        }
        else
        {
            pointDir = hand.GlobalRotation * float3.Backward;
        }

        float3 palmNormal = float3.Up;
        var thumb = FirstBone(rig, rightSide
            ? new[] { BodyNode.RightThumb_Proximal, BodyNode.RightThumb_Metacarpal, BodyNode.RightThumb_Distal }
            : new[] { BodyNode.LeftThumb_Proximal, BodyNode.LeftThumb_Metacarpal, BodyNode.LeftThumb_Distal });
        if (thumb != null && !thumb.IsDestroyed)
        {
            var n = float3.Cross(pointDir, thumb.GlobalPosition - handPos);
            if (rightSide)
                n = -n;
            if (n.LengthSquared > 1e-6f)
                palmNormal = n.Normalized;
        }
        else if (upper != null && lower != null && !upper.IsDestroyed && !lower.IsDestroyed)
        {
            var a = lower.GlobalPosition - upper.GlobalPosition;
            var b = handPos - lower.GlobalPosition;
            var n = float3.Cross(a, b);
            if (rightSide)
                n = -n; // share the labeled-left roll so both grips are consistent on a labeled rig
            if (n.LengthSquared > 1e-6f)
                palmNormal = n.Normalized;
        }

        return ToLocal(avatarRoot, handPos, BuildRotation(pointDir, palmNormal));
    }

    // Foot reference: at the foot bone, flattened to face the body's forward on the ground.
    public static RefPose ComputeFoot(Slot avatarRoot, HumanoidRig rig, bool rightSide)
    {
        var foot = rig?.TryGetBone(rightSide ? BodyNode.RightFoot : BodyNode.LeftFoot);
        if (avatarRoot == null || foot == null || foot.IsDestroyed)
            return default;

        TryComputeBodyAxes(rig!, out _, out _, out var forward);
        return ToLocal(avatarRoot, foot.GlobalPosition, BuildRotation(Flatten(forward), float3.Up));
    }

    // Pelvis reference: at the hips bone, flattened to the body's forward.
    public static RefPose ComputePelvis(Slot avatarRoot, HumanoidRig rig)
    {
        var hips = rig?.TryGetBone(BodyNode.Hips);
        if (avatarRoot == null || hips == null || hips.IsDestroyed)
            return default;

        TryComputeBodyAxes(rig!, out _, out _, out var forward);
        return ToLocal(avatarRoot, hips.GlobalPosition, BuildRotation(Flatten(forward), float3.Up));
    }

    // An explicit straight-leg fit for a rig imported with bent knees. Preserve the hips' horizontal
    // placement and its offset above the thigh joints; measure lengths in world space so scale counts. -xlinka
    public static RefPose ComputePelvisFromLegs(Slot avatarRoot, HumanoidRig rig)
    {
        var hips = rig?.TryGetBone(BodyNode.Hips);
        if (avatarRoot == null || hips is not { IsDestroyed: false })
            return default;
        float height = 0;
        int legs = 0;
        Measure(BodyNode.LeftUpperLeg, BodyNode.LeftLowerLeg, BodyNode.LeftFoot);
        Measure(BodyNode.RightUpperLeg, BodyNode.RightLowerLeg, BodyNode.RightFoot);
        if (legs == 0)
            return default;
        var position = hips.GlobalPosition;
        position.y = height / legs;
        TryComputeBodyAxes(rig!, out _, out _, out var forward);
        return ToLocal(avatarRoot, position, BuildRotation(Flatten(forward), float3.Up));

        void Measure(BodyNode upperNode, BodyNode lowerNode, BodyNode footNode)
        {
            var upper = rig!.TryGetBone(upperNode);
            var lower = rig.TryGetBone(lowerNode);
            var foot = rig.TryGetBone(footNode);
            if (upper is not { IsDestroyed: false } || lower is not { IsDestroyed: false } || foot is not { IsDestroyed: false })
                return;
            float thigh = (upper.GlobalPosition - lower.GlobalPosition).Length;
            float shin = (lower.GlobalPosition - foot.GlobalPosition).Length;
            float fitted = foot.GlobalPosition.y + thigh + shin + hips.GlobalPosition.y - upper.GlobalPosition.y;
            if (!float.IsFinite(fitted) || thigh <= 1e-5f || shin <= 1e-5f)
                return;
            height += fitted;
            legs++;
        }
    }

    // Build (or rebuild) the "AvatarReferences" subtree with auto-aligned AvatarReferencePoints.
    // Returns the reference root, or null if the rig is unusable.
    // Keep the wearer from seeing the inside of their own head: a zero-scale UserView override on the
    // head bone, which the hook turns into a collapsed head for the wearer's eyes and nobody else's.
    // Idempotent - an override that already hides the head on the bone or anything above it (a
    // package that shipped its own, an earlier run of this) is reused, so an avatar never picks up two.
    // Returns the override in force, or null when the rig has no head bone. -xlinka
    public static LocalViewOverride? EnsureHeadViewOverride(Slot avatarRoot, HumanoidRig rig)
    {
        var head = rig?.TryGetBone(BodyNode.Head);
        if (head == null || head.IsDestroyed)
            return null;

        for (var walk = head; walk != null; walk = walk.Parent)
        {
            foreach (var existing in walk.GetComponents<LocalViewOverride>())
            {
                if (!existing.IsDestroyed && existing.HidesFromOwnView)
                    return existing;
            }
            if (walk == avatarRoot)
                break;
        }

        var view = head.AttachComponent<LocalViewOverride>();
        view.HideFromOwnView();
        return view;
    }

    public static Slot AutoPlaceReferences(Slot avatarRoot, HumanoidRig rig, bool feet, bool pelvis)
    {
        if (avatarRoot == null || rig == null)
            return null!;

        var existing = avatarRoot.FindChild("AvatarReferences", recursive: false);
        if (existing != null && !existing.IsDestroyed)
            existing.Destroy();

        var root = avatarRoot.AddSlot("AvatarReferences");
        root.LocalPosition.Value = float3.Zero;
        root.LocalRotation.Value = floatQ.Identity;

        var view = ComputeViewFromHeadProxy(avatarRoot, rig);
        if (!view.Valid)
            view = ComputeView(avatarRoot, rig);
        Place(root, AvatarReferenceKind.View, "View", view);
        var leftGrip = ComputeHandGrip(avatarRoot, rig, rightSide: false);
        var rightGrip = ComputeHandGrip(avatarRoot, rig, rightSide: true);
        Place(root, AvatarReferenceKind.LeftHandGrip, "LeftHandGrip", leftGrip);
        Place(root, AvatarReferenceKind.RightHandGrip, "RightHandGrip", rightGrip);

        // Tool anchors ride off the grip frames rather than being computed from bones of their own: they
        // are defined as an offset from where the hand holds things, so a grip that came out right gives
        // anchors that come out right, and one that came out wrong is wrong in one place instead of three.
        Place(root, AvatarReferenceKind.LeftHandToolAnchor, "LeftHandToolAnchor", OffsetInPose(leftGrip, ToolAnchorGripOffset));
        Place(root, AvatarReferenceKind.RightHandToolAnchor, "RightHandToolAnchor", OffsetInPose(rightGrip, ToolAnchorGripOffset));
        Place(root, AvatarReferenceKind.LeftHandGrabAnchor, "LeftHandGrabAnchor", OffsetInPose(leftGrip, GrabAnchorGripOffset));
        Place(root, AvatarReferenceKind.RightHandGrabAnchor, "RightHandGrabAnchor", OffsetInPose(rightGrip, GrabAnchorGripOffset));

        if (feet)
        {
            Place(root, AvatarReferenceKind.LeftFoot, "LeftFoot", ComputeFoot(avatarRoot, rig, rightSide: false));
            Place(root, AvatarReferenceKind.RightFoot, "RightFoot", ComputeFoot(avatarRoot, rig, rightSide: true));
        }
        if (pelvis)
            Place(root, AvatarReferenceKind.Pelvis, "Pelvis", ComputePelvis(avatarRoot, rig));

        return root;
    }

    private static void Place(Slot referenceRoot, AvatarReferenceKind kind, string name, in RefPose pose)
    {
        if (!pose.Valid)
            return;
        // referenceRoot has an identity local transform under the avatar, so avatar-local == here.
        var slot = referenceRoot.AddSlot(name);
        slot.LocalPosition.Value = pose.LocalPosition;
        slot.LocalRotation.Value = pose.LocalRotation;
        slot.AttachComponent<AvatarReferencePoint>().Kind.Value = kind;
    }

    // Slide a pose along its OWN axes, keeping its rotation. Both are already avatar-local, so the
    // result drops straight into the same reference subtree.
    private static RefPose OffsetInPose(in RefPose pose, in float3 localOffset)
    {
        if (!pose.Valid)
            return default;
        return new RefPose
        {
            Valid = true,
            LocalPosition = pose.LocalPosition + pose.LocalRotation * localOffset,
            LocalRotation = pose.LocalRotation,
        };
    }

    private static RefPose ToLocal(Slot avatarRoot, in float3 worldPos, in floatQ worldRot)
        => new RefPose
        {
            Valid = true,
            LocalPosition = avatarRoot.GlobalPointToLocal(worldPos),
            LocalRotation = avatarRoot.GlobalRotation.Inverse * worldRot,
        };

    // First existing (non-destroyed) bone among the candidates, or null.
    private static Slot? FirstBone(HumanoidRig rig, BodyNode[] nodes)
    {
        foreach (var node in nodes)
        {
            var bone = rig.TryGetBone(node);
            if (bone != null && !bone.IsDestroyed)
                return bone;
        }
        return null;
    }

    private static float3 Flatten(float3 v)
    {
        v.y = 0f;
        return v.LengthSquared > 1e-6f ? v.Normalized : float3.Backward;
    }

    // The part of v perpendicular to a unit axis.
    private static float3 Reject(float3 v, float3 axis)
        => v - axis * float3.Dot(v, axis);

    // Rotation whose facing axis (Backward, matching the IK/slot convention) points along forward and
    // whose up rolls toward up. Built from two swings to avoid floatQ.LookRotation (returns inverse).
    private static floatQ BuildRotation(float3 forward, float3 up)
    {
        if (forward.LengthSquared < 1e-8f)
            return floatQ.Identity;
        forward = forward.Normalized;

        floatQ q1 = FabrikSolver.FromToRotation(float3.Backward, forward);
        float3 curUp = q1 * float3.Up;
        float3 upProj = up - forward * float3.Dot(up, forward);
        if (upProj.LengthSquared < 1e-6f)
            return q1;
        floatQ q2 = FabrikSolver.FromToRotation(curUp, upProj.Normalized);
        return q2 * q1;
    }
}
