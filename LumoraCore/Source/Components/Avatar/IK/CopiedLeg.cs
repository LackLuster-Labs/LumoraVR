// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components.Utility;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar.IK;

// A skinned leg that copies the mapped leg's rotations.
//
// Digitigrade avatars are often built on a humanoid proxy leg the IK can pose, with the skinned thigh, shin, hock
// and foot each copying the global rotation of a proxy bone and keeping their own lengths: thigh and hock take the
// proxy thigh's rotation, the shin the proxy shin's, the foot the proxy foot's. The skin's foot is then a two-bone
// function of the proxy's thigh and shin: an upper limb made of every link that turns with the thigh, a lower limb
// of every link that turns with the shin. Solved as that, the foot the wearer sees lands where the gait put it and
// the hock keeps its fold; solved as the proxy, a straightened proxy knee swung the hock through to the front.
// -xlinka
public sealed class CopiedLeg
{
    private readonly struct Link
    {
        public readonly Slot Bone;
        public readonly Slot Child;
        public readonly bool WithThigh;

        public Link(Slot bone, Slot child, bool withThigh)
        {
            Bone = bone;
            Child = child;
            WithThigh = withThigh;
        }
    }

    private readonly List<Link> _links = new();
    private readonly List<CopyGlobalTransform> _copiers = new();

    public Slot Root { get; private set; } = null!;
    public Slot Foot { get; private set; } = null!;
    public Slot? Ball { get; private set; }

    // The two limbs in the copied bone's own axes at the live scale, refreshed by Measure.
    public float3 UpperLocal { get; private set; }
    public float3 LowerLocal { get; private set; }
    public float UpperLength => UpperLocal.Length;
    public float LowerLength => LowerLocal.Length;
    // How far the foot reaches from the root with the leg still folded by half its fold at bind.
    public float Reach { get; private set; }
    private const float FoldKept = 0.5f;

    public string Describe() => $"Copied: {Root.SlotName.Value} .. {Foot.SlotName.Value} ({_links.Count} links)";

    public bool Alive
    {
        get
        {
            foreach (var link in _links)
                if (link.Bone == null || link.Bone.IsDestroyed || link.Child == null || link.Child.IsDestroyed)
                    return false;
            return Foot != null && !Foot.IsDestroyed;
        }
    }

    // A rotation-only copier of the mapped foot, under a parent chain of rotation-only copiers of the mapped thigh and
    // shin that starts with the thigh. A copier that also copies position is a different rig and is left alone.
    public static CopiedLeg? Detect(Slot? avatar, Slot? thigh, Slot? shin, Slot? foot)
    {
        if (avatar == null || thigh == null || shin == null || foot == null)
            return null;
        var copiers = new Dictionary<Slot, CopyGlobalTransform>();
        CopyGlobalTransform? footCopier = null;
        foreach (var copier in avatar.GetComponentsInChildren<CopyGlobalTransform>())
        {
            if (!copier.Enabled.Value || !copier.CopyRotation.Value || copier.CopyPosition.Value || copier.Slot == null)
                continue;
            var source = copier.Source.Target;
            if (ReferenceEquals(source, foot))
                footCopier ??= copier;
            else if (ReferenceEquals(source, thigh) || ReferenceEquals(source, shin))
                copiers.TryAdd(copier.Slot, copier);
        }
        if (footCopier == null)
            return null;

        var chain = new List<(Slot Bone, CopyGlobalTransform Copier)>();
        for (var cursor = footCopier.Slot.Parent; cursor != null && copiers.TryGetValue(cursor, out var c); cursor = cursor.Parent)
            chain.Insert(0, (cursor, c));
        if (chain.Count < 2 || !ReferenceEquals(chain[0].Copier.Source.Target, thigh))
            return null;
        bool turnsWithShin = false;
        foreach (var entry in chain)
            turnsWithShin |= ReferenceEquals(entry.Copier.Source.Target, shin);
        if (!turnsWithShin)
            return null;

        var leg = new CopiedLeg { Root = chain[0].Bone, Foot = footCopier.Slot };
        for (int i = 0; i < chain.Count; i++)
        {
            var child = i + 1 < chain.Count ? chain[i + 1].Bone : footCopier.Slot;
            leg._links.Add(new Link(chain[i].Bone, child, ReferenceEquals(chain[i].Copier.Source.Target, thigh)));
            leg._copiers.Add(chain[i].Copier);
        }
        leg._copiers.Add(footCopier);
        foreach (var child in footCopier.Slot.Children)
        {
            leg.Ball = child;
            break;
        }
        return leg;
    }

    // Each link's offset to its child in the link's own axes, scaled as the bone is drawn. A copier never touches
    // positions here, so these hold whatever pose the rotations are in.
    public void Measure()
    {
        MeasureLinks();
        float a = UpperLength, b = LowerLength;
        Reach = a + b;
        if (_bindFold > 0f)
            Reach = MathF.Sqrt(MathF.Max(a * a + b * b + 2f * a * b * MathF.Cos(_bindFold * FoldKept), 0f));
    }

    private float _bindFold;

    // The fold the leg stands in at bind, from the mapped thigh's and shin's bind rotations.
    public void Bind(floatQ thighRotation, floatQ shinRotation)
    {
        var upper = thighRotation * UpperLocal;
        var lower = shinRotation * LowerLocal;
        if (upper.LengthSquared > 1e-12f && lower.LengthSquared > 1e-12f)
            _bindFold = float3.Angle(upper, lower);
        Measure();
    }

    private void MeasureLinks()
    {
        float3 upper = float3.Zero, lower = float3.Zero;
        foreach (var link in _links)
        {
            var s = link.Bone.GlobalScale;
            var p = link.Child.LocalPosition.Value;
            var offset = new float3(p.x * s.x, p.y * s.y, p.z * s.z);
            if (link.WithThigh)
                upper += offset;
            else
                lower += offset;
        }
        UpperLocal = upper;
        LowerLocal = lower;
    }

    // The first bone under the copied foot, where its toes start, for a foot in this pose.
    public float3 BallAt(float3 foot, floatQ footRotation)
    {
        if (Ball == null || Ball.IsDestroyed)
            return foot;
        var s = Foot.GlobalScale;
        var p = Ball.LocalPosition.Value;
        return foot + footRotation * new float3(p.x * s.x, p.y * s.y, p.z * s.z);
    }

    // Where the copied foot sits with the thigh and shin in these global rotations.
    public float3 FootAt(floatQ thighRotation, floatQ shinRotation)
        => Root.GlobalPosition + thighRotation * UpperLocal + shinRotation * LowerLocal;

    // Pose the copied bones from the mapped ones now, root first, rather than on the copiers' own turn next frame:
    // left to them the skin trails the solve by a frame, and a planted foot slips by a frame of body travel.
    public void Copy()
    {
        foreach (var copier in _copiers)
        {
            if (copier != null && !copier.IsDestroyed)
                copier.OnUpdate(0f);
        }
    }
}
