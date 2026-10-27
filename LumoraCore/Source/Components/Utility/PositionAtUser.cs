// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// Keeps the slot positioned at its user's head plus a vertical offset
// (nameplate anchor). Runs on every peer from the replicated head transform
// and writes the result locally (no sync generation) - broadcasting it would
// duplicate the head stream every frame. Offset scales with the user so
// plates stay above scaled avatars. - xlinka
[ComponentCategory("Utility")]
[DefaultUpdateOrder(3000)]
public class PositionAtUser : Component
{
    public readonly Sync<float> VerticalOffset = new();

    // Follow THIS slot instead of the wearer's tracked head node, when it is set.
    //
    // HeadSlot is where the PERSON's head is, which on a two-legged avatar is also where the avatar's
    // head is, so nothing ever needed to distinguish them. On an animal they are nowhere near each
    // other: the wearer's head node stays at human standing height while the dog's head is out front
    // and low, so the nameplate hung in the air over empty space. Anything that knows better points
    // this at the avatar's own head bone. Null falls back to the tracked node, so an unworn or
    // unrecognised avatar behaves exactly as before. -xlinka
    public readonly SyncRef<Slot> Anchor = new();

    private Slot? _measuredHead;
    private Slot? _measuredAvatar;
    private double _nextMeshCheck;
    private readonly List<(SkinnedMeshRenderer Renderer, MeshDataAsset? Asset, int Bones, int Vertices)> _meshes = new();
    private readonly List<(Slot Bone, BoundingBox Bounds)> _headBounds = new();
    private readonly List<Slot> _headBones = new();
    private readonly List<SkinnedMeshRenderer> _renderers = new();
    private readonly List<(SkinnedMeshRenderer Renderer, MeshDataAsset? Asset, int Bones, int Vertices)> _current = new();
    private float _topAbove;
    private ulong _topMeasuredAt = ulong.MaxValue;
    private const ulong TopEvery = 6;
    // Where the avatar's head stands from the tracked head. An avatar further off is posed every few frames and
    // one that is a picture hardly at all, so the plate rides the tracked head, which moves every frame, at that
    // lead, and the lead is measured again with the top of the head.
    private float3 _headLead;

    public override void OnInit()
    {
        base.OnInit();
        VerticalOffset.Value = 0.25f;
    }

    public override void OnStart()
    {
        base.OnStart();
        if (UpdateOrder == 0)
            UpdateOrder = 3000;
    }

    private float HeadTop(Slot head, Slot? avatar)
    {
        bool changed = head != _measuredHead || avatar != _measuredAvatar;
        double now = World?.Time.TotalTime ?? 0;
        if (changed || now >= _nextMeshCheck)
        {
            _nextMeshCheck = now + 1;
            _renderers.Clear();
            avatar?.GetComponentsInChildren(_renderers);
            var current = _current;
            current.Clear();
            foreach (var renderer in _renderers)
                current.Add((renderer, renderer.MeshAsset.Asset, renderer.Bones.Count, renderer.MeshAsset.Asset?.VertexCount ?? 0));
            _renderers.Clear();
            if (current.Count != _meshes.Count)
                changed = true;
            for (int i = 0; !changed && i < current.Count; i++)
                changed = current[i] != _meshes[i];
            if (changed)
            {
                _measuredHead = head;
                _measuredAvatar = avatar;
                _meshes.Clear();
                _meshes.AddRange(current);
                MeasureHead(head);
            }
        }

        float top = head.GlobalPosition.y;
        foreach (var bone in _headBones)
            if (!bone.IsDestroyed && bone.IsActive)
                top = MathF.Max(top, bone.GlobalPosition.y);
        foreach (var entry in _headBounds)
        {
            if (entry.Bone.IsDestroyed || !entry.Bone.IsActive)
                continue;
            var bounds = entry.Bounds;
            var toWorld = entry.Bone.LocalToWorld;
            for (int i = 0; i < 8; i++)
            {
                var corner = new float3((i & 1) == 0 ? bounds.Min.x : bounds.Max.x,
                    (i & 2) == 0 ? bounds.Min.y : bounds.Max.y,
                    (i & 4) == 0 ? bounds.Min.z : bounds.Max.z);
                top = MathF.Max(top, HeightOf(toWorld, corner));
            }
        }
        return top;
    }

    // The height a point is carried to, worked out the way the full transform works out its y and nothing
    // else: the same sums in the same order, over the same w.
    private static float HeightOf(in float4x4 toWorld, in float3 point)
    {
        float y = toWorld.c0.y * point.x + toWorld.c1.y * point.y + toWorld.c2.y * point.z + toWorld.c3.y * 1f;
        float w = toWorld.c0.w * point.x + toWorld.c1.w * point.y + toWorld.c2.w * point.z + toWorld.c3.w * 1f;
        return y / w;
    }

    private void MeasureHead(Slot head)
    {
        _headBounds.Clear();
        _headBones.Clear();
        AddHeadBones(head);
        foreach (var entry in _meshes)
        {
            var renderer = entry.Renderer;
            var mesh = entry.Asset?.MeshData;
            if (mesh == null || !mesh.HasBoneBindings)
                continue;
            int count = System.Math.Min(mesh.BoneCount, renderer.Bones.Count);
            var bounds = new BoundingBox[count];
            var included = new bool[count];
            for (int i = 0; i < count; i++)
            {
                var bone = renderer.Bones[i];
                included[i] = bone != null && !bone.IsDestroyed && (bone == head || bone.IsDescendantOf(head));
                bounds[i].MakeEmpty();
            }
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                var vertex = mesh.GetVertex(i);
                var binding = vertex.BoneBinding;
                for (int j = 0; j < 4; j++)
                {
                    int index = (int)binding.boneIndices[j];
                    if (index < 0 || index >= count || !included[index] || binding.boneWeights[j] < 0.2f)
                        continue;
                    bounds[index].Encapsulate(mesh.GetBoneBindPose(index).MultiplyPoint(vertex.Position));
                }
            }
            for (int i = 0; i < count; i++)
                if (included[i] && float.IsFinite(bounds[i].Min.y))
                    _headBounds.Add((renderer.Bones[i]!, bounds[i]));
        }
    }

    private void AddHeadBones(Slot bone)
    {
        _headBones.Add(bone);
        foreach (var child in bone.Children)
            AddHeadBones(child);
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        var userRoot = Slot?.ActiveUserRoot;
        if (userRoot == null)
            return;
        var anchor = Anchor.Target;
        var head = anchor != null && !anchor.IsDestroyed && anchor.IsActive ? anchor
            : userRoot.GetRegisteredComponent<Avatar.AvatarEquipManager>()?.AvatarHead ?? userRoot.HeadSlot;
        var parent = Slot?.Parent;
        if (head == null || head.IsDestroyed || parent == null)
            return;

        // A plate over somebody behind the viewer or far past them follows less often; nobody sees it move.
        ulong frame = World!.Time.UpdateIndex;
        var view = Avatar.AvatarViewLod.Look(userRoot.Slot);
        if (view.Level == Avatar.AvatarViewLod.Level.Hidden && !view.RunsThisFrame(frame))
            return;

        float scale = MathF.Max(userRoot.GlobalScale, 0.01f);
        var avatar = userRoot.GetRegisteredComponent<Avatar.AvatarEquipManager>()?.EquippedAvatar;
        var tracked = userRoot.HeadSlot;
        bool picture = view.Level == Avatar.AvatarViewLod.Level.StandIn;
        bool riding = view.Level is Avatar.AvatarViewLod.Level.Reduced or Avatar.AvatarViewLod.Level.Distant or Avatar.AvatarViewLod.Level.StandIn
            && tracked != null && !tracked.IsDestroyed && !ReferenceEquals(tracked, head);
        var target = riding ? tracked!.GlobalPosition + _headLead : head.GlobalPosition;
        // The top of the head is every head bone and the skin hung on them. It turns with the head, so it is kept
        // as a height above the head and walked again every few frames instead of every frame: a crowd's plates
        // were a millisecond of bone walks a frame. -xlinka
        if (!picture && (head != _measuredHead || avatar != _measuredAvatar || frame < _topMeasuredAt || frame - _topMeasuredAt >= TopEvery))
        {
            var bone = head.GlobalPosition;
            _topAbove = HeadTop(head, avatar) - bone.y;
            _topMeasuredAt = frame;
            _headLead = tracked != null && !tracked.IsDestroyed ? bone - tracked.GlobalPosition : float3.Zero;
        }
        target.y = MathF.Max(target.y + VerticalOffset.Value * scale, target.y + _topAbove + 0.12f * scale);
        var local = parent.GlobalPointToLocal(target);

        if ((Slot!.LocalPosition.Value - local).LengthSquared > 1e-10f)
        {
            // This anchor lives under the (possibly REMOTE) user's root, so on an observer the write's actor is
            // the observer's own user, who doesn't own it -> the permission gate denies it and the badge stops
            // tracking the head. It's a purely-local visual follow of already-replicated head data (no sync
            // generated), so bypass the gate - same treatment as the remote-body stream apply. -xlinka
            using var bypass = World?.DataModelPermissions?.EnterSystemBypass();
            Slot.LocalPosition.SetValueSilently(local, change: true);
        }
    }
}
