// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Assets.Animation;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

// GESTURES ON THE WEARER'S OWN BODY.
//
// Plays animation clips on the head and hands the way tracking moves them: on the wearer's machine only, written
// into the body nodes after tracking has put them, so the pose streams to everyone like any other movement and
// every peer's IK, fingers, hair and tails follow it. Channels are named by node and property:
//   Head, LeftHand, RightHand . Position (float3, metres in the user's space, authored for a 1.62 m eye)
//   Head, LeftHand, RightHand . Rotation (floatQ, in the user's space)
//   LeftHand, RightHand . Fingers (float4 curl per joint in the hand presets' units: thumb, index, middle, ring;
//   the pinky follows the ring) and . Splay (float, the thumb).
// A layer either adds to what is there (an idle under everything: breathing, a weight shift, a drifting head) or
// takes the pose over by its weight (a wave), and each fades in and out over its own times, so gestures overlap
// and hand over without a snap. The head is where its wearer looks, so a layer only ever adds to it: a nod, a
// tilt, a laugh. Positions scale by PoseScale, the wearer's eye height over 1.62 m. Fingers are only shaped
// while ShapeFingers is on; otherwise live hands keep them. -xlinka
[ComponentCategory("Users/Avatar")]
[DefaultUpdateOrder(-900000)] // after the tracked device positioners put the nodes, before the IK reads them
public sealed class BodyGesturePlayer : UserRootComponent
{
    public const float ReferenceEyeHeight = 1.62f;

    public readonly Sync<float> PoseScale = new(1f);
    public readonly Sync<bool> ShapeFingers = new(false);
    // Curl per joint of a hand at rest (thumb, index, middle, ring), and the thumb's splay.
    public readonly Sync<float4> RestCurl = new(new float4(0.15f, 0.18f, 0.22f, 0.26f));
    public readonly Sync<float> RestSplay = new(0f);

    public sealed class Layer
    {
        public AnimationClip Clip = null!;
        public bool Additive;
        public bool Loop;
        public float Weight = 1f;
        public float Speed = 1f;
        public float FadeIn = 0.2f;
        public float FadeOut = 0.3f;
        internal double Start;
        internal double StopAt = double.PositiveInfinity;
        internal float StopFade;

        public bool Finished { get; internal set; }

        // When it started, in the world's time.
        public double Started => Start;

        // Fades the layer out from now, over its own fade unless one is given.
        public void Stop(float fade = -1f) => _stopRequest = fade < 0f ? FadeOut : fade;

        internal float? _stopRequest;
    }

    private readonly List<Layer> _layers = new();
    private static readonly string[] Nodes = { "Head", "LeftHand", "RightHand" };
    private readonly (float3 Position, floatQ Rotation, bool Valid)[] _written = new (float3, floatQ, bool)[3];
    private readonly (float3 Position, floatQ Rotation)[] _base = new (float3, floatQ)[3];
    private readonly StaticHandPose _hands = new();
    private HandPoseStreamManager? _handStream;
    private IHandPoseSource? _handsBefore;

    public IReadOnlyList<Layer> Layers => _layers;

    // Starts a clip now. A layer that does not loop ends with its clip, fading out over its last FadeOut.
    public Layer Play(AnimationClip clip, bool additive = false, bool loop = false, float fadeIn = 0.2f, float fadeOut = 0.3f,
        float weight = 1f, float speed = 1f)
    {
        var layer = new Layer
        {
            Clip = clip, Additive = additive, Loop = loop, FadeIn = fadeIn, FadeOut = fadeOut, Weight = weight,
            Speed = speed <= 0f ? 1f : speed, Start = Now,
        };
        _layers.Add(layer);
        return layer;
    }

    public void StopAll(float fade = 0.3f)
    {
        foreach (var layer in _layers)
            layer.Stop(fade);
    }

    private double Now => World?.Time.TotalTime ?? 0.0;

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (!IsUnderLocalUser)
            return;
        var root = Slot.ActiveUserRoot;
        var space = root?.Slot;
        if (root == null || space == null || space.IsDestroyed)
            return;

        double now = Now;
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            if (layer._stopRequest is float fade)
            {
                layer._stopRequest = null;
                if (now < layer.StopAt)
                {
                    layer.StopAt = now;
                    layer.StopFade = MathF.Max(fade, 1e-3f);
                }
            }
            if (Envelope(layer, now) <= 0f && (now > layer.StopAt || (!layer.Loop && Local(layer, now) >= layer.Clip.Duration)))
            {
                layer.Finished = true;
                _layers.RemoveAt(i);
            }
        }

        float scale = PoseScale.Value > 1e-3f ? PoseScale.Value : 1f;
        PoseNode(0, root.HeadSlot, space, scale, now);
        PoseNode(1, Tracked(root, BodyNode.LeftController, BodyNode.LeftHand), space, scale, now);
        PoseNode(2, Tracked(root, BodyNode.RightController, BodyNode.RightHand), space, scale, now);
        ShapeHands(root, now);
    }

    // The node tracking moves for a hand: the controller's, which the hand's socket rides under.
    private readonly List<TrackedDevicePositioner> _positioners = new();

    private Slot? Tracked(UserRoot root, BodyNode controller, BodyNode hand)
    {
        Slot? fallback = null;
        _positioners.Clear();
        root.GetRegisteredComponents(_positioners);
        foreach (var positioner in _positioners)
        {
            if (positioner.IsDestroyed)
                continue;
            if (positioner.AutoBodyNode.Value == controller)
                return positioner.Slot;
            if (positioner.AutoBodyNode.Value == hand)
                fallback = positioner.Slot;
        }
        return fallback;
    }

    // 0..1: in over FadeIn from the start, out over FadeOut before the clip ends (or after a stop), times Weight.
    private static float Envelope(Layer layer, double now)
    {
        double age = now - layer.Start;
        float w = layer.FadeIn > 1e-3f ? (float)System.Math.Clamp(age / layer.FadeIn, 0.0, 1.0) : 1f;
        if (!layer.Loop && layer.FadeOut > 1e-3f && layer.Clip.Duration > 0f)
        {
            double left = (layer.Clip.Duration - Local(layer, now)) / layer.Speed;
            w = MathF.Min(w, (float)System.Math.Clamp(left / layer.FadeOut, 0.0, 1.0));
        }
        if (now > layer.StopAt)
            w = MathF.Min(w, 1f - (float)System.Math.Clamp((now - layer.StopAt) / layer.StopFade, 0.0, 1.0));
        w = w * w * (3f - 2f * w);
        return w * layer.Weight;
    }

    private static float Local(Layer layer, double now)
    {
        float t = (float)((now - layer.Start) * layer.Speed);
        return layer.Loop ? layer.Clip.Wrap(t, PlaybackLoopMode.Loop) : MathF.Min(t, layer.Clip.Duration);
    }

    private void PoseNode(int index, Slot? node, Slot space, float scale, double now)
    {
        if (node == null || node.IsDestroyed)
            return;
        // What tracking put there this frame; if nothing did, the node still holds what this wrote last, and the
        // pose is built again from last frame's base so additive layers never pile up.
        var position = space.GlobalPointToLocal(node.GlobalPosition);
        var rotation = space.GlobalRotationToLocal(node.GlobalRotation);
        var written = _written[index];
        if (written.Valid && float3.Distance(written.Position, position) < 1e-5f && MathF.Abs(floatQ.Dot(written.Rotation, rotation)) > 0.999999f)
        {
            position = _base[index].Position;
            rotation = _base[index].Rotation;
        }
        _base[index] = (position, rotation);

        bool head = index == 0;
        bool any = false;
        string name = Nodes[index];
        foreach (var layer in _layers)
        {
            float w = Envelope(layer, now);
            if (w <= 1e-4f)
                continue;
            float t = Local(layer, now);
            bool additive = layer.Additive || head;
            if (layer.Clip.FindTrack(name, "Position") is CurveAnimationTrack<float3> moves && moves.Evaluate(t, out var p))
            {
                position = additive ? position + p * (scale * w) : float3.Lerp(position, p * scale, w);
                any = true;
            }
            if (layer.Clip.FindTrack(name, "Rotation") is CurveAnimationTrack<floatQ> turns && turns.Evaluate(t, out var r))
            {
                rotation = additive ? (rotation * floatQ.Slerp(floatQ.Identity, r, w)).Normalized : floatQ.Slerp(rotation, r, w);
                any = true;
            }
        }
        if (!any)
        {
            _written[index] = default;
            return;
        }

        var global = space.LocalPointToGlobal(position);
        var globalRotation = space.LocalRotationToGlobal(rotation);
        var parent = node.Parent;
        var local = parent != null ? parent.GlobalPointToLocal(global) : global;
        var localRotation = parent != null ? parent.GlobalRotationToLocal(globalRotation) : globalRotation;
        // A streamed node sends its pose on the stream, so the field write stays quiet, as tracking's does.
        if (node.GetComponent<TransformStreamDriver>() is { IsDestroyed: false })
        {
            node.LocalPosition.SetValueSilently(local, change: true);
            node.LocalRotation.SetValueSilently(localRotation, change: true);
        }
        else
        {
            node.LocalPosition.Value = local;
            node.LocalRotation.Value = localRotation;
        }
        _written[index] = (position, rotation, true);
    }

    private void ShapeHands(UserRoot root, double now)
    {
        var stream = root.GetRegisteredComponent<HandPoseStreamManager>();
        if (!ShapeFingers.Value)
        {
            if (_handStream != null && ReferenceEquals(_handStream.LiveSource, _hands))
                _handStream.LiveSource = _handsBefore;
            _handStream = null;
            return;
        }
        if (stream == null)
            return;
        if (!ReferenceEquals(stream, _handStream))
        {
            _handStream = stream;
            _handsBefore = ReferenceEquals(stream.LiveSource, _hands) ? null : stream.LiveSource;
            stream.LiveSource = _hands;
        }
        ShapeHand(Chirality.Left, "LeftHand", now);
        ShapeHand(Chirality.Right, "RightHand", now);
    }

    private void ShapeHand(Chirality side, string name, double now)
    {
        var curl = RestCurl.Value;
        float splay = RestSplay.Value;
        foreach (var layer in _layers)
        {
            float w = Envelope(layer, now);
            if (w <= 1e-4f)
                continue;
            float t = Local(layer, now);
            if (layer.Clip.FindTrack(name, "Fingers") is CurveAnimationTrack<float4> fingers && fingers.Evaluate(t, out var c))
                curl = layer.Additive ? curl + c * w : float4.Lerp(curl, c, w);
            if (layer.Clip.FindTrack(name, "Splay") is CurveAnimationTrack<float> thumb && thumb.Evaluate(t, out var s))
                splay = layer.Additive ? splay + s * w : splay + (s - splay) * w;
        }
        // The pinky curls a little past the ring, as a hand at rest does.
        HandPosePresets.Shape(_hands, side, curl.x, curl.y, curl.z, curl.w, curl.w * 1.08f + 0.03f, splay);
    }

    public override void OnDestroy()
    {
        if (_handStream != null && ReferenceEquals(_handStream.LiveSource, _hands))
            _handStream.LiveSource = _handsBefore;
        base.OnDestroy();
    }
}
