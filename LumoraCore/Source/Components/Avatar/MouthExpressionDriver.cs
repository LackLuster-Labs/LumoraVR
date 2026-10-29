// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Avatar;

// Runs on every peer because the mouth stream is synced; with nothing tracking the mouth rests at zero.
//
// An avatar that names its face-tracking shapes is driven by exactly those: each named shape on ShapeMesh follows
// one expression, from ShapeRanges.x at rest to .y at full, so a sideways shape can run negative one way and
// positive the other. Those are claimed over the guesses by name; anything not named falls back to matching
// names, unless ExactShapes says the avatar named all it wants. -xlinka
[ComponentCategory("Users/Avatar/Face")]
public sealed class MouthExpressionDriver : Component
{
    public readonly SyncRef<SkinnedMeshRenderer> ShapeMesh = new();
    public readonly SyncFieldList<string> ShapeNames = new();
    public readonly SyncFieldList<FaceExpression> ShapeExpressions = new();
    public readonly SyncFieldList<float2> ShapeRanges = new();
    public readonly Sync<bool> ExactShapes = new();
    public readonly Sync<float> StrengthMultiplier;

    // Name tokens per mouth shape, indexed by the MouthShape enum order; whole-segment matches (FaceShapes.HasToken).
    private static readonly string[][] Keywords =
    {
        new[] { "jawopen", "jaw_open", "mouthopen", "mouth_open" }, // JawOpen
        new[] { "jawleft", "jaw_left" },                            // JawLeft
        new[] { "jawright", "jaw_right" },                          // JawRight
        new[] { "jawforward", "jaw_forward" },                      // JawForward
        new[] { "pucker", "mouthpucker", "pout" },                  // MouthPucker
        // A bare "stretch" took any stretched part of a body (a belly or a tail slider) and pinned it at rest.
        new[] { "mouthwide", "mouthstretch", "mouth_stretch", "lipstretch", "lip_stretch" }, // MouthWide
        // The vendor lip set writes Mouth_Upper_UpLeft and Mouth_Lower_DownLeft, and the unified set's smile
        // is MouthCornerPull; without these the tracker's lips and smile reached nothing on those avatars.
        new[] { "smileleft", "mouthsmileleft", "smile_l", "cornerpullleft" },   // SmileLeft
        new[] { "smileright", "mouthsmileright", "smile_r", "cornerpullright" }, // SmileRight
        new[] { "frownleft", "mouthfrownleft", "sad_l", "sadleft" },             // FrownLeft
        new[] { "frownright", "mouthfrownright", "sad_r", "sadright" },          // FrownRight
        new[] { "upperup", "mouthupperup", "upperlipup", "upper_up" },           // UpperUp
        new[] { "lowerdown", "mouthlowerdown", "lowerlipdown", "lower_down" },   // LowerDown
        new[] { "cheekpuff", "cheek_puff" },                        // CheekPuff
        // Tongue intentionally NOT claimed by name: claiming a tongue shape and pinning it to weight 0 mis-renders
        // any avatar whose neutral tongue isn't authored at morph-0 (it pops the tongue out). An avatar that names
        // its tongue shapes gets them through the named list. - xlinka
    };

    private readonly List<(SkinnedMeshRenderer Renderer, int Index, MouthShape Shape)> _targets = new();
    private readonly List<(SkinnedMeshRenderer Renderer, int Index, FaceExpression Expression, float2 Range)> _named = new();
    private bool _resolved;
    private float _retry;
    private MouthStreamManager? _mouth;
    private bool _tracking;
    private readonly AvatarFaceWearer _wearer = new();

    public MouthExpressionDriver()
    {
        StrengthMultiplier = new Sync<float>(this, 1f);
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

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
        if (_targets.Count == 0 && _named.Count == 0)
            return;

        if (_mouth != null && _mouth.IsDestroyed)
            _mouth = null;
        _mouth ??= wearer.GetRegisteredComponent<MouthStreamManager>();
        _tracking = _mouth != null && _mouth.IsTracking;
        float strength = StrengthMultiplier.Value;

        for (int i = 0; i < _named.Count; i++)
        {
            var (renderer, index, expression, range) = _named[i];
            if (renderer == null || renderer.IsDestroyed || !renderer.OwnsBlendShape(index, this))
            {
                _resolved = false;
                _retry = 0f;
                return;
            }
            // Nothing tracking is nothing to show: every named shape rests at 0, even one whose range puts the
            // middle of a sideways expression somewhere else.
            float value = System.Math.Clamp(Evaluate(expression), 0f, 1f);
            renderer.DriveBlendShapeWeight(index, _tracking ? (range.x + (range.y - range.x) * value) * strength : 0f);
        }

        for (int i = 0; i < _targets.Count; i++)
        {
            var (renderer, index, shape) = _targets[i];
            if (renderer == null || renderer.IsDestroyed || !renderer.OwnsBlendShape(index, this))
            {
                _resolved = false; // mesh went away or a higher-priority driver took this shape; rescan
                _retry = 0f;
                return;
            }

            renderer.DriveBlendShapeWeight(index, Channel(shape) * strength);
        }
    }

    // One tracked mouth channel, 0..1; 0 while nothing tracks.
    private float Channel(MouthShape shape) => _tracking ? System.Math.Clamp(_mouth!.GetWeight(shape), 0f, 1f) : 0f;

    private float Average(MouthShape a, MouthShape b) => (Channel(a) + Channel(b)) * 0.5f;

    // Across two opposite channels: 0.5 at rest, 0 all the way one way, 1 all the way the other.
    private float Across(MouthShape toward0, MouthShape toward1) => (Channel(toward1) - Channel(toward0)) * 0.5f + 0.5f;

    // The tongue's reach: half out at the first step, all the way at the second.
    private float TongueReach => System.Math.Clamp((Channel(MouthShape.TongueOut) + Channel(MouthShape.TongueExtend)) * 0.5f, 0f, 1f);

    private float Evaluate(FaceExpression expression) => expression switch
    {
        FaceExpression.SmileLeft => Channel(MouthShape.SmileLeft),
        FaceExpression.SmileRight => Channel(MouthShape.SmileRight),
        FaceExpression.Smile => Average(MouthShape.SmileLeft, MouthShape.SmileRight),
        FaceExpression.SmileClosedLeft => (1f - Channel(MouthShape.UpperUpLeft)) * Channel(MouthShape.SmileLeft),
        FaceExpression.SmileClosedRight => (1f - Channel(MouthShape.UpperUpRight)) * Channel(MouthShape.SmileRight),
        FaceExpression.SmileClosed => ((1f - Channel(MouthShape.UpperUpLeft)) * Channel(MouthShape.SmileLeft)
            + (1f - Channel(MouthShape.UpperUpRight)) * Channel(MouthShape.SmileRight)) * 0.5f,
        FaceExpression.GrinLeft => Channel(MouthShape.UpperUpLeft) * Channel(MouthShape.SmileLeft),
        FaceExpression.GrinRight => Channel(MouthShape.UpperUpRight) * Channel(MouthShape.SmileRight),
        FaceExpression.Grin => (Channel(MouthShape.UpperUpLeft) * Channel(MouthShape.SmileLeft)
            + Channel(MouthShape.UpperUpRight) * Channel(MouthShape.SmileRight)) * 0.5f,
        FaceExpression.SmirkLeft => System.Math.Max(0f, Channel(MouthShape.SmileLeft) - Channel(MouthShape.SmileRight)),
        FaceExpression.SmirkRight => System.Math.Max(0f, Channel(MouthShape.SmileRight) - Channel(MouthShape.SmileLeft)),
        FaceExpression.FrownLeft => Channel(MouthShape.FrownLeft),
        FaceExpression.FrownRight => Channel(MouthShape.FrownRight),
        FaceExpression.Frown => Average(MouthShape.FrownLeft, MouthShape.FrownRight),
        FaceExpression.JawOpen => Channel(MouthShape.JawOpen),
        FaceExpression.JawLeft => Channel(MouthShape.JawLeft),
        FaceExpression.JawRight => Channel(MouthShape.JawRight),
        FaceExpression.JawSideways => Across(MouthShape.JawLeft, MouthShape.JawRight),
        FaceExpression.JawForward => Channel(MouthShape.JawForward),
        FaceExpression.Pout => Channel(MouthShape.MouthPucker),
        FaceExpression.CheekPuffLeft => Channel(MouthShape.CheekPuffLeft),
        FaceExpression.CheekPuffRight => Channel(MouthShape.CheekPuffRight),
        FaceExpression.CheekPuff => Average(MouthShape.CheekPuffLeft, MouthShape.CheekPuffRight),
        FaceExpression.CheekSuck => Channel(MouthShape.CheekSuck),
        FaceExpression.UpperLipRaiseLeft => Channel(MouthShape.UpperUpLeft),
        FaceExpression.UpperLipRaiseRight => Channel(MouthShape.UpperUpRight),
        FaceExpression.UpperLipRaise => Average(MouthShape.UpperUpLeft, MouthShape.UpperUpRight),
        FaceExpression.LowerLipDownLeft => Channel(MouthShape.LowerDownLeft),
        FaceExpression.LowerLipDownRight => Channel(MouthShape.LowerDownRight),
        FaceExpression.LowerLipDown => Average(MouthShape.LowerDownLeft, MouthShape.LowerDownRight),
        FaceExpression.UpperLipLeft => Channel(MouthShape.UpperLipLeft),
        FaceExpression.UpperLipRight => Channel(MouthShape.UpperLipRight),
        FaceExpression.UpperLipSideways => Across(MouthShape.UpperLipLeft, MouthShape.UpperLipRight),
        FaceExpression.LowerLipLeft => Channel(MouthShape.LowerLipLeft),
        FaceExpression.LowerLipRight => Channel(MouthShape.LowerLipRight),
        FaceExpression.LowerLipSideways => Across(MouthShape.LowerLipLeft, MouthShape.LowerLipRight),
        FaceExpression.UpperLipOverturn => Channel(MouthShape.UpperLipOverturn),
        FaceExpression.LowerLipOverturn => Channel(MouthShape.LowerLipOverturn),
        FaceExpression.UpperLipTuck => Channel(MouthShape.UpperLipInside),
        FaceExpression.LowerLipTuck => Channel(MouthShape.LowerLipInside),
        FaceExpression.LowerLipOverlay => Channel(MouthShape.LowerLipOverlay),
        FaceExpression.TongueOut => TongueReach,
        FaceExpression.TongueRaise => System.Math.Clamp(TongueReach * 2f, 0f, 1f),
        FaceExpression.TongueExtend => System.Math.Clamp(TongueReach * 2f - 1f, 0f, 1f),
        FaceExpression.TongueUp => Channel(MouthShape.TongueUp),
        FaceExpression.TongueDown => Channel(MouthShape.TongueDown),
        FaceExpression.TongueLeft => Channel(MouthShape.TongueLeft),
        FaceExpression.TongueRight => Channel(MouthShape.TongueRight),
        FaceExpression.TongueVertical => Across(MouthShape.TongueDown, MouthShape.TongueUp),
        FaceExpression.TongueSideways => Across(MouthShape.TongueLeft, MouthShape.TongueRight),
        FaceExpression.TongueRoll => Channel(MouthShape.TongueRoll),
        FaceExpression.TongueUpLeft => Channel(MouthShape.TongueUpLeft),
        FaceExpression.TongueUpRight => Channel(MouthShape.TongueUpRight),
        FaceExpression.TongueDownLeft => Channel(MouthShape.TongueDownLeft),
        FaceExpression.TongueDownRight => Channel(MouthShape.TongueDownRight),
        _ => 0f,
    };

    private void Release()
    {
        foreach (var (renderer, index, _) in _targets)
        {
            if (renderer != null && !renderer.IsDestroyed)
                renderer.ReleaseBlendShape(index, this);
        }
        foreach (var (renderer, index, _, _) in _named)
        {
            if (renderer != null && !renderer.IsDestroyed)
                renderer.ReleaseBlendShape(index, this);
        }
        _targets.Clear();
        _named.Clear();
        _resolved = false;
        _retry = 0f;
        _mouth = null;
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
        _named.Clear();

        // A named shape whose mesh has not loaded its shapes yet is looked for again in a moment.
        bool missing = false;
        var mesh = ShapeMesh.Target;
        if (mesh != null && !mesh.IsDestroyed)
        {
            for (int i = 0; i < ShapeNames.Count && i < ShapeExpressions.Count; i++)
            {
                var shape = ShapeNames[i];
                if (string.IsNullOrEmpty(shape))
                    continue;
                int index = mesh.GetBlendShapeIndex(shape);
                if (index < 0 && mesh.BlendShapeCount == 0)
                    missing = true;
                var range = i < ShapeRanges.Count ? ShapeRanges[i] : new float2(0f, 1f);
                if (index >= 0 && mesh.ClaimBlendShape(index, this, SkinnedMeshRenderer.BlendShapePriorityNamedExpression))
                    _named.Add((mesh, index, ShapeExpressions[i], range));
            }
        }
        _retry = 1f;

        if (!ExactShapes.Value)
            FindByName();
        _resolved = !missing;
        if (_targets.Count + _named.Count > 0)
            LumoraLogger.Debug($"MouthExpressionDriver: resolved {_named.Count} named and {_targets.Count} guessed mouth blendshape target(s)");
    }

    private void FindByName()
    {
        foreach (var renderer in Slot.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            for (int i = 0; i < renderer.BlendShapeCount; i++)
            {
                var name = renderer.BlendShapeName(i);
                if (string.IsNullOrEmpty(name) || renderer.OwnsBlendShape(i, this))
                    continue;
                var lower = name.ToLowerInvariant();

                for (int s = 0; s < Keywords.Length; s++)
                {
                    if (!FaceShapes.HasAnyToken(lower, Keywords[s]))
                        continue;
                    // Lowest priority: only take the shape if blink/viseme haven't already claimed it, so the
                    // catch-all expression mapping never fights a more specific driver.
                    if (renderer.ClaimBlendShape(i, this, SkinnedMeshRenderer.BlendShapePriorityExpression))
                        _targets.Add((renderer, i, (MouthShape)s));
                    break; // one shape per blendshape
                }
            }
        }
    }
}
