// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

// WHO IS A PICTURE.
//
// A room of thirty people cannot pose and draw thirty bodies a frame. Somebody else's avatar past the picture
// distance, or further down the list than the nearest few (EngineSettings.AvatarStandInDistance and
// AvatarFullDetailCount), is drawn as a picture of itself: taken from where the viewer stands, now and then, and
// handed to the platform to draw on a card that faces the viewer. Under the picture the body is hidden and posed
// only often enough to keep its walk going (AvatarViewLod.Level.StandIn), so its IK, hair, animation and skin
// cost next to nothing.
//
// Nothing pops: going over, the picture fades in over the live body and only then is the body hidden; coming
// back, the body is shown and posed under the picture before the picture fades away. A new picture blends over
// the last one on the platform side. Your own avatar is never a picture, and neither is one still arriving
// (AvatarArrival holds it hidden) or one nearer than MinPictureMetres.
//
// Local to this machine and worked out once a frame, before the first thing asks (AvatarViewLod). -xlinka
public sealed class AvatarCrowd
{
    public enum Phase : byte
    {
        Body,
        Entering,
        Picture,
        Leaving,
    }

    public sealed class Member
    {
        public Slot Root { get; internal set; } = null!;
        public User? User { get; internal set; }
        // The worn avatar, which is what gets hidden and what the picture is taken of.
        public Slot? Avatar { get; internal set; }
        public Phase Phase { get; internal set; }
        // 0 is all body, 1 all picture.
        public float Fade { get; internal set; }
        public float Distance { get; internal set; }
        public bool InView { get; internal set; }
        // Take a picture this frame: the body is posed for it before the hooks run.
        public bool CaptureNow { get; internal set; }
        public int Pictures { get; internal set; }
        // Set by whatever draws the pictures once it has one to show. The body is never hidden before then.
        public bool HasPicture { get; set; }

        internal double ChangedAt = double.NegativeInfinity;
        internal double PictureAt = double.NegativeInfinity;
        internal double AvatarCheckedAt = double.NegativeInfinity;
        internal float3 PictureLook;
        internal bool PictureDue;
        internal ulong SeenFrame;
        internal bool Hiding;

        // What a picture is taken of: every skinned and plain renderer on the avatar, and its biggest skeleton for
        // the bounds. Gathered again when the avatar or the slot structure changed, at most twice a second.
        public List<SkinnedMeshRenderer> SkinnedParts { get; } = new();
        public List<MeshRenderer> MeshParts { get; } = new();
        public SkeletonBuilder? MainSkeleton { get; private set; }
        public int PartsVersion { get; private set; }

        private readonly List<SkeletonBuilder> _skeletons = new();
        private Slot? _partsOf;
        private int _partsEpoch;
        private double _partsAt = double.NegativeInfinity;

        public void RefreshParts(double now)
        {
            var avatar = Avatar;
            bool same = ReferenceEquals(avatar, _partsOf) && avatar is { IsDestroyed: false };
            if (same && (_partsEpoch == Slot.StructureEpoch || now - _partsAt < 0.5))
                return;
            _partsOf = avatar;
            _partsEpoch = Slot.StructureEpoch;
            _partsAt = now;
            SkinnedParts.Clear();
            MeshParts.Clear();
            _skeletons.Clear();
            MainSkeleton = null;
            if (avatar != null && !avatar.IsDestroyed)
            {
                avatar.GetComponentsInChildren(SkinnedParts);
                avatar.GetComponentsInChildren(MeshParts);
                avatar.GetComponentsInChildren(_skeletons);
                int most = 0;
                foreach (var skeleton in _skeletons)
                {
                    if (skeleton.BoneCount > most)
                    {
                        most = skeleton.BoneCount;
                        MainSkeleton = skeleton;
                    }
                }
                _skeletons.Clear();
            }
            PartsVersion++;
        }
    }

    // Set by the platform once it can draw pictures. Without one nothing is ever a picture, so a headless peer
    // keeps its bodies as they are.
    public static Func<World, bool>? CanDraw;

    private const float Hysteresis = 1.5f;
    private const float MinPictureMetres = 6f;
    private const double DwellSeconds = 1.5;
    private const float FadeSeconds = 0.4f;
    private const float AngleStep = 10f * MathF.PI / 180f;
    private const double AvatarRecheckSeconds = 0.5;
    // Pictures taken because one went stale, at most this often across everyone: each is a small render of its own.
    private const double PictureGapSeconds = 1.0 / 15.0;

    private static readonly ConditionalWeakTable<World, AvatarCrowd> Crowds = new();

    private readonly World _world;
    private readonly Dictionary<Slot, Member> _members = new();
    private readonly List<Member> _list = new();
    private readonly List<User> _users = new();
    private ulong _frame = ulong.MaxValue;
    private double _staleTakenAt = double.NegativeInfinity;

    private AvatarCrowd(World world)
    {
        _world = world;
        world.AddFramePass(Refresh);
    }

    public static AvatarCrowd For(World world) => Crowds.GetValue(world, static w => new AvatarCrowd(w));

    public static AvatarCrowd? Existing(World? world) => world != null && Crowds.TryGetValue(world, out var crowd) ? crowd : null;

    // Everyone this machine sees, nearest first; refreshed for this frame.
    public IReadOnlyList<Member> Members
    {
        get
        {
            Refresh();
            return _list;
        }
    }

    public static Member? Find(World? world, Slot? root)
    {
        if (world == null || root == null)
            return null;
        var crowd = For(world);
        crowd.Refresh();
        return crowd._members.TryGetValue(root, out var member) ? member : null;
    }

    private void Refresh()
    {
        var world = _world;
        if (world.IsDestroyed)
            return;
        ulong frame = world.Time?.UpdateIndex ?? 0;
        if (frame == _frame)
            return;
        _frame = frame;

        double now = world.Time!.TotalTime;
        float dt = System.Math.Clamp(world.Time.Delta, 0f, 0.1f);
        var head = world.LocalUser?.Root?.HeadSlot;
        float pictureAt = EngineSettings.AvatarStandInDistance;
        int budget = EngineSettings.AvatarFullDetailCount;
        bool on = head != null && !head.IsDestroyed && (pictureAt > 0f || budget > 0)
            && world.Focus == World.WorldFocus.Focused && (CanDraw?.Invoke(world) ?? false);

        float3 eye = on ? head!.GlobalPosition : float3.Zero;
        float3 forward = on ? head!.GlobalRotation * float3.Backward : float3.Backward;

        _users.Clear();
        if (on)
            world.GetAllUsers(_users);
        var local = world.LocalUser;
        foreach (var user in _users)
        {
            if (user == null || user.IsDestroyed || ReferenceEquals(user, local))
                continue;
            var userRoot = user.Root;
            if (userRoot == null || userRoot.IsDestroyed || userRoot.Slot == null || userRoot.Slot.IsDestroyed)
                continue;
            var root = userRoot.Slot;
            if (!_members.TryGetValue(root, out var member))
            {
                member = new Member { Root = root };
                _members[root] = member;
                _list.Add(member);
            }
            member.User = user;
            member.SeenFrame = frame;
            var to = root.GlobalPosition - eye;
            float distance = to.Length;
            member.Distance = distance;
            member.InView = distance < 3f || (distance > 1e-4f && float3.Dot(forward, to / distance) > 0.17f);
            if (now - member.AvatarCheckedAt >= AvatarRecheckSeconds || member.Avatar is { IsDestroyed: true })
            {
                member.AvatarCheckedAt = now;
                var avatar = userRoot.GetRegisteredComponent<AvatarEquipManager>()?.EquippedAvatar;
                if (!ReferenceEquals(avatar, member.Avatar))
                {
                    Unhide(member);
                    member.Avatar = avatar;
                }
            }
        }
        _users.Clear();

        // Gone, or nothing to see this frame: back to a body at once.
        for (int i = _list.Count - 1; i >= 0; i--)
        {
            var member = _list[i];
            if (member.SeenFrame == frame && !member.Root.IsDestroyed)
                continue;
            Unhide(member);
            _members.Remove(member.Root);
            _list[i] = _list[^1];
            _list.RemoveAt(_list.Count - 1);
        }

        _list.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));

        // One picture a frame, whoever asks: every copy shown for a picture is on the one band the picture cameras
        // film, so two taken together each had the other standing in it.
        Member? due = null;
        Member? best = null;
        float bestScore = 1f;
        for (int i = 0; i < _list.Count; i++)
        {
            var member = _list[i];
            member.CaptureNow = false;
            var avatar = member.Avatar;
            // Still arriving, or somebody this viewer has blocked (their whole root is hidden): no picture of it.
            bool arriving = avatar == null || avatar.IsDestroyed || !avatar.IsActive || avatar.IsLocallyHiddenFor(Slot.LocalHide.Other)
                || member.Root.IsLocallyHidden || member.User?.IsBlockedByMe == true;
            bool picture = member.Phase is Phase.Entering or Phase.Picture;
            float distance = member.Distance;
            bool far = pictureAt > 0f && distance > (picture ? pictureAt - Hysteresis : pictureAt + Hysteresis);
            bool crowded = budget > 0 && (picture ? i >= budget - 1 : i >= budget);
            bool wanted = on && !arriving && distance >= MinPictureMetres && (far || crowded);
            Step(member, wanted, arriving || !on, now, dt);

            if (member.Phase is Phase.Entering or Phase.Picture)
            {
                // The first picture goes as it starts to fade in, and another as the body goes, so the picture left
                // standing is the body as it was when it went; one that did not come out is asked for again.
                bool forced = member.Pictures == 0 || member.PictureDue || (!member.HasPicture && now - member.PictureAt > 0.25);
                if (forced)
                {
                    due ??= member;
                    continue;
                }
                if (!member.InView)
                    continue;
                float angle = MathF.Acos(System.Math.Clamp(float3.Dot(LookFrom(member, eye), member.PictureLook), -1f, 1f));
                double every = distance < 15f ? 0.5 : distance < 30f ? 1.0 : 2.0;
                float score = MathF.Max(angle / AngleStep, (float)((now - member.PictureAt) / every));
                if (score >= bestScore)
                {
                    bestScore = score;
                    best = member;
                }
            }
        }
        if (due != null)
        {
            TakePicture(due, eye, now);
        }
        else if (best != null && now - _staleTakenAt >= PictureGapSeconds)
        {
            _staleTakenAt = now;
            TakePicture(best, eye, now);
        }
    }

    private void Step(Member member, bool wanted, bool drop, double now, float dt)
    {
        float step = dt / FadeSeconds;
        switch (member.Phase)
        {
            case Phase.Body:
                if (wanted && now - member.ChangedAt >= DwellSeconds)
                {
                    member.Phase = Phase.Entering;
                    member.Fade = 0f;
                    member.Pictures = 0;
                    member.ChangedAt = now;
                }
                break;
            case Phase.Entering:
                if (!wanted)
                {
                    member.Phase = Phase.Leaving;
                    member.ChangedAt = now;
                    break;
                }
                member.Fade = MathF.Min(1f, member.Fade + step);
                if (member.Fade >= 1f && member.HasPicture)
                {
                    member.Phase = Phase.Picture;
                    member.ChangedAt = now;
                    member.PictureDue = true;
                    Hide(member);
                }
                break;
            case Phase.Picture:
                if (drop || (!wanted && now - member.ChangedAt >= DwellSeconds))
                {
                    Unhide(member);
                    member.Phase = Phase.Leaving;
                    member.ChangedAt = now;
                }
                break;
            case Phase.Leaving:
                if (wanted && !drop)
                {
                    member.Phase = Phase.Entering;
                    member.ChangedAt = now;
                    break;
                }
                member.Fade = MathF.Max(0f, member.Fade - step);
                if (member.Fade <= 0f)
                {
                    member.Phase = Phase.Body;
                    member.ChangedAt = now;
                }
                break;
        }
        if (drop && member.Phase != Phase.Body)
        {
            // Switched off, out of focus or the avatar went away: no fade, there may be nothing left to draw it.
            Unhide(member);
            member.Phase = Phase.Body;
            member.Fade = 0f;
            member.ChangedAt = now;
        }
    }

    // Where the viewer is, in the avatar's own frame, so the avatar turning round asks for a new picture as much
    // as the viewer walking round it does.
    private static float3 LookFrom(Member member, float3 eye)
    {
        var root = member.Root;
        var look = root.GlobalRotation.Inverse * (eye - root.GlobalPosition);
        float length = look.Length;
        return length > 1e-5f ? look / length : float3.Backward;
    }

    private static void TakePicture(Member member, float3 eye, double now)
    {
        member.CaptureNow = true;
        member.PictureDue = false;
        member.PictureAt = now;
        member.PictureLook = LookFrom(member, eye);
        member.Pictures++;
    }

    private static void Hide(Member member)
    {
        var avatar = member.Avatar;
        if (avatar == null || avatar.IsDestroyed)
            return;
        avatar.SetLocallyHidden(Slot.LocalHide.StandIn, true);
        member.Hiding = true;
    }

    private static void Unhide(Member member)
    {
        if (!member.Hiding)
            return;
        member.Hiding = false;
        var avatar = member.Avatar;
        if (avatar != null && !avatar.IsDestroyed)
            avatar.SetLocallyHidden(Slot.LocalHide.StandIn, false);
    }
}
