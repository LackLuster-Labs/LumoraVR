// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

// Pupils drift procedurally until eye tracking takes over; widen, squeeze and frown come from tracking
// only and rest at 0 without it.
//
// An avatar can name its eye shapes, on any of its meshes: each follows one channel of one eye through a remap of
// its own (input x..y lands output z..w, so a widen can start late and overdrive), and is claimed over the guesses
// by name. With ExactShapes those are all the eye shapes there are. -xlinka
[ComponentCategory("Users/Avatar/Face")]
public sealed class EyeExpressionDriver : Component
{
    // Radians of noise phase per second.
    public readonly Sync<float> PupilNoiseSpeed = new();
    // Dilation range of the procedural drift, 0..1.
    public readonly Sync<float> PupilNoiseMin = new();
    public readonly Sync<float> PupilNoiseMax = new();

    // Channel: 0 pupil, 1 widen, 2 squeeze, 3 frown. Side: -1 both eyes, 0 left, 1 right.
    public readonly SyncRefList<SkinnedMeshRenderer> ShapeMeshes = new();
    public readonly SyncFieldList<string> ShapeNames = new();
    public readonly SyncFieldList<int> ShapeChannels = new();
    public readonly SyncFieldList<int> ShapeSides = new();
    public readonly SyncFieldList<float4> ShapeRemaps = new();
    public readonly Sync<bool> ExactShapes = new();

    private enum Channel { Pupil, Widen, Squeeze, Frown }

    // The vendor eye shape set writes these as Eye_Left_Wide and Eye_Frown, and the unified set lowers the
    // brow with BrowLowerer; a bare "wide" would take a mouth's MouthWide at this driver's higher priority.
    // A pupil that shrinks is not a dilation, and a nose dilates too: those stay out of the pupil drift.
    private static readonly (Channel Channel, string[] Keywords)[] ChannelKeywords =
    {
        (Channel.Pupil, new[] { "pupil", "dilat" }),
        (Channel.Widen, new[] { "widen", "eyewide", "eye_left_wide", "eye_right_wide" }),
        (Channel.Squeeze, new[] { "squint", "squeeze" }),
        (Channel.Frown, new[] { "browdown", "brow_down", "browlower", "eyefrown", "eye_frown" }),
    };

    // side: -1 combined, 0 left, 1 right
    private readonly List<(SkinnedMeshRenderer Renderer, int Index, Channel Channel, int Side, float4? Remap)> _targets = new();
    private bool _resolved;
    private float _retry;
    private float _time;
    private EyeStreamManager? _eye;
    private readonly AvatarFaceWearer _wearer = new();

    public override void OnInit()
    {
        base.OnInit();
        PupilNoiseSpeed.Value = 0.25f;
        PupilNoiseMin.Value = 0.15f;
        PupilNoiseMax.Value = 0.45f;
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        _time += delta;

        if (_wearer.Update(Slot))
            Release();
        var wearer = _wearer.Current;
        if (wearer == null)
            return;

        if (!_resolved)
        {
            _retry -= delta;
            if (_retry <= 0f)
                ResolveTargets();
        }
        if (_targets.Count == 0)
            return;

        if (_eye != null && _eye.IsDestroyed)
            _eye = null;
        _eye ??= wearer.GetRegisteredComponent<EyeStreamManager>();
        bool tracking = _eye != null && _eye.IsTracking;
        float proceduralPupil = ProceduralPupil();

        for (int i = 0; i < _targets.Count; i++)
        {
            var (renderer, index, channel, side, remap) = _targets[i];
            if (renderer == null || renderer.IsDestroyed || !renderer.OwnsBlendShape(index, this))
            {
                _resolved = false; // mesh went away or our claim was lost; rescan next frame
                _retry = 0f;
                return;
            }

            float weight = channel switch
            {
                Channel.Pupil => tracking && _eye != null ? System.Math.Clamp(_eye.Pupil, 0f, 1f) : proceduralPupil,
                Channel.Widen => tracking ? EyeChannel(channel, side) : 0f,
                Channel.Squeeze => tracking ? EyeChannel(channel, side) : 0f,
                Channel.Frown => tracking ? EyeChannel(channel, side) : 0f,
                _ => 0f,
            };

            if (remap is { } r)
            {
                float span = r.y - r.x;
                float v = System.Math.Clamp(MathF.Abs(span) < 1e-6f ? (weight >= r.y ? 1f : 0f) : (weight - r.x) / span, 0f, 1f);
                renderer.DriveBlendShapeWeight(index, r.z + (r.w - r.z) * v);
            }
            else
                renderer.DriveBlendShapeWeight(index, System.Math.Clamp(weight, 0f, 1f));
        }
    }

    private float EyeChannel(Channel channel, int side)
    {
        if (_eye == null)
            return 0f;

        float left, right;
        switch (channel)
        {
            case Channel.Widen: left = _eye.GetWiden(Chirality.Left); right = _eye.GetWiden(Chirality.Right); break;
            case Channel.Squeeze: left = _eye.GetSqueeze(Chirality.Left); right = _eye.GetSqueeze(Chirality.Right); break;
            case Channel.Frown: left = _eye.GetFrown(Chirality.Left); right = _eye.GetFrown(Chirality.Right); break;
            default: return 0f;
        }

        return side == 0 ? left : side == 1 ? right : (left + right) * 0.5f;
    }

    // Slow multi-frequency noise in [PupilNoiseMin, PupilNoiseMax] so the pupils drift like a living eye
    // even without a tracker.
    private float ProceduralPupil()
    {
        float t = _time * PupilNoiseSpeed.Value;
        float n = MathF.Sin(t) * 0.5f + MathF.Sin(t * 2.3f + 1.7f) * 0.3f + MathF.Sin(t * 0.6f + 0.5f) * 0.2f;
        n = n * 0.5f + 0.5f; // -> 0..1
        return PupilNoiseMin.Value + (PupilNoiseMax.Value - PupilNoiseMin.Value) * n;
    }

    private void Release()
    {
        foreach (var (renderer, index, _, _, _) in _targets)
        {
            if (renderer != null && !renderer.IsDestroyed)
                renderer.ReleaseBlendShape(index, this);
        }
        _targets.Clear();
        _resolved = false;
        _retry = 0f;
        _eye = null;
    }

    public override void OnAwake()
    {
        base.OnAwake();
        if (Slot != null)
            Slot.ActiveChanged += OnSlotActiveChanged;
    }

    // Switched off, it stops updating and would never see anything else change, so it lets go now.
    private void OnSlotActiveChanged(Slot slot)
    {
        if (Slot != null && !Slot.IsActive)
            Release();
    }

    public override void OnDestroy()
    {
        if (Slot != null)
            Slot.ActiveChanged -= OnSlotActiveChanged;
        Release();
        base.OnDestroy();
    }

    private void ResolveTargets()
    {
        _targets.Clear();

        // A named shape whose mesh has not loaded its shapes yet is looked for again in a moment.
        bool missing = false;
        for (int n = 0; n < ShapeNames.Count && n < ShapeMeshes.Count && n < ShapeChannels.Count; n++)
        {
            var mesh = ShapeMeshes[n];
            if (mesh == null || mesh.IsDestroyed || string.IsNullOrEmpty(ShapeNames[n]) || (uint)ShapeChannels[n] > (uint)Channel.Frown)
                continue;
            int index = FaceShapes.Find(mesh, ShapeNames[n]);
            if (index < 0)
            {
                missing |= mesh.BlendShapeCount == 0;
                continue;
            }
            int side = n < ShapeSides.Count ? ShapeSides[n] : -1;
            var remap = n < ShapeRemaps.Count ? ShapeRemaps[n] : new float4(0f, 1f, 0f, 1f);
            if (mesh.ClaimBlendShape(index, this, SkinnedMeshRenderer.BlendShapePriorityEye))
                _targets.Add((mesh, index, (Channel)ShapeChannels[n], side, remap));
        }
        _retry = 1f;
        _resolved = !missing;
        if (ExactShapes.Value)
            return;

        foreach (var renderer in Slot.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            for (int i = 0; i < renderer.BlendShapeCount; i++)
            {
                var name = renderer.BlendShapeName(i);
                if (string.IsNullOrEmpty(name) || renderer.OwnsBlendShape(i, this))
                    continue;
                var lower = name.ToLowerInvariant();

                for (int c = 0; c < ChannelKeywords.Length; c++)
                {
                    if (!ContainsAny(lower, ChannelKeywords[c].Keywords))
                        continue;
                    if (ChannelKeywords[c].Channel == Channel.Pupil && ContainsAny(lower, NotDilation))
                        break;
                    if (renderer.ClaimBlendShape(i, this, SkinnedMeshRenderer.BlendShapePriorityEye))
                        _targets.Add((renderer, i, ChannelKeywords[c].Channel, Side(lower), null));
                    break; // one channel per blendshape
                }
            }
        }
    }

    private static readonly string[] NotDilation = { "small", "constrict", "shrink", "contract", "nasal", "nose", "nostril" };

    // Whole segments only (FaceShapes.HasToken), as the other face drivers match.
    private static bool ContainsAny(string lower, string[] keywords) => FaceShapes.HasAnyToken(lower, keywords);

    private static int Side(string lower)
    {
        if (lower.Contains("left"))
            return 0;
        if (lower.Contains("right"))
            return 1;
        if (EndsWithSideToken(lower, 'l'))
            return 0;
        if (EndsWithSideToken(lower, 'r'))
            return 1;
        return -1;
    }

    private static bool EndsWithSideToken(string s, char side)
    {
        if (s.Length < 2 || s[s.Length - 1] != side)
            return false;
        char sep = s[s.Length - 2];
        return sep == '_' || sep == '.' || sep == '-' || sep == ' ';
    }
}
