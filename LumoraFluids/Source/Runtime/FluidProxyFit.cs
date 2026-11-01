// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// An avatar's collision proxies, one per body part its rig has: a box for each sole and toe, a box for each hand, a
// capsule for each limb segment and the chest, a sphere for the head. Two sets share one list of frames:
//   Proxies, the outer set, sized to the outermost surface: all the collision an avatar gets at Reduced LOD, with no
//   mesh data, or past the skin budget;
//   Inner, the backstop, sized INSIDE the closed surface (skin, clothes, never hair cards): what a person skinned on
//   the GPU keeps, so nothing that slips past the skin tunnels into the body, while the skin is what fluid meets.
// A head with long hair must not grow its sphere: the outer head sphere is fitted round the head's own points near
// its middle, and the inner one from the closed points only.
//
// Prepare (world thread) reads the rig as it stands and makes rig-default proxies from bone lengths at once, so a
// person is never without shapes. Fit (worker) makes a new instance sized to the contact mesh's points, the outermost
// surface where the part is cut, so a sole box sits on the shoe's tread and not on the bare foot inside it. A fit more
// than 3x its rig default, or from under 20 points, keeps the default; the inner set falls back to the outer one
// shrunk.
//
// Every proxy lives in a frame of its own, upright for soles (world up at equip is the sole's normal, whatever roll the
// foot bone was imported with), along the bone for limbs, the point cloud's own axes for hands. Each frame is stored
// relative to its part bone, so the gather poses it as part bone x frame every frame, and its sizes are metres at
// equip that scale with the avatar. -xlinka
public sealed class FluidProxyFit
{
    public const int MinPoints = 20;
    public const float MaxGrowth = 3f;
    public const float Percentile = 0.9f;
    // the outer head sphere and limb capsules: how much of the surface they take in (hair and skirts stay out)
    public const float OuterPercentile = 0.75f;
    // the inner set: the radius most of the closed surface stays outside, shrunk again by InnerScale
    public const float InnerPercentile = 0.1f;
    public const float InnerScale = 0.8f;
    // sectors round a limb's axis for its inner radius
    public const int Sectors = 8;
    // slices along a limb's segment for its inner radius
    public const int Slices = 4;
    // an inner box's share of its part's middle 60% (est.)
    public const float InnerBoxScale = 0.6f;

    public readonly bool[] PartPresent = new bool[FluidBodyParts.Count];
    // The part bones' LocalToWorld at equip.
    public readonly float4x4[] PartWorld = new float4x4[FluidBodyParts.Count];
    // Where each part's segment ends at equip: the next joint down the chain.
    public readonly float3[] PartEnd = new float3[FluidBodyParts.Count];

    // One per proxy: Bone is the index into Frames, Part the FluidBodyParts index, Person filled by the gather.
    public readonly List<FluidProxyRecord> Proxies = new();
    // The inner backstop, one per proxy, same Kind, Part and Bone (frame), smaller.
    public readonly List<FluidProxyRecord> Inner = new();
    // Relative to the part bone: part bone LocalToWorld x frame = the proxy's frame in the world.
    public readonly List<float4x4> Frames = new();
    public readonly List<string> Notes = new();

    public float3 Up { get; private set; } = float3.Up;

    public float3 Forward { get; private set; } = float3.Forward;

    public float Scale { get; private set; } = 1f;

    public float FloorY { get; private set; }

    public float SoleInset { get; private set; }

    public bool Fitted { get; private set; }

    public int Rejected { get; private set; }

    public int PresentParts
    {
        get
        {
            int n = 0;
            foreach (bool p in PartPresent)
                n += p ? 1 : 0;
            return n;
        }
    }

    // World thread: the rig at equip and its default shapes.
    public static FluidProxyFit Prepare(Slot avatar, HumanoidRig? rig, AvatarFluidContact? contact)
    {
        var fit = new FluidProxyFit();
        float scale = avatar.GlobalScale.y;
        var height = Height(rig);
        fit.Scale = height > 0.3f ? height / 1.7f : (scale > 1e-4f && float.IsFinite(scale) ? scale : 1f);
        fit.Up = Unit(avatar.GlobalRotation * float3.Up, float3.Up);
        fit.Forward = Unit(avatar.GlobalRotation * float3.Forward, float3.Forward);
        fit.SoleInset = contact != null && !contact.IsDestroyed ? contact.SoleInset.Value : 0f;
        fit.FloorY = avatar.GlobalPosition.y;
        for (int p = 0; p < FluidBodyParts.Count; p++)
        {
            var bone = FluidBodyParts.BoneOf(rig, p);
            fit.PartPresent[p] = bone != null;
            fit.PartWorld[p] = bone != null ? bone.LocalToWorld : float4x4.Identity;
        }
        // The toes ride the foot bone when the rig has none of their own; give them the foot's box only.
        if (rig != null && rig.TryGetBone(BodyNode.LeftToes) == null)
            fit.PartPresent[FluidBodyParts.LeftToes] = false;
        if (rig != null && rig.TryGetBone(BodyNode.RightToes) == null)
            fit.PartPresent[FluidBodyParts.RightToes] = false;
        for (int p = 0; p < FluidBodyParts.Count; p++)
            fit.PartEnd[p] = fit.EndOf(rig, p);
        // A foot that stands well above the avatar's root is not on the floor (a seated or odd rig): guess an ankle
        // height instead of reaching down to the root.
        if (fit.PartPresent[FluidBodyParts.LeftFoot])
        {
            float ankle = Position(fit.PartWorld[FluidBodyParts.LeftFoot]).y - fit.FloorY;
            if (!(ankle > 0f && ankle < 0.25f * fit.Scale))
                fit.FloorY = Position(fit.PartWorld[FluidBodyParts.LeftFoot]).y - 0.08f * fit.Scale;
        }
        fit.MakeDefaults();
        return fit;
    }

    // Worker: a new fit sized to the contact mesh's points at equip. The prepared instance is left as it was.
    public static FluidProxyFit Fit(FluidProxyFit prepared, FluidContactMesh mesh)
    {
        var fit = prepared.CopyRig();
        fit.Fitted = true;
        for (int p = 0; p < FluidBodyParts.Count; p++)
        {
            if (!fit.PartPresent[p])
                continue;
            var points = mesh.PartPoints(p);
            var closed = mesh.ClosedPartPoints(p);
            var frame = fit.DefaultFrame(p);
            FluidProxyRecord outer;
            if (points.Count < MinPoints)
            {
                outer = fit.DefaultRecord(p, frame);
                fit.Rejected++;
                fit.Notes.Add($"{FluidBodyParts.Nodes[p]}: {points.Count} points, rig default");
            }
            else if (!fit.TryFit(p, ref frame, points, out outer))
            {
                frame = fit.DefaultFrame(p);
                outer = fit.DefaultRecord(p, frame);
                fit.Rejected++;
                fit.Notes.Add($"{FluidBodyParts.Nodes[p]}: fit past {MaxGrowth}x its rig default, rig default");
            }
            var inner = closed.Count >= MinPoints && fit.TryFitInner(p, frame, closed, outer, out var fitted) ? fitted : Shrunk(outer);
            fit.Add(outer, inner, p, frame);
        }
        return fit;
    }

    // The proxy's frame in the world at equip.
    public float4x4 FrameWorldAtEquip(int proxy)
    {
        var record = Proxies[proxy];
        return PartWorld[record.Part] * Frames[(int)record.Bone];
    }

    // The lowest point of a sole or toe box in the world at equip, along Up; NaN when that part has no box.
    public float SoleBottom(int part)
    {
        for (int i = 0; i < Proxies.Count; i++)
        {
            var r = Proxies[i];
            if (r.Part != part || r.Kind != FluidLayout.ProxyBox)
                continue;
            var world = FrameWorldAtEquip(i);
            var bottom = new float3(r.A.x, r.A.y - r.B.y, r.A.z);
            return float3.Dot(world.MultiplyPoint(bottom), Up);
        }
        return float.NaN;
    }

    private FluidProxyFit CopyRig()
    {
        var fit = new FluidProxyFit
        {
            Up = Up,
            Forward = Forward,
            Scale = Scale,
            FloorY = FloorY,
            SoleInset = SoleInset,
        };
        Array.Copy(PartPresent, fit.PartPresent, PartPresent.Length);
        Array.Copy(PartWorld, fit.PartWorld, PartWorld.Length);
        Array.Copy(PartEnd, fit.PartEnd, PartEnd.Length);
        return fit;
    }

    private void MakeDefaults()
    {
        Proxies.Clear();
        Inner.Clear();
        Frames.Clear();
        for (int p = 0; p < FluidBodyParts.Count; p++)
        {
            if (!PartPresent[p])
                continue;
            var frame = DefaultFrame(p);
            var record = DefaultRecord(p, frame);
            Add(record, Shrunk(record), p, frame);
        }
    }

    // The inner backstop from the outer shape: well inside it (est. 60%).
    private static FluidProxyRecord Shrunk(FluidProxyRecord record)
    {
        var inner = record;
        if (record.Kind == FluidLayout.ProxyBox)
            inner.B = new float4(record.B.x * 0.6f, record.B.y * 0.6f, record.B.z * 0.6f, record.B.w);
        else
            inner.A = new float4(record.A.x, record.A.y, record.A.z, record.A.w * 0.6f);
        return inner;
    }

    private static bool IsSole(int part) => part is FluidBodyParts.LeftFoot or FluidBodyParts.RightFoot or FluidBodyParts.LeftToes or FluidBodyParts.RightToes;

    private static bool IsToes(int part) => part is FluidBodyParts.LeftToes or FluidBodyParts.RightToes;

    private static bool IsHand(int part) => part is FluidBodyParts.LeftHand or FluidBodyParts.RightHand;

    // Upright for soles, along the segment for limbs and hands, the world's axes at the bone for the head.
    private float4x4 DefaultFrame(int p)
    {
        var origin = Position(PartWorld[p]);
        var along = PartEnd[p] - origin;
        if (IsSole(p))
        {
            var flat = along - Up * float3.Dot(along, Up);
            var z = Unit(flat, Forward - Up * float3.Dot(Forward, Up));
            return FromAxes(float3.Cross(Up, z), Up, z, origin);
        }
        if (p == FluidBodyParts.Head)
            return FromAxes(float3.Cross(Up, Forward), Up, Forward, origin);
        var axis = Unit(along, Up);
        var reference = MathF.Abs(float3.Dot(axis, Up)) < 0.9f ? Up : Forward;
        var x = Unit(float3.Cross(reference, axis), float3.Right);
        var y = float3.Cross(axis, x);
        return FromAxes(x, y, axis, origin);
    }

    private FluidProxyRecord DefaultRecord(int p, in float4x4 frameWorld)
    {
        float s = Scale;
        var origin = Position(frameWorld);
        float length = (PartEnd[p] - Position(PartWorld[p])).Length;
        FluidProxyRecord record = default;
        record.Part = (uint)p;
        if (IsSole(p))
        {
            float above = float3.Dot(origin, Up) - FloorY;
            float bottom = -System.Math.Clamp(above, IsToes(p) ? 0.01f * s : 0.03f * s, 0.15f * s) + SoleInset;
            float top = 0.03f * s;
            float back = IsToes(p) ? 0f : -0.06f * s;
            float front = IsToes(p) ? 0.06f * s : MathF.Max(0.08f * s, Flat(PartEnd[p] - origin));
            record.Kind = FluidLayout.ProxyBox;
            record.A = new float4(0f, (bottom + top) * 0.5f, (back + front) * 0.5f, 0f);
            record.B = new float4(0.045f * s, (top - bottom) * 0.5f, (front - back) * 0.5f, 0f);
        }
        else if (IsHand(p))
        {
            record.Kind = FluidLayout.ProxyBox;
            record.A = new float4(0f, 0f, 0.085f * s, 0f);
            record.B = new float4(0.045f * s, 0.016f * s, 0.09f * s, 0f);
        }
        else if (p == FluidBodyParts.Head)
        {
            record.Kind = FluidLayout.ProxySphere;
            record.A = new float4(0f, 0.08f * s, 0.02f * s, 0.11f * s);
        }
        else
        {
            record.Kind = FluidLayout.ProxyCapsule;
            record.A = new float4(0f, 0f, 0f, DefaultRadius(p) * s);
            record.B = new float4(0f, 0f, MathF.Max(length, 0.02f * s), 0f);
        }
        return record;
    }

    private static float DefaultRadius(int p) => p switch
    {
        FluidBodyParts.LeftLowerLeg or FluidBodyParts.RightLowerLeg => 0.055f,
        FluidBodyParts.LeftUpperLeg or FluidBodyParts.RightUpperLeg => 0.08f,
        FluidBodyParts.LeftLowerArm or FluidBodyParts.RightLowerArm => 0.04f,
        FluidBodyParts.LeftUpperArm or FluidBodyParts.RightUpperArm => 0.05f,
        FluidBodyParts.Chest => 0.14f,
        _ => 0.05f,
    };

    private bool TryFit(int p, ref float4x4 frame, IReadOnlyList<float3> points, out FluidProxyRecord record)
    {
        float s = Scale;
        record = default;
        record.Part = (uint)p;
        if (IsSole(p))
        {
            ToLocalBounds(frame, points, out var mn, out var mx);
            mn.y += SoleInset;
            var half = (mx - mn) * 0.5f;
            if (half.x > 0.045f * s * MaxGrowth || half.y > 0.09f * s * MaxGrowth || half.z > 0.13f * s * MaxGrowth)
                return false;
            record.Kind = FluidLayout.ProxyBox;
            record.A = new float4((mn.x + mx.x) * 0.5f, (mn.y + mx.y) * 0.5f, (mn.z + mx.z) * 0.5f, 0f);
            record.B = new float4(half.x, half.y, half.z, 0f);
            return true;
        }
        if (IsHand(p))
        {
            frame = PrincipalFrame(points, frame);
            ToLocalBounds(frame, points, out var mn, out var mx);
            var half = (mx - mn) * 0.5f;
            float limit = 0.09f * s * MaxGrowth;
            if (half.x > limit || half.y > limit || half.z > limit)
                return false;
            record.Kind = FluidLayout.ProxyBox;
            record.A = new float4((mn.x + mx.x) * 0.5f, (mn.y + mx.y) * 0.5f, (mn.z + mx.z) * 0.5f, 0f);
            record.B = new float4(half.x, half.y, half.z, 0f);
            return true;
        }
        if (p == FluidBodyParts.Head)
        {
            // Round the head's own middle: the points nearest the rig's guess at it, so long hair neither drags the
            // centre down the back nor blows the radius past the shoulders.
            var centre = HeadCentre(frame, points);
            var distances = new float[points.Count];
            for (int i = 0; i < distances.Length; i++)
                distances[i] = (points[i] - centre).Length;
            float radius = MathF.Min(PercentileOf(distances, OuterPercentile), 0.13f * s);
            if (radius > 0.11f * s * MaxGrowth)
                return false;
            var local = ToLocal(frame, centre);
            record.Kind = FluidLayout.ProxySphere;
            record.A = new float4(local.x, local.y, local.z, radius);
            return true;
        }
        {
            var a = Position(frame);
            var axis = new float3(frame.c2.x, frame.c2.y, frame.c2.z);
            float length = MathF.Max((PartEnd[p] - Position(PartWorld[p])).Length, 0.02f * s);
            var distances = new float[points.Count];
            for (int i = 0; i < distances.Length; i++)
            {
                var d = points[i] - a;
                float t = System.Math.Clamp(float3.Dot(d, axis), 0f, length);
                distances[i] = (d - axis * t).Length;
            }
            float radius = PercentileOf(distances, OuterPercentile);
            if (radius > DefaultRadius(p) * s * MaxGrowth)
                return false;
            record.Kind = FluidLayout.ProxyCapsule;
            record.A = new float4(0f, 0f, 0f, radius);
            record.B = new float4(0f, 0f, length, 0f);
            return true;
        }
    }

    // The inner backstop from the closed surface's points, in the outer proxy's frame: boxes shrunk round the
    // points' own bounds, a sphere and capsules at the radius 90% of the closed surface lies outside of, shrunk again.
    private bool TryFitInner(int p, in float4x4 frame, IReadOnlyList<float3> closed, in FluidProxyRecord outer, out FluidProxyRecord record)
    {
        float s = Scale;
        record = outer;
        if (outer.Kind == FluidLayout.ProxyBox)
        {
            // The dense middle of the part, not its bounds: spread fingers and toes leave gaps a bounding box would
            // fill, and the backstop would stand out between them.
            var xs = new float[closed.Count];
            var ys = new float[closed.Count];
            var zs = new float[closed.Count];
            for (int i = 0; i < closed.Count; i++)
            {
                var l = ToLocal(frame, closed[i]);
                xs[i] = l.x;
                ys[i] = l.y;
                zs[i] = l.z;
            }
            float Lo(float[] v) => PercentileOf((float[])v.Clone(), 0.2f);
            float Hi(float[] v) => PercentileOf((float[])v.Clone(), 0.8f);
            var mn = new float3(Lo(xs), Lo(ys), Lo(zs));
            var mx = new float3(Hi(xs), Hi(ys), Hi(zs));
            var centre = (mn + mx) * 0.5f;
            // a sole box keeps its bottom up, so a skinned sole meets the floor first
            float lift = IsSole(p) ? 0.01f * s : 0f;
            var half = (mx - mn) * (0.5f * InnerBoxScale);
            half = new float3(MathF.Max(half.x, 0.004f * s), MathF.Max(half.y - lift * 0.5f, 0.003f * s), MathF.Max(half.z, 0.004f * s));
            centre = new float3(centre.x, centre.y + lift * 0.5f, centre.z);
            record.A = new float4(centre.x, centre.y, centre.z, 0f);
            record.B = new float4(half.x, half.y, half.z, 0f);
            return true;
        }
        if (outer.Kind == FluidLayout.ProxySphere)
        {
            // The largest sphere the closed surface holds round the skull's middle: a muzzle, a jaw or a mouth comes
            // closer to the head's middle than its sides do, and a sphere of the head's mean size would stand out there.
            if (!InscribedSphere(closed, HeadCentre(frame, closed), 0.05f * s, 0.01f * s, out var centre, out float empty))
                return false;
            float radius = System.Math.Clamp(empty * InnerScale, 0.025f * s, 0.095f * s);
            var local = ToLocal(frame, centre);
            record.A = new float4(local.x, local.y, local.z, radius);
            return true;
        }
        {
            // Per slice along the segment and per sector round the axis: a spine sits at the back of the torso and
            // the chest narrows to the neck, so one radius for every direction and every height would stand out. The
            // thinnest slice and sector decides.
            var a = Position(frame);
            var axis = new float3(frame.c2.x, frame.c2.y, frame.c2.z);
            var ax = new float3(frame.c0.x, frame.c0.y, frame.c0.z);
            var ay = new float3(frame.c1.x, frame.c1.y, frame.c1.z);
            float length = outer.B.z;
            var cells = new List<float>[Slices * Sectors];
            for (int k = 0; k < cells.Length; k++)
                cells[k] = new List<float>();
            for (int i = 0; i < closed.Count; i++)
            {
                var d = closed[i] - a;
                float t = float3.Dot(d, axis);
                if (t < 0f || t > length)
                    continue;
                var radial = d - axis * t;
                float angle = MathF.Atan2(float3.Dot(radial, ay), float3.Dot(radial, ax));
                int sector = System.Math.Clamp((int)((angle + MathF.PI) / (2f * MathF.PI) * Sectors), 0, Sectors - 1);
                int slice = System.Math.Clamp((int)(t / length * Slices), 0, Slices - 1);
                cells[slice * Sectors + sector].Add(radial.Length);
            }
            float thinnest = float.MaxValue;
            int used = 0;
            foreach (var list in cells)
            {
                if (list.Count < 4)
                    continue;
                used++;
                thinnest = MathF.Min(thinnest, PercentileOf(list.ToArray(), InnerPercentile));
            }
            if (used < Sectors)
                return false;
            float floor = 0.25f * DefaultRadius(p) * s;
            float radius = System.Math.Clamp(thinnest * InnerScale, floor, MathF.Max(outer.A.w * 0.9f, floor));
            // the round ends stay inside the segment's own length
            float cut = MathF.Min(radius, 0.3f * length);
            record.A = new float4(0f, 0f, cut, radius);
            record.B = new float4(0f, 0f, length - cut, 0f);
            return true;
        }
    }

    // Over a grid of centres round start (reach, step), the one whose nearest surface point is farthest, among those
    // the points enclose (some point within 37 degrees of each of fourteen directions); out: that centre and distance.
    private static bool InscribedSphere(IReadOnlyList<float3> points, float3 start, float reach, float step, out float3 centre, out float radius)
    {
        centre = start;
        radius = 0f;
        int n = (int)MathF.Round(reach / step);
        var directions = new float3[14];
        int at = 0;
        for (int ix = -1; ix <= 1; ix++)
            for (int iy = -1; iy <= 1; iy++)
                for (int iz = -1; iz <= 1; iz++)
                {
                    int nonZero = (ix != 0 ? 1 : 0) + (iy != 0 ? 1 : 0) + (iz != 0 ? 1 : 0);
                    if (nonZero == 1 || nonZero == 3)
                    {
                        var dir = new float3(ix, iy, iz);
                        directions[at++] = dir / dir.Length;
                    }
                }
        Span<bool> seen = stackalloc bool[14];
        for (int ix = -n; ix <= n; ix++)
            for (int iy = -n; iy <= n; iy++)
                for (int iz = -n; iz <= n; iz++)
                {
                    var c = start + new float3(ix, iy, iz) * step;
                    float nearest = float.MaxValue;
                    seen.Clear();
                    for (int i = 0; i < points.Count; i++)
                    {
                        var d = points[i] - c;
                        float l = d.Length;
                        if (l < nearest)
                            nearest = l;
                        if (nearest <= radius)
                            break;
                        if (l < 1e-6f)
                            continue;
                        var u = d / l;
                        for (int k = 0; k < directions.Length; k++)
                        {
                            if (!seen[k] && float3.Dot(u, directions[k]) > 0.8f)
                                seen[k] = true;
                        }
                    }
                    if (nearest <= radius)
                        continue;
                    bool enclosed = true;
                    foreach (bool b in seen)
                        enclosed &= b;
                    if (!enclosed)
                        continue;
                    radius = nearest;
                    centre = c;
                }
        return radius > 0f;
    }

    // The middle of a head: start at the rig default's centre, keep the half of the points nearest it, and average
    // them, twice.
    private float3 HeadCentre(in float4x4 frame, IReadOnlyList<float3> points)
    {
        var centre = frame.MultiplyPoint(new float3(0f, 0.08f * Scale, 0.02f * Scale));
        var distances = new float[points.Count];
        for (int round = 0; round < 2; round++)
        {
            for (int i = 0; i < points.Count; i++)
                distances[i] = (points[i] - centre).Length;
            float median = PercentileOf((float[])distances.Clone(), 0.5f);
            var sum = float3.Zero;
            int n = 0;
            for (int i = 0; i < points.Count; i++)
            {
                if (distances[i] > median)
                    continue;
                sum += points[i];
                n++;
            }
            if (n > 0)
                centre = sum / n;
        }
        return centre;
    }

    private void Add(FluidProxyRecord record, FluidProxyRecord inner, int part, in float4x4 frameWorld)
    {
        record.Bone = (uint)Frames.Count;
        inner.Bone = record.Bone;
        inner.Part = record.Part;
        inner.Kind = record.Kind;
        Frames.Add(PartWorld[part].Inverse * frameWorld);
        Proxies.Add(record);
        Inner.Add(inner);
    }

    // The joint a part's segment runs to, with a guess past the end of a chain.
    private float3 EndOf(HumanoidRig? rig, int p)
    {
        var origin = Position(PartWorld[p]);
        float s = Scale;
        float3? Bone(BodyNode node)
        {
            var b = rig?.TryGetBone(node);
            return b != null && !b.IsDestroyed ? b.GlobalPosition : null;
        }
        float3 Past(float3? end, float3 fallbackDirection, float length)
            => end ?? origin + Unit(fallbackDirection, Forward) * length;
        switch (p)
        {
            case FluidBodyParts.LeftFoot:
                return Past(Bone(BodyNode.LeftToes), Forward, 0.13f * s);
            case FluidBodyParts.RightFoot:
                return Past(Bone(BodyNode.RightToes), Forward, 0.13f * s);
            case FluidBodyParts.LeftToes:
            case FluidBodyParts.RightToes:
                return origin + Forward * 0.05f * s;
            case FluidBodyParts.LeftLowerLeg:
                return Past(Bone(BodyNode.LeftFoot), -Up, 0.42f * s);
            case FluidBodyParts.RightLowerLeg:
                return Past(Bone(BodyNode.RightFoot), -Up, 0.42f * s);
            case FluidBodyParts.LeftUpperLeg:
                return Past(Bone(BodyNode.LeftLowerLeg), -Up, 0.42f * s);
            case FluidBodyParts.RightUpperLeg:
                return Past(Bone(BodyNode.RightLowerLeg), -Up, 0.42f * s);
            case FluidBodyParts.LeftHand:
                return Past(Bone(BodyNode.LeftMiddleFinger_Proximal), origin - Position(PartWorld[FluidBodyParts.LeftLowerArm]), 0.09f * s);
            case FluidBodyParts.RightHand:
                return Past(Bone(BodyNode.RightMiddleFinger_Proximal), origin - Position(PartWorld[FluidBodyParts.RightLowerArm]), 0.09f * s);
            case FluidBodyParts.LeftLowerArm:
                return Past(Bone(BodyNode.LeftHand), -Up, 0.26f * s);
            case FluidBodyParts.RightLowerArm:
                return Past(Bone(BodyNode.RightHand), -Up, 0.26f * s);
            case FluidBodyParts.LeftUpperArm:
                return Past(Bone(BodyNode.LeftLowerArm), -Up, 0.28f * s);
            case FluidBodyParts.RightUpperArm:
                return Past(Bone(BodyNode.RightLowerArm), -Up, 0.28f * s);
            case FluidBodyParts.Chest:
                return Past(Bone(BodyNode.Neck) ?? Bone(BodyNode.Head), Up, 0.3f * s);
            default:
                return origin + Up * 0.2f * s;
        }
    }

    // The point cloud's own axes, longest first, as a right-handed frame at its centre. Falls back to the given frame
    // when the cloud is flat in every direction.
    private static float4x4 PrincipalFrame(IReadOnlyList<float3> points, in float4x4 fallback)
    {
        var centre = float3.Zero;
        foreach (var p in points)
            centre += p;
        centre /= points.Count;
        Span<float> c = stackalloc float[9];
        foreach (var p in points)
        {
            var d = p - centre;
            c[0] += d.x * d.x; c[1] += d.x * d.y; c[2] += d.x * d.z;
            c[4] += d.y * d.y; c[5] += d.y * d.z;
            c[8] += d.z * d.z;
        }
        c[3] = c[1];
        c[6] = c[2];
        c[7] = c[5];
        Span<float> v = stackalloc float[9];
        Span<float> e = stackalloc float[3];
        Jacobi(c, v, e);
        int i0 = 0, i1 = 1, i2 = 2;
        if (e[i1] > e[i0]) (i0, i1) = (i1, i0);
        if (e[i2] > e[i0]) (i0, i2) = (i2, i0);
        if (e[i2] > e[i1]) (i1, i2) = (i2, i1);
        if (!(e[i0] > 1e-12f))
            return fallback;
        // z along the longest axis, pointing the way the hand's segment runs; y the thinnest.
        var z = new float3(v[i0], v[3 + i0], v[6 + i0]);
        var x = new float3(v[i1], v[3 + i1], v[6 + i1]);
        var along = new float3(fallback.c2.x, fallback.c2.y, fallback.c2.z);
        if (float3.Dot(z, along) < 0f)
            z = -z;
        x = Unit(x - z * float3.Dot(x, z), float3.Right);
        var y = float3.Cross(z, x);
        return FromAxes(x, y, z, centre);
    }

    // Cyclic Jacobi on a symmetric 3x3 (row major); columns of v are the eigenvectors.
    private static void Jacobi(Span<float> a, Span<float> v, Span<float> eigen)
    {
        v.Clear();
        v[0] = v[4] = v[8] = 1f;
        for (int sweep = 0; sweep < 32; sweep++)
        {
            float off = MathF.Abs(a[1]) + MathF.Abs(a[2]) + MathF.Abs(a[5]);
            if (off < 1e-14f)
                break;
            for (int p = 0; p < 2; p++)
            {
                for (int q = p + 1; q < 3; q++)
                {
                    float apq = a[p * 3 + q];
                    if (MathF.Abs(apq) < 1e-20f)
                        continue;
                    float app = a[p * 3 + p], aqq = a[q * 3 + q];
                    float theta = (aqq - app) / (2f * apq);
                    float t = MathF.Sign(theta) / (MathF.Abs(theta) + MathF.Sqrt(theta * theta + 1f));
                    if (theta == 0f)
                        t = 1f;
                    float cs = 1f / MathF.Sqrt(t * t + 1f), sn = t * cs;
                    for (int k = 0; k < 3; k++)
                    {
                        float akp = a[k * 3 + p], akq = a[k * 3 + q];
                        a[k * 3 + p] = cs * akp - sn * akq;
                        a[k * 3 + q] = sn * akp + cs * akq;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        float apk = a[p * 3 + k], aqk = a[q * 3 + k];
                        a[p * 3 + k] = cs * apk - sn * aqk;
                        a[q * 3 + k] = sn * apk + cs * aqk;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        float vkp = v[k * 3 + p], vkq = v[k * 3 + q];
                        v[k * 3 + p] = cs * vkp - sn * vkq;
                        v[k * 3 + q] = sn * vkp + cs * vkq;
                    }
                }
            }
        }
        eigen[0] = a[0];
        eigen[1] = a[4];
        eigen[2] = a[8];
    }

    private static void ToLocalBounds(in float4x4 frame, IReadOnlyList<float3> points, out float3 mn, out float3 mx)
    {
        mn = new float3(float.MaxValue);
        mx = new float3(float.MinValue);
        foreach (var p in points)
        {
            var l = ToLocal(frame, p);
            mn = new float3(MathF.Min(mn.x, l.x), MathF.Min(mn.y, l.y), MathF.Min(mn.z, l.z));
            mx = new float3(MathF.Max(mx.x, l.x), MathF.Max(mx.y, l.y), MathF.Max(mx.z, l.z));
        }
    }

    // Frames are orthonormal at equip, so their inverse is the transpose.
    private static float3 ToLocal(in float4x4 frame, in float3 p)
    {
        var d = p - Position(frame);
        return new float3(
            d.x * frame.c0.x + d.y * frame.c0.y + d.z * frame.c0.z,
            d.x * frame.c1.x + d.y * frame.c1.y + d.z * frame.c1.z,
            d.x * frame.c2.x + d.y * frame.c2.y + d.z * frame.c2.z);
    }

    private static float PercentileOf(float[] values, float fraction)
    {
        Array.Sort(values);
        int index = System.Math.Clamp((int)MathF.Ceiling(fraction * values.Length) - 1, 0, values.Length - 1);
        return values[index];
    }

    private float Flat(in float3 v) => (v - Up * float3.Dot(v, Up)).Length;

    private static float Height(HumanoidRig? rig)
    {
        var head = rig?.TryGetBone(BodyNode.Head);
        var foot = rig?.TryGetBone(BodyNode.LeftFoot);
        if (head == null || foot == null || head.IsDestroyed || foot.IsDestroyed)
            return 0f;
        // Head bone to ankle runs about 85% of standing height.
        return (head.GlobalPosition.y - foot.GlobalPosition.y) / 0.85f;
    }

    private static float4x4 FromAxes(in float3 x, in float3 y, in float3 z, in float3 origin)
        => new(new float4(x.x, x.y, x.z, 0f), new float4(y.x, y.y, y.z, 0f), new float4(z.x, z.y, z.z, 0f), new float4(origin.x, origin.y, origin.z, 1f));

    private static float3 Position(in float4x4 m) => new(m.c3.x, m.c3.y, m.c3.z);

    private static float3 Unit(in float3 v, in float3 fallback)
    {
        float len = v.Length;
        if (len > 1e-6f)
            return v / len;
        float f = fallback.Length;
        return f > 1e-6f ? fallback / f : float3.Up;
    }
}
