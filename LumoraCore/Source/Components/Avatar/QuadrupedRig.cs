// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Input;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Avatar;

// The bone map for a four-legged avatar, and its own body frame.
//
// The PRESENCE of this component is the quadruped flag. No new field on HumanoidRig, no rig-kind enum
// to keep in sync with anything: the three places that attach an IK solver ask whether this component
// is on the tree, and attach one solver or the other. That matters because a rig-kind field added to
// HumanoidRig would reorder its MemberIndex and touch every avatar already saved.
//
// The storage shape deliberately mirrors HumanoidRig's: a SyncObjectDictionary keyed by an enum, whose
// values are SyncRef sub-workers. That shape replicates and survives a duplicate, where a plain local
// dictionary comes back empty on a remote peer.
//
// One field shape is NOT copied. HumanoidRig stores its forward as Sync<float3?>, and float3? has no
// SyncCoder entry - it falls through to the object encoder and DataTreeCoder refuses it, so that field
// throws the moment anything saves it. Here it is a plain Sync<float3> beside a Sync<bool>. -xlinka
[ComponentCategory("Users/Avatar")]
public class QuadrupedRig : Component
{
    public readonly SyncObjectDictionary<QuadNode, SyncRef<Slot>> Bones = new();

    public readonly Sync<float3> ForwardAxis = new();

    public readonly Sync<bool> HasForwardAxis = new();

    public readonly Sync<int> TailBoneCount = new();

    // The three segments a limb cannot solve without.
    private static readonly int[] RequiredSegments = { QuadLimb.SegUpper, QuadLimb.SegLower, QuadLimb.SegPaw };

    public Slot TryGetBone(QuadNode node)
    {
        if (Bones.TryGetValue(node, out var reference) && reference.Target != null)
            return reference.Target!;
        return null!;
    }

    public Slot this[QuadNode node]
    {
        get => TryGetBone(node);
        set
        {
            if (value != null)
                SetBone(node, value);
            else
                Bones.Remove(node);
        }
    }

    private void SetBone(QuadNode node, Slot bone) => Bones.GetOrAdd(node).Target = bone;

    public Slot TryGetLimbBone(QuadLimbId limb, int segment)
        => TryGetBone(QuadLimb.Segment(limb, segment));

    // A limb is usable when it has upper, lower and paw. Scapula, cannon and toe are extra segments the
    // solver picks up if present and does not miss if absent.
    public bool HasLimb(QuadLimbId limb)
    {
        for (int i = 0; i < RequiredSegments.Length; i++)
        {
            if (TryGetLimbBone(limb, RequiredSegments[i]) == null)
                return false;
        }
        return true;
    }

    public bool IsQuadruped
    {
        get
        {
            if (TryGetBone(QuadNode.Pelvis) == null || TryGetBone(QuadNode.Chest) == null)
                return false;
            for (int i = 0; i < QuadLimb.Count; i++)
            {
                if (!HasLimb((QuadLimbId)i))
                    return false;
            }
            return true;
        }
    }

    // POPULATION

    public void PopulateFromSkeleton(SkeletonBuilder skeleton)
    {
        if (skeleton == null || !skeleton.IsBuilt.Value)
        {
            LumoraLogger.Warn("QuadrupedRig: Cannot populate from null or unbuilt skeleton");
            return;
        }

        // <NOIK> MEANS "NOT PART OF THE HUMANOID RIG", WHICH ON AN ANIMAL IS THE ANATOMY ITSELF.
        //
        // This used to call HumanoidRig.IsExcludedFromIK and skip those bones, i.e. the quadruped rig
        // applied the BIPED's exclusion list. On a real fox that threw away every leg it needed and kept
        // exactly the ones it should have ignored: the author tags the anatomical legs <NOIK> so the
        // humanoid rig leaves them alone, and provides a plain "Leg Left / Knee Left / Foot Left" proxy
        // chain for the humanoid rig to drive instead. 73 bones went in and 17 came out, seven of them
        // tail, no limbs at all.
        //
        // For a quadruped the tag is the opposite of a veto - it is the strongest available marker of
        // which subtree is the animal. So the anatomical set is the tagged bones plus everything under
        // them (a paw is often untagged beneath a tagged shin), and when that set exists the untagged
        // proxy chain is ignored for LIMBS. Body bones stay open to both, because a rig with no tagged
        // bones at all must still work exactly as before. -xlinka
        var anatomical = new HashSet<Slot>();
        bool hasTaggedAnatomy = false;
        for (int i = 0; i < skeleton.BoneCount; i++)
        {
            var boneSlot = skeleton.BoneSlots[i];
            if (boneSlot == null || !HumanoidRig.IsExcludedFromIK(skeleton.BoneNames[i]))
                continue;
            hasTaggedAnatomy = true;
            MarkSubtree(boneSlot, anatomical);
        }

        // Pass 1: name-classify every bone. Nothing is skipped up front any more; the tagged set decides
        // which candidates may claim a LIMB, further down.
        System.Array.Clear(_statedEnd, 0, _statedEnd.Length);
        var slots = new List<Slot>();
        var classified = new Dictionary<Slot, QuadNode>();
        for (int i = 0; i < skeleton.BoneCount; i++)
        {
            var boneSlot = skeleton.BoneSlots[i];
            if (boneSlot == null)
                continue;

            var node = ClassifyBoneName(skeleton.BoneNames[i], out bool guessedEnd);
            slots.Add(boneSlot);
            classified[boneSlot] = node;
            if (!guessedEnd && QuadLimb.IsLimbNode(node))
                _statedEnd[(int)QuadLimb.LimbOf(node)] = true;
        }

        // Pass 2: depth-sorted so a parent is settled before its children read it, and first match wins
        // so a second bone claiming a filled node cannot clobber the first.
        slots.Sort((a, b) => SlotDepth(a).CompareTo(SlotDepth(b)));

        int tail = 0;
        foreach (var slot in slots)
        {
            var node = classified[slot];
            if (node == QuadNode.NONE)
                continue;

            // A second bone claiming a filled LIMB segment belongs FURTHER DOWN the same leg, not in the
            // bin.
            //
            // Anatomy words are coarser than real skeletons. A digitigrade hind leg is thigh, knee, shin,
            // foot - four bones - but "knee" and "shin" both read as the lower segment, so the second one
            // hit an occupied slot and was dropped outright. The leg then solved as a three-link chain
            // over four bones and the extra one hung limp, which is what "crumpled back legs" looks like.
            // The front leg was unaffected because scapula/leg/knee/paw happen to spell four different
            // words, which is why only half the animal was wrong.
            //
            // Pass 2 is depth-sorted, so the bone arriving second IS the deeper one, and pushing it down
            // the chain is the only direction that can be right. Never pushes up, never leaves the limb.
            // -xlinka
            if (Bones.ContainsKey(node) && QuadLimb.IsLimbNode(node))
            {
                var limb = QuadLimb.LimbOf(node);
                int seg = (int)node - (int)QuadLimb.Base(limb);
                var pushed = QuadNode.NONE;
                for (int next = seg + 1; next <= QuadLimb.SegToe; next++)
                {
                    var candidate = QuadLimb.Segment(limb, next);
                    if (!Bones.ContainsKey(candidate))
                    {
                        pushed = candidate;
                        break;
                    }
                }
                if (pushed == QuadNode.NONE)
                    continue;
                LumoraLogger.Debug($"QuadrupedRig: '{slot.SlotName.Value}' wanted {node} which was taken; "
                               + $"pushed down the chain to {pushed}");
                node = pushed;
            }
            else if (Bones.ContainsKey(node))
            {
                continue;
            }

            // A proxy limb chain built for the humanoid rig must never claim a leg on the animal. It is
            // shallower than the anatomy, and pass 2 takes the shallowest first, so without this the
            // fox's front-left upper leg would be a bone from the biped passthrough. -xlinka
            if (hasTaggedAnatomy && QuadLimb.IsLimbNode(node) && !anatomical.Contains(slot))
                continue;

            SetBone(node, slot);
            if (node >= QuadNode.Tail0 && node <= QuadNode.Tail7)
                tail++;
        }

        TailBoneCount.Value = tail;

        // Names are a hint. The physical arrangement is the truth, and it overrides them.
        // A person who dragged the four paw markers onto the animal has already answered this, and
        // nothing anyone measures gets to argue with them.
        if (!AdoptPawReferences())
            ResolveSidesGeometrically();

        FixGirdleAnchors();

        if (TryComputeForward(out var forward, out var measured))
            StoreForward(forward);

        LumoraLogger.Debug(
            $"QuadrupedRig: populated {Bones.Count} bones ({tail} tail), IsQuadruped={IsQuadruped}, "
            + $"forward={(HasForwardAxis.Value ? BodyForward.ToString() : "none")}");
        SnapshotAuthoredPose();

        LumoraLogger.Debug("QuadrupedRig: " + DescribeForward(in measured));
        LogGirdleSpan("populate");
        DumpRig();
    }

    // Everything the rig decided, bone by bone, with world AND local positions.
    //
    // The import path runs this whole setup automatically, so by the time anything is visible the
    // decisions have already been made and there was no way to see what they were. Every guess about
    // this rig's shape so far has been inferred from two or three numbers in a summary line, and most
    // of those guesses were wrong. This prints the actual skeleton: which slot won each node, where it
    // is, who its parent is, and the girdle geometry the gait and the chassis are driven from. -xlinka
    private const char NewLine = (char)10;
    private const char Quote = (char)39;

    private void DumpRig()
    {
        if (!LumoraLogger.EnableDebug)
            return;
        var sb = new System.Text.StringBuilder();
        sb.Append("QuadrupedRig DUMP: ").Append(Bones.Count).Append(" bones, IsQuadruped=").Append(IsQuadruped);

        void Line(string label, Slot? bone)
        {
            sb.Append(NewLine).Append("  ").Append(label.PadRight(22));
            if (bone == null || bone.IsDestroyed)
            {
                sb.Append("(unmapped)");
                return;
            }
            var g = bone.GlobalPosition;
            var l = bone.LocalPosition.Value;
            sb.Append(Quote).Append(bone.SlotName.Value).Append(Quote)
              .Append(" world=(").Append(g.x.ToString("F3")).Append(", ").Append(g.y.ToString("F3")).Append(", ").Append(g.z.ToString("F3")).Append(')')
              .Append(" local=(").Append(l.x.ToString("F3")).Append(", ").Append(l.y.ToString("F3")).Append(", ").Append(l.z.ToString("F3")).Append(')')
              .Append(" parent='").Append(bone.Parent?.SlotName.Value ?? "none").Append(Quote);
        }

        sb.Append(NewLine).Append(" SPINE");
        Line("Pelvis", TryGetBone(QuadNode.Pelvis));
        Line("Spine0", TryGetBone(QuadNode.Spine0));
        Line("Spine1", TryGetBone(QuadNode.Spine1));
        Line("Spine2", TryGetBone(QuadNode.Spine2));
        Line("Spine3", TryGetBone(QuadNode.Spine3));
        Line("Chest", TryGetBone(QuadNode.Chest));

        sb.Append(NewLine).Append(" NECK");
        Line("Neck0", TryGetBone(QuadNode.Neck0));
        Line("Neck1", TryGetBone(QuadNode.Neck1));
        Line("Neck2", TryGetBone(QuadNode.Neck2));
        Line("Head", TryGetBone(QuadNode.Head));
        Line("Jaw", TryGetBone(QuadNode.Jaw));

        string[] segNames = { "Scapula", "Upper", "Lower", "Cannon", "Paw", "Toe" };
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            var id = (QuadLimbId)i;
            sb.Append(NewLine).Append(" ").Append(QuadLimb.Name(id))
              .Append(_statedEnd[i] ? " (end STATED by name)" : " (end guessed)");
            for (int seg = 0; seg < QuadLimb.SegmentsPerLimb; seg++)
                Line(segNames[seg], TryGetLimbBone(id, seg));
        }

        sb.Append(NewLine).Append(" GIRDLES");
        if (TryGetGirdle(front: true, out float3 fc, out float ft, out _, out _)
            && TryGetGirdle(front: false, out float3 rc, out float rt, out _, out _))
        {
            float3 span = fc - rc;
            float3 flat = new float3(span.x, 0f, span.z);
            sb.Append(NewLine).Append("  front centre=(").Append(fc.x.ToString("F3")).Append(", ").Append(fc.y.ToString("F3")).Append(", ").Append(fc.z.ToString("F3"))
              .Append(") track=").Append(ft.ToString("F3"));
            sb.Append(NewLine).Append("  rear  centre=(").Append(rc.x.ToString("F3")).Append(", ").Append(rc.y.ToString("F3")).Append(", ").Append(rc.z.ToString("F3"))
              .Append(") track=").Append(rt.ToString("F3"));
            sb.Append(NewLine).Append("  separation horizontal=").Append(flat.Length.ToString("F3"))
              .Append(" vertical=").Append((fc.y - rc.y).ToString("F3"))
              .Append("  (a standing quadruped has the two girdles at similar heights)");
        }
        else
        {
            sb.Append(NewLine).Append("  one or both girdles unresolved");
        }

        var root = Slot;
        if (root != null && !root.IsDestroyed)
        {
            var rp = root.GlobalPosition;
            sb.Append(NewLine).Append(" ROOT '").Append(root.SlotName.Value).Append("' world=(")
              .Append(rp.x.ToString("F3")).Append(", ").Append(rp.y.ToString("F3")).Append(", ").Append(rp.z.ToString("F3"))
              .Append(") scale=").Append(root.GlobalScale.x.ToString("F3"));
        }

        LumoraLogger.Debug(sb.ToString());
    }

    // Per limb: did the BONE NAMES state which end of the animal it is, or did we guess?
    //
    // Runtime only, rebuilt on every populate. The geometric pass overrules names because names lie
    // often enough to matter - but a bone called "ForeLeg" is not lying, it is telling you, and a pass
    // that "corrects" it is the one introducing the error. Geometry keeps full authority over LEFT and
    // RIGHT, which is the part authors really do get wrong, and over front/rear on any rig that only
    // said "Leg.L". -xlinka
    private readonly bool[] _statedEnd = new bool[QuadLimb.Count];

    private static void MarkSubtree(Slot root, HashSet<Slot> into)
    {
        if (root == null || root.IsDestroyed || !into.Add(root))
            return;
        foreach (var child in root.Children)
            MarkSubtree(child, into);
    }

    private static int SlotDepth(Slot slot)
    {
        int depth = 0;
        var cursor = slot?.Parent;
        while (cursor != null)
        {
            depth++;
            cursor = cursor.Parent;
        }
        return depth;
    }

    // Quadruped naming vocabulary. Anatomical and colloquial names both, because rig authors use both
    // and often in the same armature ("Femur" next to "Thigh.R").
    //
    // Fore/hind and left/right are HINTS ONLY. They get re-decided geometrically afterwards, which is
    // the whole reason this returns a guess rather than refusing an ambiguous name: a rig whose limb
    // bones are authored on the swapped physical side is common enough that the biped path grew a
    // dedicated sign flip for it. -xlinka
    public static QuadNode ClassifyBoneName(string name, out bool ambiguous)
    {
        ambiguous = false;
        if (string.IsNullOrWhiteSpace(name))
            return QuadNode.NONE;

        string n = name.ToLowerInvariant();

        // Author tags like <NOIK> or <color=...> are markup, not anatomy. Stripped before anything is
        // matched, or "<noik> thigh.l" reads as a helper bone (it contains "ik") and as no segment at
        // all. -xlinka
        n = StripTags(n);

        // Twist/helper bones are never IK targets. "ik" has to be a whole token: matched loose it eats
        // any name containing those two letters, which is how the tag above used to disqualify itself.
        if (n.Contains("twist") || n.Contains("helper") || HasToken(n, "ik") || HasToken(n, "roll"))
            return QuadNode.NONE;

        // Centre line first: these have no side and would otherwise be caught by a limb word.
        if (Has(n, "jaw", "mandible", "chin"))
            return QuadNode.Jaw;
        if (Has(n, "head", "skull", "cranium"))
            return QuadNode.Head;
        if (Has(n, "tail"))
            return TailIndexed(n);
        if (Has(n, "neck", "cervical"))
            return NeckIndexed(n);
        if (Has(n, "chest", "ribcage", "thorax", "withers", "upperbody"))
            return QuadNode.Chest;
        // A SIDED hip is a limb bone, not the pelvis. "HipDip.L" is the bone the left thigh hangs from,
        // and claiming it as the body's pelvis both loses the real pelvis and leaves that hip outside
        // the solver's control. The pelvis has no side. -xlinka
        bool sidedHip = Has(n, "hip") && (EndsWithSide(n, 'l') || EndsWithSide(n, 'r'));
        if (!sidedHip && Has(n, "pelvis", "hips", "hip", "croup", "sacrum", "root"))
            return n.Contains("root") && !n.Contains("hip") ? QuadNode.Root : QuadNode.Pelvis;
        if (Has(n, "spine", "back", "lumbar", "thoracic"))
            return SpineIndexed(n);

        // Limb segment, then which corner it belongs to.
        int segment = ClassifySegment(n);
        if (segment < 0)
            return QuadNode.NONE;

        bool front = LooksFront(n, segment, out bool statedEnd);
        bool left = LooksLeft(n);
        ambiguous = !statedEnd;

        var limb = front
            ? (left ? QuadLimbId.FrontLeft : QuadLimbId.FrontRight)
            : (left ? QuadLimbId.RearLeft : QuadLimbId.RearRight);
        return QuadLimb.Segment(limb, segment);
    }

    private static string StripTags(string n)
    {
        if (n.IndexOf('<') < 0)
            return n;
        var sb = new System.Text.StringBuilder(n.Length);
        int depth = 0;
        foreach (char c in n)
        {
            if (c == '<') { depth++; continue; }
            if (c == '>') { if (depth > 0) depth--; continue; }
            if (depth == 0) sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    // True when `word` appears delimited by something that is not a letter, so "ik" matches "hand.ik"
    // and "ik_target" but not "noik" or "spike".
    private static bool HasToken(string n, string word)
    {
        int from = 0;
        while (true)
        {
            int at = n.IndexOf(word, from, StringComparison.Ordinal);
            if (at < 0)
                return false;
            bool leftOk = at == 0 || !char.IsLetter(n[at - 1]);
            int end = at + word.Length;
            bool rightOk = end >= n.Length || !char.IsLetter(n[end]);
            if (leftOk && rightOk)
                return true;
            from = at + 1;
        }
    }

    private static bool Has(string n, params string[] words)
    {
        for (int i = 0; i < words.Length; i++)
        {
            if (n.Contains(words[i], StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static int ClassifySegment(string n)
    {
        if (Has(n, "toe", "digit", "phalanx", "phalange"))
            return QuadLimb.SegToe;
        if (Has(n, "paw", "hoof", "foot", "hand", "ankle", "wrist"))
            return QuadLimb.SegPaw;
        if (Has(n, "cannon", "metacarpal", "metatarsal", "pastern", "metapodium"))
            return QuadLimb.SegCannon;
        if (Has(n, "hock", "tarsus", "carpus", "shin", "tibia", "radius", "ulna", "calf", "forearm", "lowerleg", "lowerarm", "knee", "elbow"))
            return QuadLimb.SegLower;
        if (Has(n, "thigh", "femur", "humerus", "upperleg", "upperarm", "upperlimb"))
            return QuadLimb.SegUpper;
        // Hip bones are deliberately NOT chain segments. Mapping "HipDip" as a rear scapula was tried:
        // the chain grew, the solver's scapula swing started moving the hip, and the rear roots split
        // 20 cm apart again. The hip is levelled by QuadrupedIK.SymmetriseGirdles through the thigh's
        // parent instead, without being in the solve. -xlinka
        if (Has(n, "scapula", "clavicle", "shoulder", "collar"))
            return QuadLimb.SegScapula;
        // A bare "leg"/"arm" with no qualifier: treat as the upper segment and let topology sort it.
        if (Has(n, "leg", "arm", "limb"))
            return QuadLimb.SegUpper;
        return -1;
    }

    private static bool LooksFront(string n, int segment) => LooksFront(n, segment, out _);

    // `stated` distinguishes a name that SAYS which end it is from one where we fell through to a guess.
    // Only the guess is safe for the geometric pass to overrule. -xlinka
    private static bool LooksFront(string n, int segment, out bool stated)
    {
        stated = true;
        if (Has(n, "front", "fore", "anterior"))
            return true;
        if (Has(n, "rear", "hind", "back", "posterior"))
            return false;

        // Leading "f." / "b.", which is how paw and toe bones are commonly abbreviated when the limb
        // word has already been spent on the parent ("F.ToeOut1.L" under ForePaw). Only at the START and
        // only followed by a separator, so it cannot catch a name that merely begins with those letters.
        if (StartsWithSideTag(n, 'f'))
            return true;
        if (StartsWithSideTag(n, 'b'))
            return false;
        // No fore/hind word. Front-limb anatomy names carry it instead.
        if (Has(n, "humerus", "radius", "ulna", "carpus", "metacarpal", "scapula", "clavicle", "forearm", "hand", "arm"))
            return true;
        if (Has(n, "femur", "tibia", "fibula", "tarsus", "hock", "metatarsal", "thigh", "shin", "calf"))
            return false;

        // Nothing in the name said which end. This is the only branch the geometry may overrule.
        stated = false;
        return segment <= QuadLimb.SegUpper;
    }

    private static bool StartsWithSideTag(string n, char tag)
        => n.Length >= 2 && char.ToLowerInvariant(n[0]) == tag
           && (n[1] == '.' || n[1] == '_' || n[1] == '-');

    private static bool LooksLeft(string n)
    {
        // Suffix forms first (".l", "_l", "-l", " l"), which are what Blender rigs actually use, then
        // the spelled-out word. A bare "l" anywhere would match "pelvis", so it is never tested loose.
        if (EndsWithSide(n, 'l'))
            return true;
        if (EndsWithSide(n, 'r'))
            return false;
        if (Has(n, "left"))
            return true;
        if (Has(n, "right"))
            return false;
        return true;
    }

    private static bool EndsWithSide(string n, char side)
    {
        for (int i = n.Length - 1; i >= 1; i--)
        {
            char c = n[i];
            if (char.IsDigit(c) || c == '.' || c == '_' || c == '-' || c == ' ')
                continue;
            return char.ToLowerInvariant(c) == side
                   && (n[i - 1] == '.' || n[i - 1] == '_' || n[i - 1] == '-' || n[i - 1] == ' ');
        }
        return false;
    }

    private static QuadNode SpineIndexed(string n) => (QuadNode)((int)QuadNode.Spine0 + ChainIndex(n, 3));

    private static QuadNode NeckIndexed(string n) => (QuadNode)((int)QuadNode.Neck0 + ChainIndex(n, 2));

    private static QuadNode TailIndexed(string n) => (QuadNode)((int)QuadNode.Tail0 + ChainIndex(n, 7));

    // Trailing digits pick the position in a numbered chain ("Spine.002" -> 2). Unnumbered lands at 0
    // and the first-match-wins rule in pass 2 keeps the shallowest bone there.
    private static int ChainIndex(string n, int max)
    {
        int end = n.Length;
        int start = end;
        while (start > 0 && char.IsDigit(n[start - 1]))
            start--;
        if (start == end)
            return 0;
        if (!int.TryParse(n.AsSpan(start, end - start), out int value))
            return 0;
        // Blender numbers duplicates from .001, so a chain usually reads 0,1,2 with the root unnumbered.
        return System.Math.Clamp(value, 0, max);
    }

    // Re-key the four limbs from the paw REFERENCE POINTS the avatar creator placed by hand.
    //
    // This is the manual override, and it outranks every heuristic in this file. Names get guessed at,
    // geometry gets measured off bones that may be mislabelled, and both have been wrong on real rigs:
    // the escape hatch is that someone drags a marker onto the actual paw and the rig believes them.
    // Avatar Studio spawned those four markers all along and then discarded them at Create, so the
    // escape hatch existed as UI and was wired to nothing.
    //
    // Each corner takes the limb whose PAW bone is nearest its marker. Refused unless all four are
    // present and the result is a permutation, because a half-answer here is worse than a measurement:
    // it would silently pin two legs to one corner. -xlinka
    private bool AdoptPawReferences()
    {
        var refs = new Slot?[QuadLimb.Count];
        var host = Slot;
        if (host == null || host.IsDestroyed)
            return false;

        foreach (var point in host.GetComponentsInChildren<AvatarReferencePoint>())
        {
            if (point == null || point.IsDestroyed || point.Slot == null)
                continue;
            switch (point.Kind.Value)
            {
                case AvatarReferenceKind.FrontLeftPaw: refs[(int)QuadLimbId.FrontLeft] ??= point.Slot; break;
                case AvatarReferenceKind.FrontRightPaw: refs[(int)QuadLimbId.FrontRight] ??= point.Slot; break;
                case AvatarReferenceKind.RearLeftPaw: refs[(int)QuadLimbId.RearLeft] ??= point.Slot; break;
                case AvatarReferenceKind.RearRightPaw: refs[(int)QuadLimbId.RearRight] ??= point.Slot; break;
            }
        }

        var chains = new Slot?[QuadLimb.Count][];
        var paws = new Slot?[QuadLimb.Count];
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            if (refs[i] == null || refs[i]!.IsDestroyed)
                return false;

            var limb = (QuadLimbId)i;
            chains[i] = new Slot?[QuadLimb.SegmentsPerLimb];
            for (int seg = 0; seg < QuadLimb.SegmentsPerLimb; seg++)
                chains[i][seg] = TryGetLimbBone(limb, seg);

            paws[i] = TryGetLimbBone(limb, QuadLimb.SegPaw)
                      ?? TryGetLimbBone(limb, QuadLimb.SegCannon)
                      ?? TryGetLimbBone(limb, QuadLimb.SegLower);
            if (paws[i] == null || paws[i]!.IsDestroyed)
                return false;
        }

        // corner -> the limb currently holding the nearest paw
        var take = new int[QuadLimb.Count];
        var used = new bool[QuadLimb.Count];
        for (int corner = 0; corner < QuadLimb.Count; corner++)
        {
            float best = float.MaxValue;
            int pick = -1;
            for (int limb = 0; limb < QuadLimb.Count; limb++)
            {
                if (used[limb])
                    continue;
                float d = (paws[limb]!.GlobalPosition - refs[corner]!.GlobalPosition).LengthSquared;
                if (d < best)
                {
                    best = d;
                    pick = limb;
                }
            }
            if (pick < 0)
                return false;
            take[corner] = pick;
            used[pick] = true;
        }

        for (int i = 0; i < QuadLimb.Count; i++)
        {
            for (int seg = 0; seg < QuadLimb.SegmentsPerLimb; seg++)
                Bones.Remove(QuadLimb.Segment((QuadLimbId)i, seg));
        }
        var moves = new System.Text.StringBuilder();
        for (int corner = 0; corner < QuadLimb.Count; corner++)
        {
            int from = take[corner];
            for (int seg = 0; seg < QuadLimb.SegmentsPerLimb; seg++)
            {
                var bone = chains[from][seg];
                if (bone != null && !bone.IsDestroyed)
                    SetBone(QuadLimb.Segment((QuadLimbId)corner, seg), bone);
            }
            _statedEnd[corner] = true;
            if (corner > 0) moves.Append(", ");
            moves.Append((QuadLimbId)from).Append("->").Append((QuadLimbId)corner);
        }

        LumoraLogger.Debug($"QuadrupedRig: limbs keyed from the authored paw references; {moves}");
        return true;
    }

    // Put Chest and Pelvis on the bones that actually CARRY the girdles.
    //
    // A rig can ship a humanoid proxy chain beside the animal's own anatomy - Hips, Spine, Chest, Neck
    // stacked vertically for a two-legged rig, with the real quadruped bones tagged <NOIK> alongside.
    // The tagged-anatomy rule in pass 2 only defends LIMB nodes, so the body nodes took the proxy: on a
    // real avatar Pelvis landed on 'Hips' at the FRONT of the animal, 1.4 units from the actual hips,
    // and Chest landed half a metre straight above it. The chassis then asked for two points that were
    // both at the animal's front, one over the other, the rear was never positioned at all, and since
    // the solver writes the pelvis position and that bone is the root of everything, the whole animal
    // got dragged with it.
    //
    // The girdles cannot be fooled the same way: they are the midpoints of the limb roots, so whatever
    // bone the two front legs hang from IS the chest and whatever the two rear legs hang from IS the
    // pelvis. Only replaces a mapping that is measurably worse than the candidate, so a rig whose spine
    // bones are named honestly keeps them. -xlinka
    private void FixGirdleAnchors()
    {
        if (!TryGetGirdle(front: true, out float3 frontCentre, out _, out _, out _))
            return;
        if (!TryGetGirdle(front: false, out float3 rearCentre, out _, out _, out _))
            return;

        Reanchor(QuadNode.Chest, LimbBranchPoint(QuadLimbId.FrontLeft, QuadLimbId.FrontRight), frontCentre, "chest");
        Reanchor(QuadNode.Pelvis, LimbBranchPoint(QuadLimbId.RearLeft, QuadLimbId.RearRight), rearCentre, "pelvis");
    }

    private void Reanchor(QuadNode node, Slot? candidate, float3 girdle, string label)
    {
        if (candidate == null || candidate.IsDestroyed)
            return;

        var current = TryGetBone(node);
        if (current != null && !current.IsDestroyed)
        {
            float now = (current.GlobalPosition - girdle).LengthSquared;
            float then = (candidate.GlobalPosition - girdle).LengthSquared;
            if (now <= then)
                return;

            LumoraLogger.Debug(
                $"QuadrupedRig: {label} re-anchored from '{current.SlotName.Value}' to '{candidate.SlotName.Value}'; "
                + $"it sat {MathF.Sqrt(now):F3} from its girdle and the branch point sits {MathF.Sqrt(then):F3}");
        }

        // Never leave two nodes on one slot: the solver's spine chain would get a zero-length segment.
        for (var spine = QuadNode.Spine0; spine <= QuadNode.Spine3; spine++)
        {
            if (TryGetBone(spine) == candidate)
                Bones.Remove(spine);
        }

        SetBone(node, candidate);
    }

    // The bone both limbs of a pair ultimately hang from.
    private Slot? LimbBranchPoint(QuadLimbId a, QuadLimbId b)
    {
        var left = FirstMappedSegment(a);
        var right = FirstMappedSegment(b);
        if (left == null || right == null)
            return null;

        var seen = new HashSet<Slot>();
        for (var s = left.Parent; s != null && seen.Count < 32; s = s.Parent)
            seen.Add(s);
        for (var s = right.Parent; s != null; s = s.Parent)
        {
            if (seen.Contains(s))
                return s;
        }
        return null;
    }

    private Slot? FirstMappedSegment(QuadLimbId limb)
    {
        for (int seg = 0; seg < QuadLimb.SegmentsPerLimb; seg++)
        {
            var bone = TryGetLimbBone(limb, seg);
            if (bone != null && !bone.IsDestroyed)
                return bone;
        }
        return null;
    }

    // One line saying how long the animal currently is, callable from any stage of setup.
    //
    // The body measures 1.326 at populate and 0.209 by the time the solver captures rest, and nothing
    // in between reports anything, so which stage does it is guesswork. Stamping the same measurement
    // at each stage turns that into a bisect. -xlinka
    public void LogGirdleSpan(string stage)
    {
        if (!LumoraLogger.EnableDebug)
            return;
        if (!TryGetGirdle(front: true, out float3 front, out float frontTrack, out _, out _)
            || !TryGetGirdle(front: false, out float3 rear, out float rearTrack, out _, out _))
        {
            LumoraLogger.Debug($"QuadrupedRig SPAN [{stage}]: girdles unresolved");
            return;
        }

        float3 gap = front - rear;
        gap.y = 0f;
        float scale = Slot != null && !Slot.IsDestroyed ? Slot.GlobalScale.x : 1f;
        // Identity in the line, because two rigs on one avatar would produce exactly this pattern of
        // "different answers at different stages" with nothing having moved at all.
        LumoraLogger.Debug($"QuadrupedRig SPAN [{stage}] rig#{ReferenceID} on '{Slot?.SlotName.Value}' "
                       + $"bones={Bones.Count} drivenFL={Driven(LimbRoot(QuadLimbId.FrontLeft))} "
                       + $"drivenRL={Driven(LimbRoot(QuadLimbId.RearLeft))} "
                       + $"frontRoot='{LimbRoot(QuadLimbId.FrontLeft)?.SlotName.Value}' "
                       + $"rearRoot='{LimbRoot(QuadLimbId.RearLeft)?.SlotName.Value}': "
                       + $"horiz={gap.Length:F3} vert={(front.y - rear.y):F3} "
                       + $"front={front} rear={rear} tracks={frontTrack:F3}/{rearTrack:F3} rootScale={scale:F3}");
    }

    // The pose the AUTHOR left, captured before anything starts writing to these bones.
    //
    // Populate runs during import, and the span measured there is the animal as its creator built it -
    // 1.326 long with level girdles on a real avatar. The orphaned drive links that ship with a package
    // then take the bones somewhere else entirely, and releasing those links only stops the bleeding: it
    // cannot put back what was already overwritten. This is the copy to put back. -xlinka
    private readonly Dictionary<Slot, (float3 Position, floatQ Rotation)> _authoredPose = new();

    private void SnapshotAuthoredPose()
    {
        _authoredPose.Clear();
        foreach (var bone in GetPoseBones())
            _authoredPose[bone] = (bone.LocalPosition.Value, bone.LocalRotation.Value);
    }

    // Include connecting bones up to the shared skeleton root, without claiming the avatar slot.
    internal HashSet<Slot> GetPoseBones()
    {
        var mapped = new HashSet<Slot>();
        foreach (var node in Bones.Keys)
        {
            var bone = TryGetBone(node);
            if (bone != null && !bone.IsDestroyed)
                mapped.Add(bone);
        }
        var result = new HashSet<Slot>(mapped);
        Slot? common = null;
        foreach (var bone in mapped)
        {
            if (common == null)
                common = bone;
            while (common != null)
            {
                bool contains = false;
                for (var ancestor = bone; ancestor != null; ancestor = ancestor.Parent)
                {
                    if (ancestor != common)
                        continue;
                    contains = true;
                    break;
                }
                if (contains)
                    break;
                common = common.Parent;
            }
        }
        foreach (var bone in mapped)
        {
            if (bone == common)
                continue;
            for (var parent = bone.Parent; parent != null; parent = parent.Parent)
            {
                if (parent == Slot)
                    break;
                result.Add(parent);
                if (parent == common)
                    break;
            }
        }
        return result;
    }

    // Restore mapped bones and their connecting hierarchy. Skips anything still driven, because writing
    // through a live link is exactly the silent no-op this whole hunt was about.
    //
    // Forced, not set. A bone a local-only driver held reads the authored value already on this machine, so a
    // plain set skipped it as unchanged and every other machine kept the value the package was saved with: they
    // captured a different animal for rest than the wearer did. -xlinka
    public int RestoreAuthoredPose()
    {
        int restored = 0;
        foreach (var pair in _authoredPose)
        {
            var bone = pair.Key;
            if (bone == null || bone.IsDestroyed)
                continue;
            if (bone.LocalPosition.IsDriven || bone.LocalRotation.IsDriven)
                continue;

            bone.LocalPosition.ForceSet(pair.Value.Position);
            bone.LocalRotation.ForceSet(pair.Value.Rotation);
            restored++;
        }
        return restored;
    }

    // Is anything DRIVING this bone's transform? A driver would move it without any of the pose-writing
    // call sites being involved, which is exactly the signature we are looking at. -xlinka
    private static string Driven(Slot? bone)
    {
        if (bone == null || bone.IsDestroyed)
            return "n/a";
        return $"{(bone.LocalPosition.IsDriven ? "P" : "-")}{(bone.LocalRotation.IsDriven ? "R" : "-")}{(bone.LocalScale.IsDriven ? "S" : "-")}";
    }

    // GEOMETRY

    // Re-decide fore/hind and left/right from where the bones physically ARE, and re-key the map when
    // the authored labels disagree.
    //
    // Names lie often enough that the biped path carries a dedicated flip for it. Here it is worse than
    // cosmetic: a trot is defined by which corners move together, so a swapped label does not merely
    // mirror the animal, it turns the diagonal pair into a lateral pair and the walk falls apart. -xlinka
    public void ResolveSidesGeometrically()
    {
        // Collect the four limbs by their CURRENT key, then work out where each actually sits.
        var roots = new Slot?[QuadLimb.Count];
        var chains = new Slot?[QuadLimb.Count][];
        int mapped = 0;
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            var limb = (QuadLimbId)i;
            chains[i] = new Slot?[QuadLimb.SegmentsPerLimb];
            for (int s = 0; s < QuadLimb.SegmentsPerLimb; s++)
                chains[i][s] = TryGetLimbBone(limb, s);
            roots[i] = chains[i][QuadLimb.SegUpper] ?? chains[i][QuadLimb.SegScapula];
            if (roots[i] != null && !roots[i]!.IsDestroyed)
                mapped++;
        }

        // All four corners have to be on the table. With three the centroid sits off to one side and the
        // 2/2 split below cannot be checked, which is worse than leaving the labels alone.
        if (mapped < QuadLimb.Count)
        {
            LumoraLogger.Warn($"QuadrupedRig: geometric side resolve skipped, only {mapped}/4 limb roots mapped; "
                            + $"keeping authored labels ({DescribeRoots(roots)})");
            return;
        }

        float3 centre = float3.Zero;
        for (int i = 0; i < QuadLimb.Count; i++)
            centre += roots[i]!.GlobalPosition;
        centre /= QuadLimb.Count;

        // Two candidate body axes, and the limbs themselves decide which one is real.
        //
        // chest-pelvis is the obvious measure and it is what this used to trust outright. On a rig whose
        // spine bones are labelled loosely it can come out across the animal rather than along it, and
        // then all four legs project to nearly the same place, the front/back split collapses, two limbs
        // claim one corner and the whole correction is thrown away - which is exactly how a fox ends up
        // wearing its hind legs at the front. The paw spread cannot make that mistake: on any four-legged
        // body the corners are further apart along the spine than across the shoulders, so the axis of
        // greatest spread IS the body axis, and it needs no bone labels at all to measure.
        //
        // Neither is trusted blind. A candidate is used only if it actually splits the four roots two in
        // front and two behind, two left and two right. -xlinka
        Span<float3> candidates = stackalloc float3[2];
        int candidateCount = 0;

        var pelvis = TryGetBone(QuadNode.Pelvis);
        var chest = TryGetBone(QuadNode.Chest);
        if (pelvis != null && chest != null && !pelvis.IsDestroyed && !chest.IsDestroyed)
        {
            float3 spine = chest.GlobalPosition - pelvis.GlobalPosition;
            spine.y = 0f;
            if (spine.LengthSquared > 1e-8f)
                candidates[candidateCount++] = spine.Normalized;
        }

        if (TrySpreadAxis(roots, centre, out float3 spread))
            candidates[candidateCount++] = spread;

        float3 forward = float3.Zero;
        float3 right = float3.Zero;
        var target = new QuadLimbId[QuadLimb.Count];
        bool haveSplit = false;

        for (int c = 0; c < candidateCount && !haveSplit; c++)
        {
            float3 axis = candidates[c];

            // The spread axis has no sign of its own, and chest-pelvis can be authored backwards. The
            // sign comes from the SAME vote TryComputeForward uses, so the re-key and the stored forward
            // can never disagree about which end the head is on: they used to (this read one head bone
            // against the centroid, that read the head against the chest) and the root yaw was built on
            // whichever lost. The vote only counts markers that sit beyond the limb spread, because on
            // this rig the bone winning the Head key can stand directly over one girdle, where its
            // lateral offset is noise. Chest-pelvis is the last resort when nothing is beyond. -xlinka
            float minProj = float.MaxValue, maxProj = float.MinValue;
            for (int i = 0; i < QuadLimb.Count; i++)
            {
                float t = float3.Dot(roots[i]!.GlobalPosition - centre, axis);
                minProj = MathF.Min(minProj, t);
                maxProj = MathF.Max(maxProj, t);
            }
            var probe = default(ForwardMeasurement);
            int signVote = VoteSign(axis, centre, MathF.Max((maxProj - minProj) * 0.5f, 1e-3f), ref probe);
            if (signVote < 0)
            {
                axis = -axis;
            }
            else if (signVote == 0
                     && pelvis != null && chest != null && !pelvis.IsDestroyed && !chest.IsDestroyed)
            {
                float3 sign = chest.GlobalPosition - pelvis.GlobalPosition;
                sign.y = 0f;
                if (sign.LengthSquared > 1e-8f && float3.Dot(sign.Normalized, axis) < 0f)
                    axis = -axis;
            }

            float3 axisRight = float3.Cross(axis, float3.Up);
            if (axisRight.LengthSquared < 1e-8f)
                continue;
            axisRight = axisRight.Normalized;

            int frontCount = 0, leftCount = 0;
            var attempt = new QuadLimbId[QuadLimb.Count];
            for (int i = 0; i < QuadLimb.Count; i++)
            {
                float3 offset = roots[i]!.GlobalPosition - centre;
                bool front = float3.Dot(offset, axis) > 0f;
                bool left = float3.Dot(offset, axisRight) < 0f;
                if (front) frontCount++;
                if (left) leftCount++;
                attempt[i] = front
                    ? (left ? QuadLimbId.FrontLeft : QuadLimbId.FrontRight)
                    : (left ? QuadLimbId.RearLeft : QuadLimbId.RearRight);
            }

            if (frontCount != 2 || leftCount != 2)
                continue;

            // A candidate axis that moves an explicitly-named limb to the other end of the animal is
            // wrong about the animal, not about the limb.
            //
            // This is the check that was missing, and its absence is what mangled the legs: the bones
            // said ForeShoulder / ForeLeg / ForeKnee / ForePaw against Thigh / Knee / Foot, which is as
            // clear as a rig ever gets, and the geometric pass moved all four anyway. The mapping it
            // produced kept the side on one pair and flipped it on the other, which no rigid relabelling
            // of a real body can do - so the measurement was bad, and it still won because nothing was
            // allowed to contradict it. -xlinka
            bool contradictsNames = false;
            for (int i = 0; i < QuadLimb.Count && !contradictsNames; i++)
            {
                if (_statedEnd[i] && QuadLimb.IsFront(attempt[i]) != QuadLimb.IsFront((QuadLimbId)i))
                    contradictsNames = true;
            }
            if (contradictsNames)
                continue;

            var used = new bool[QuadLimb.Count];
            bool permutation = true;
            for (int i = 0; i < QuadLimb.Count && permutation; i++)
            {
                if (used[(int)attempt[i]])
                    permutation = false;
                else
                    used[(int)attempt[i]] = true;
            }
            if (!permutation)
                continue;

            forward = axis;
            right = axisRight;
            attempt.CopyTo(target, 0);
            haveSplit = true;
        }

        if (!haveSplit)
        {
            int stated = 0;
            for (int i = 0; i < QuadLimb.Count; i++)
            {
                if (_statedEnd[i]) stated++;
            }
            LumoraLogger.Warn("QuadrupedRig: no body axis split the limbs two-and-two without contradicting the "
                            + $"names, keeping authored labels; {stated}/4 limbs named an end; "
                            + $"centre={centre} ({DescribeRoots(roots)})");
            return;
        }

        bool changed = false;
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            if (target[i] != (QuadLimbId)i)
                changed = true;
        }

        if (!changed)
            return;

        var moves = new System.Text.StringBuilder();
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            if (i > 0) moves.Append(", ");
            moves.Append((QuadLimbId)i).Append("->").Append(target[i]);
        }
        LumoraLogger.Debug($"QuadrupedRig: re-keying limbs from geometry, forward={forward} right={right}; {moves}; "
                       + DescribeRoots(roots));

        for (int i = 0; i < QuadLimb.Count; i++)
        {
            for (int s = 0; s < QuadLimb.SegmentsPerLimb; s++)
                Bones.Remove(QuadLimb.Segment((QuadLimbId)i, s));
        }
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            for (int s = 0; s < QuadLimb.SegmentsPerLimb; s++)
            {
                var bone = chains[i][s];
                if (bone != null && !bone.IsDestroyed)
                    SetBone(QuadLimb.Segment(target[i], s), bone);
            }
        }

        LumoraLogger.Debug("QuadrupedRig: limb re-key applied");
    }

    // Horizontal axis the four limb roots are most spread along.
    //
    // A quadruped's corners are always further apart nose-to-tail than shoulder-to-shoulder, so this is
    // the body axis, and unlike chest-pelvis it cannot be thrown off by how the spine bones were named.
    // Standard 2x2 covariance on the XZ plane; the principal direction is the eigenvector of the larger
    // eigenvalue. Refused when the two spreads are close, because a rig standing square gives no honest
    // answer and a coin flip here swaps the animal end for end. -xlinka
    private static bool TrySpreadAxis(Slot?[] roots, float3 centre, out float3 axis)
    {
        axis = float3.Zero;

        float cxx = 0f, cxz = 0f, czz = 0f;
        for (int i = 0; i < roots.Length; i++)
        {
            var root = roots[i];
            if (root == null || root.IsDestroyed)
                continue;
            float3 d = root.GlobalPosition - centre;
            cxx += d.x * d.x;
            cxz += d.x * d.z;
            czz += d.z * d.z;
        }

        float trace = cxx + czz;
        float det = cxx * czz - cxz * cxz;
        float disc = trace * trace - 4f * det;
        if (disc < 0f)
            disc = 0f;
        float rootDisc = MathF.Sqrt(disc);
        float major = (trace + rootDisc) * 0.5f;
        float minor = (trace - rootDisc) * 0.5f;
        if (major < 1e-8f || major < minor * 1.15f)
            return false;

        float theta = 0.5f * MathF.Atan2(2f * cxz, cxx - czz);
        axis = new float3(MathF.Cos(theta), 0f, MathF.Sin(theta));
        return axis.LengthSquared > 1e-8f;
    }

    private static string DescribeRoots(Slot?[] roots)
    {
        var report = new System.Text.StringBuilder();
        for (int i = 0; i < roots.Length; i++)
        {
            if (i > 0) report.Append(", ");
            var r = roots[i];
            report.Append((QuadLimbId)i).Append('=');
            report.Append(r == null || r.IsDestroyed ? "UNMAPPED" : $"'{r.SlotName.Value}'@{r.GlobalPosition}");
        }
        return report.ToString();
    }

    // The body's forward, measured rather than guessed from a bone rotation.
    //
    // This is mandatory, not an optimisation. HumanoidRig.GuessForwardAxis cannot serve a quadruped:
    // it needs a left/right line from the UPPER ARMS (or shoulders, or hands), and a fox's front legs
    // classify as legs, so all three of its attempts fail and it returns null. Even given a line it
    // would still degenerate, because it builds forward as Cross(up, right) with up = head - hips,
    // which on a horizontal spine is roughly the forward direction itself - the cross comes out near
    // vertical and its own flatten step kills it.
    //
    // Returning null there is silent. Every downstream consumer treats a missing forward as "nothing to
    // do" and returns early, so the failure shows up as an avatar that solves backward rather than as
    // an error. -xlinka
    public bool TryComputeForward(out float3 forward)
        => TryComputeForward(out forward, out _);

    // Everything one measurement saw, so a log line can say WHY it chose an end and not just which.
    public struct ForwardMeasurement
    {
        public bool Valid;
        public bool FromGirdles;
        public float3 FrontCentre;
        public float3 RearCentre;
        public float3 Axis;
        public float3 Forward;
        public int HeadVote;
        public int TailVote;
        public int ToeVote;
        public int ToeCount;
        public string FrontLeft, FrontRight, RearLeft, RearRight, Head, Tail, Chest, Pelvis;
    }

    public bool TryComputeForward(out float3 forward, out ForwardMeasurement m)
    {
        forward = float3.Backward;
        m = default;
        m.Chest = BoneName(TryGetBone(QuadNode.Chest));
        m.Pelvis = BoneName(TryGetBone(QuadNode.Pelvis));

        // Primary: the line between the two girdles, each the midpoint of a limb PAIR. Four limb roots
        // go into it, so no single mismapped bone can turn it. Chest minus pelvis is only the fallback
        // for a rig with a leg missing: on a real avatar the bone winning Pelvis stood at the shoulders
        // with Chest directly above it, and that line was vertical, not along the animal at all.
        float3 frontCentre, rearCentre;
        if (TryGetGirdle(front: true, out frontCentre, out _, out _, out _)
            && TryGetGirdle(front: false, out rearCentre, out _, out _, out _))
        {
            m.FromGirdles = true;
            m.FrontLeft = BoneName(LimbRoot(QuadLimbId.FrontLeft));
            m.FrontRight = BoneName(LimbRoot(QuadLimbId.FrontRight));
            m.RearLeft = BoneName(LimbRoot(QuadLimbId.RearLeft));
            m.RearRight = BoneName(LimbRoot(QuadLimbId.RearRight));
        }
        else
        {
            var pelvisBone = TryGetBone(QuadNode.Pelvis);
            var chestBone = TryGetBone(QuadNode.Chest);
            if (pelvisBone == null || chestBone == null)
                return false;
            frontCentre = chestBone.GlobalPosition;
            rearCentre = pelvisBone.GlobalPosition;
        }
        m.FrontCentre = frontCentre;
        m.RearCentre = rearCentre;

        float3 axis = frontCentre - rearCentre;
        axis.y = 0f;
        if (axis.LengthSquared < 1e-8f)
            return false;
        float halfSpan = axis.Length * 0.5f;
        axis = axis.Normalized;
        m.Axis = axis;

        int vote = VoteSign(axis, (frontCentre + rearCentre) * 0.5f, halfSpan, ref m);

        forward = vote < 0 ? -axis : axis;
        m.Forward = forward;
        m.Valid = true;
        return true;
    }

    // Which way along `axis` the front is, from every marker that sits BEYOND the span.
    //
    // A marker between the girdles says nothing about which end is which and must not vote. On a real
    // avatar the bone that wins the Head key is the top of an upright proxy chain planted on one
    // girdle, and the old head-minus-chest test read its sign off millimetres of lateral offset; that
    // sign came out opposite between two runs of the same package. A real head is past the front pair,
    // a tail base is past the rear pair, and toes point out the front of a paw. Those three cannot all
    // be fooled by one bone, and a tie leaves the axis exactly as the limb names keyed it, which is
    // front minus rear. -xlinka
    private int VoteSign(float3 axis, float3 mid, float halfSpan, ref ForwardMeasurement m)
    {
        float beyond = halfSpan * 1.02f;
        int vote = 0;

        var head = TryGetBone(QuadNode.Head) ?? TryGetBone(QuadNode.Neck0);
        m.Head = BoneName(head);
        if (head != null && !head.IsDestroyed)
        {
            float t = float3.Dot(head.GlobalPosition - mid, axis);
            if (t > beyond) m.HeadVote = 2;
            else if (t < -beyond) m.HeadVote = -2;
            vote += m.HeadVote;
        }

        var tailBone = TryGetBone(QuadNode.Tail0) ?? TryGetBone(QuadNode.Tail1) ?? TryGetBone(QuadNode.Tail2);
        m.Tail = BoneName(tailBone);
        if (tailBone != null && !tailBone.IsDestroyed)
        {
            float t = float3.Dot(tailBone.GlobalPosition - mid, axis);
            if (t < -beyond) m.TailVote = 2;
            else if (t > beyond) m.TailVote = -2;
            vote += m.TailVote;
        }

        float3 toeSum = float3.Zero;
        int toeCount = 0;
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            var paw = TryGetLimbBone((QuadLimbId)i, QuadLimb.SegPaw);
            var toe = TryGetLimbBone((QuadLimbId)i, QuadLimb.SegToe);
            if (paw == null || toe == null || paw.IsDestroyed || toe.IsDestroyed)
                continue;
            float3 d = toe.GlobalPosition - paw.GlobalPosition;
            d.y = 0f;
            if (d.LengthSquared < 1e-8f)
                continue;
            toeSum += d.Normalized;
            toeCount++;
        }
        m.ToeCount = toeCount;
        if (toeCount > 0 && toeSum.LengthSquared > 1e-8f)
        {
            m.ToeVote = float3.Dot(toeSum.Normalized, axis) >= 0f ? 1 : -1;
            vote += m.ToeVote;
        }

        return vote;
    }

    private static string BoneName(Slot? bone)
        => bone == null || bone.IsDestroyed ? "<none>" : bone.SlotName.Value;

    public string DescribeForward(in ForwardMeasurement m)
    {
        if (!m.Valid)
            return "forward: no measurement (girdles and chest/pelvis both unavailable)";

        var sb = new System.Text.StringBuilder();
        if (m.FromGirdles)
        {
            sb.Append($"forward from girdles: front=('{m.FrontLeft}', '{m.FrontRight}') centre {m.FrontCentre}, ")
              .Append($"rear=('{m.RearLeft}', '{m.RearRight}') centre {m.RearCentre}");
        }
        else
        {
            sb.Append($"forward from chest-pelvis FALLBACK: chest {m.FrontCentre}, pelvis {m.RearCentre}");
        }
        sb.Append($"; axis={m.Axis}; votes: head '{m.Head}' {m.HeadVote:+0;-0;0}, tail '{m.Tail}' {m.TailVote:+0;-0;0}, ")
          .Append($"toes({m.ToeCount}) {m.ToeVote:+0;-0;0} -> forward={m.Forward}");
        var slot = Slot;
        if (slot != null && !slot.IsDestroyed)
            sb.Append($" rootLocal={slot.GlobalRotation.Inverse * m.Forward}");
        sb.Append($"; Chest='{m.Chest}' Pelvis='{m.Pelvis}' Head='{m.Head}'");
        return sb.ToString();
    }

    // ForwardAxis lives in the rig slot's OWN frame, never in world space.
    //
    // It was stored as the world vector measured at import or equip and read back verbatim, so
    // BodyForward froze to wherever the wearer faced at equip time. The gait laid its stance points and
    // the solver twisted the spine toward that stale heading the moment the user turned. Kept root-local
    // and rotated by the live root, the same field follows the avatar. A save from before this carries a
    // world vector, and every equip re-runs AlignAvatarFacing, which rewrites it. -xlinka
    private void StoreForward(float3 worldForward)
    {
        var slot = Slot;
        float3 local = slot != null && !slot.IsDestroyed
            ? slot.GlobalRotation.Inverse * worldForward
            : worldForward;
        if (local.LengthSquared < 1e-8f)
            return;
        ForwardAxis.Value = local.Normalized;
        HasForwardAxis.Value = true;
    }

    public float3 BodyForward
    {
        get
        {
            var slot = Slot;
            if (HasForwardAxis.Value && ForwardAxis.Value.LengthSquared > 1e-8f && slot != null && !slot.IsDestroyed)
            {
                float3 world = slot.GlobalRotation * ForwardAxis.Value;
                world.y = 0f;
                if (world.LengthSquared > 1e-8f)
                    return world.Normalized;
            }
            return TryComputeForward(out var f) ? f : float3.Backward;
        }
    }

    // Centre, track width and the two limb roots of one girdle. The chassis hangs off these: the front
    // girdle is carried by the front pair, the rear girdle by the rear pair, and the spine between them
    // is what makes the body pitch and roll fall out of the legs instead of being animated on top.
    public bool TryGetGirdle(bool front, out float3 centre, out float trackWidth, out float3 leftRoot, out float3 rightRoot)
    {
        centre = float3.Zero;
        trackWidth = 0f;
        leftRoot = float3.Zero;
        rightRoot = float3.Zero;

        var left = LimbRoot(front ? QuadLimbId.FrontLeft : QuadLimbId.RearLeft);
        var right = LimbRoot(front ? QuadLimbId.FrontRight : QuadLimbId.RearRight);
        if (left == null || right == null || left.IsDestroyed || right.IsDestroyed)
            return false;

        leftRoot = left.GlobalPosition;
        rightRoot = right.GlobalPosition;
        centre = (leftRoot + rightRoot) * 0.5f;
        trackWidth = float3.Distance(leftRoot, rightRoot);
        return true;
    }

    private Slot? LimbRoot(QuadLimbId limb)
        => TryGetLimbBone(limb, QuadLimb.SegUpper) ?? TryGetLimbBone(limb, QuadLimb.SegScapula);

    // DETECTION

    // Does this skeleton describe an animal that stands on four legs?
    //
    // The horizontal spine is REQUIRED, not weighted. An anthro biped with a tail and digitigrade legs
    // hits every other signal here, and the only thing that separates it from a fox is that its head
    // sits above its hips rather than ahead of them. Make that test a mere contributor and every furry
    // avatar in the world starts walking on all fours. -xlinka
    public static int ScoreQuadruped(SkeletonBuilder skeleton, HumanoidRig? rig)
    {
        if (skeleton == null || !skeleton.IsBuilt.Value || skeleton.BoneCount < 8)
            return 0;

        // The gate. Measured from the rig when there is one, from names when there is not.
        Slot? head = rig?.TryGetBone(BodyNode.Head);
        Slot? hips = rig?.TryGetBone(BodyNode.Hips);
        if (head == null || hips == null)
        {
            head = FindByWords(skeleton, "head", "skull");
            hips = FindByWords(skeleton, "pelvis", "hips", "croup");
        }
        if (head == null || hips == null || head.IsDestroyed || hips.IsDestroyed)
            return 0;

        float3 spineLine = head.GlobalPosition - hips.GlobalPosition;
        float span = spineLine.Length;
        if (span < 1e-4f)
            return 0;
        float verticality = MathF.Abs(spineLine.y) / span;
        bool horizontalSpine = verticality < 0.5f;

        // Vocabulary and four grounded tips TOGETHER override the spine gate.
        //
        // The gate reads the BIND POSE, and plenty of quadruped models are authored standing upright or
        // rearing, which scores them zero no matter how obviously they are an animal. But an anthro
        // biped does not have a bone called hock, scapula, carpus, cannon or pastern, and it does not
        // rest with four limb tips on one plane - its hands are up. Requiring BOTH of those keeps the
        // thing the gate was built to stop while letting an upright fox through. -xlinka
        int vocabularyCount = CountVocabulary(skeleton);
        bool decisiveAnatomy = vocabularyCount >= 4 && CountGroundedLimbTips(skeleton) >= 4;

        if (!horizontalSpine && !decisiveAnatomy)
            return 0;

        int score = 4;

        // Vocabulary. Present on most real quadruped rigs, absent on most bipeds, but never sufficient
        // on its own - hence the gate above.
        int vocabulary = vocabularyCount;
        if (vocabulary >= 4)
            score += 2;
        else if (vocabulary >= 2)
            score += 1;

        // Four distal bones sitting near one horizontal plane is what standing on four legs looks like.
        if (CountGroundedLimbTips(skeleton) >= 4)
            score += 2;

        return score;
    }

    private static int CountVocabulary(SkeletonBuilder skeleton)
    {
        int vocabulary = 0;
        for (int i = 0; i < skeleton.BoneCount; i++)
        {
            string n = (skeleton.BoneNames[i] ?? string.Empty).ToLowerInvariant();
            if (n.Length == 0)
                continue;
            if (Has(n, "hock", "carpus", "tarsus", "scapula", "femur", "humerus", "cannon", "pastern", "metacarpal", "metatarsal"))
                vocabulary++;
            else if (Has(n, "fore", "hind", "frontleg", "rearleg", "paw", "hoof"))
                vocabulary++;
        }
        return vocabulary;
    }

    public static bool LooksQuadruped(SkeletonBuilder skeleton, HumanoidRig? rig)
        => ScoreQuadruped(skeleton, rig) >= 6;

    private static Slot? FindByWords(SkeletonBuilder skeleton, params string[] words)
    {
        for (int i = 0; i < skeleton.BoneCount; i++)
        {
            string n = (skeleton.BoneNames[i] ?? string.Empty).ToLowerInvariant();
            if (n.Length > 0 && Has(n, words))
                return skeleton.BoneSlots[i];
        }
        return null;
    }

    private static int CountGroundedLimbTips(SkeletonBuilder skeleton)
    {
        var tips = new List<float3>();
        float lowest = float.MaxValue;
        for (int i = 0; i < skeleton.BoneCount; i++)
        {
            string n = (skeleton.BoneNames[i] ?? string.Empty).ToLowerInvariant();
            var slot = skeleton.BoneSlots[i];
            if (slot == null || slot.IsDestroyed || n.Length == 0)
                continue;
            if (!Has(n, "paw", "hoof", "foot", "toe", "hand"))
                continue;
            if (n.Contains("tail", StringComparison.Ordinal))
                continue;
            var p = slot.GlobalPosition;
            tips.Add(p);
            if (p.y < lowest)
                lowest = p.y;
        }

        if (tips.Count < 4)
            return tips.Count;

        // Within 15% of the body span of the lowest one. Uses the spread of the tips themselves as the
        // scale so it works on an avatar of any size.
        float spread = 0f;
        for (int i = 0; i < tips.Count; i++)
        {
            for (int j = i + 1; j < tips.Count; j++)
                spread = MathF.Max(spread, float3.Distance(tips[i], tips[j]));
        }
        float band = MathF.Max(spread * 0.15f, 1e-3f);

        int grounded = 0;
        for (int i = 0; i < tips.Count; i++)
        {
            if (tips[i].y - lowest <= band)
                grounded++;
        }
        return grounded;
    }

    // FACING

    // Yaw the avatar root frame onto the measured forward, counter-rotating the children so nothing
    // visibly moves. The equivalent biped call takes a HumanoidRig and goes through GuessForwardAxis,
    // which is exactly the method that cannot answer for a quadruped. -xlinka
    public void AlignAvatarFacing()
    {
        const float Deg = 180f / MathF.PI;

        var slot = Slot;
        if (slot == null || slot.IsDestroyed)
            return;
        if (!TryComputeForward(out float3 forward, out var measured))
        {
            LumoraLogger.Warn("QuadrupedRig: facing not aligned, " + DescribeForward(in measured));
            return;
        }

        float3 rootForward = FlatRootForward(slot);
        if (rootForward.LengthSquared < 1e-8f)
            return;

        float angle = SignedYaw(rootForward, forward);
        LumoraLogger.Debug($"QuadrupedRig: {DescribeForward(in measured)}; root forward={rootForward}, off by {angle * Deg:F1} deg");

        if (MathF.Abs(angle) < 0.5f * (MathF.PI / 180f))
        {
            StoreForward(forward);
            return;
        }

        // A yaw on a driven root, or a counter-restore on a driven child, is not an error anywhere: the
        // write dies in BeginModification and the old log then claimed a turn that never happened, or
        // one that DID happen with the body spun along with it. Refuse instead and name the slot. The
        // stored forward is still right, so the solver and gait keep working without the frame turn. -xlinka
        string? blocker = FindDrivenTransform(slot);
        if (blocker != null)
        {
            LumoraLogger.Warn($"QuadrupedRig: root frame NOT yawed ({angle * Deg:F1} deg wanted); {blocker} is driven, "
                            + "so the frame turn or its counter-restore would be dropped silently");
            StoreForward(forward);
            return;
        }

        var children = new List<Slot>(slot.Children);
        var keepPos = new float3[children.Count];
        var keepRot = new floatQ[children.Count];
        for (int i = 0; i < children.Count; i++)
        {
            keepPos[i] = children[i].GlobalPosition;
            keepRot[i] = children[i].GlobalRotation;
        }

        slot.GlobalRotation = floatQ.AxisAngleRad(float3.Up, angle) * slot.GlobalRotation;

        for (int i = 0; i < children.Count; i++)
        {
            children[i].GlobalPosition = keepPos[i];
            children[i].GlobalRotation = keepRot[i];
        }

        // Verified, not asserted. The residual here is exactly what the next call will measure, so a
        // frame that repeats a ~180 turn on every equip shows up as a non-zero residual RIGHT HERE and
        // names the child that moved, instead of a second "unchanged" line twenty seconds later. -xlinka
        float3 rootAfter = FlatRootForward(slot);
        float residual = TryComputeForward(out float3 forwardAfter, out _)
            ? SignedYaw(rootAfter, forwardAfter)
            : float.NaN;

        float worstTurn = 0f, worstShift = 0f;
        string worstChild = "<none>";
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i].IsDestroyed)
                continue;
            float turn = AngleBetween(keepRot[i], children[i].GlobalRotation);
            float shift = float3.Distance(keepPos[i], children[i].GlobalPosition);
            if (turn > worstTurn || shift > worstShift)
                worstChild = children[i].SlotName.Value;
            worstTurn = MathF.Max(worstTurn, turn);
            worstShift = MathF.Max(worstShift, shift);
        }

        StoreForward(float.IsNaN(residual) ? forward : forwardAfter);

        string report = $"QuadrupedRig: root frame yawed {angle * Deg:F1} deg onto body front; "
                      + $"residual root-vs-body {residual * Deg:F1} deg, children held to {worstTurn * Deg:F2} deg / {worstShift:F3} m "
                      + $"(worst '{worstChild}')";
        if (float.IsNaN(residual) || MathF.Abs(residual) > 2f * (MathF.PI / 180f)
            || worstTurn > 1f * (MathF.PI / 180f) || worstShift > 0.01f)
        {
            LumoraLogger.Warn(report + "; the frame turn did NOT hold, the body moved with the root");
        }
        else
        {
            LumoraLogger.Debug(report);
        }
    }

    private static float3 FlatRootForward(Slot slot)
    {
        float3 f = slot.GlobalRotation * float3.Backward;
        f.y = 0f;
        return f.LengthSquared > 1e-8f ? f.Normalized : float3.Zero;
    }

    // Yaw that takes `from` onto `to` about +Y, in (-pi, pi]. Same convention as AxisAngleRad(Up, x):
    // Backward onto Left is +90 degrees.
    private static float SignedYaw(float3 from, float3 to)
    {
        float delta = MathF.Atan2(to.x, to.z) - MathF.Atan2(from.x, from.z);
        while (delta > MathF.PI) delta -= 2f * MathF.PI;
        while (delta <= -MathF.PI) delta += 2f * MathF.PI;
        return delta;
    }

    private static float AngleBetween(floatQ a, floatQ b)
    {
        floatQ d = a.Inverse * b;
        return 2f * MathF.Acos(System.Math.Clamp(MathF.Abs(d.w), 0f, 1f));
    }

    private static string? FindDrivenTransform(Slot root)
    {
        if (root.LocalRotation.IsDriven)
            return $"root '{root.SlotName.Value}' rotation";
        foreach (var child in root.Children)
        {
            if (child == null || child.IsDestroyed)
                continue;
            if (child.LocalRotation.IsDriven)
                return $"child '{child.SlotName.Value}' rotation";
            if (child.LocalPosition.IsDriven)
                return $"child '{child.SlotName.Value}' position";
        }
        return null;
    }
}
