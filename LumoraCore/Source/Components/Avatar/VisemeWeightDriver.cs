// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;

namespace Lumora.Core.Components.Avatar;

[ComponentCategory("Users/Avatar")]
public sealed class VisemeWeightDriver : Component
{
    public readonly SyncRef<LipSyncAnalyzer> Source = new();
    public readonly Sync<float> StrengthMultiplier = new();

    // Named shapes, in viseme order (silence, PP, FF, TH, DD, kk, CH, SS, nn, RR, aa, E, ih, oh, ou), on
    // one mesh. An avatar that says which shape is which (an imported descriptor does) is driven by
    // exactly those; anything left empty falls back to matching names, unless ExactShapes says the avatar
    // named all it wants: a mouth worked some other way names none, and nothing on it moves. -xlinka
    public readonly SyncRef<SkinnedMeshRenderer> ShapeMesh = new();
    public readonly SyncFieldList<string> ShapeNames = new();
    public readonly Sync<bool> ExactShapes = new();

    // Name tokens per viseme (lowercased). A blendshape matches if any token stands in its name (HasToken).
    private static readonly Dictionary<Viseme, string[]> Keywords = new()
    {
        { Viseme.Silence, new[] { "sil", "viseme_sil" } },
        { Viseme.PP, new[] { "v_pp", "viseme_pp", "_pp", "vrc.v_pp" } },
        { Viseme.FF, new[] { "v_ff", "viseme_ff", "_ff", "_fv" } },
        { Viseme.TH, new[] { "v_th", "viseme_th", "_th" } },
        { Viseme.DD, new[] { "v_dd", "viseme_dd", "_dd", "_td" } },
        { Viseme.KK, new[] { "v_kk", "viseme_kk", "_kk" } },
        { Viseme.CH, new[] { "v_ch", "viseme_ch", "_ch", "_sh" } },
        { Viseme.SS, new[] { "v_ss", "viseme_ss", "_ss" } },
        { Viseme.NN, new[] { "v_nn", "viseme_nn", "_nn", "ん" } },
        { Viseme.RR, new[] { "v_rr", "viseme_rr", "_rr" } },
        { Viseme.AA, new[] { "v_aa", "viseme_aa", "_aa", "あ" } },
        { Viseme.E, new[] { "v_e", "viseme_e", "_ee", "え" } },
        { Viseme.IH, new[] { "v_ih", "viseme_ih", "_ih", "い" } },
        { Viseme.OH, new[] { "v_oh", "viseme_oh", "_oh", "_oo", "お" } },
        { Viseme.OU, new[] { "v_ou", "viseme_ou", "_ou", "う" } },
        { Viseme.Laughter, new[] { "laugh" } },
    };

    // A face authored with the bare viseme codes calls its shapes nothing more than "AA", "PP" or "sil", which
    // none of the tokens above find. Matched on the whole name only: two letters inside a longer name are not a
    // viseme. -xlinka
    private static readonly Dictionary<string, Viseme> Codes = new(StringComparer.Ordinal)
    {
        ["sil"] = Viseme.Silence, ["pp"] = Viseme.PP, ["ff"] = Viseme.FF, ["th"] = Viseme.TH, ["dd"] = Viseme.DD,
        ["kk"] = Viseme.KK, ["ch"] = Viseme.CH, ["ss"] = Viseme.SS, ["nn"] = Viseme.NN, ["rr"] = Viseme.RR,
        ["aa"] = Viseme.AA, ["e"] = Viseme.E, ["ee"] = Viseme.E, ["ih"] = Viseme.IH, ["oh"] = Viseme.OH, ["ou"] = Viseme.OU,
    };

    private readonly List<(SkinnedMeshRenderer Renderer, int Index, Viseme V)> _targets = new();
    private bool _resolved;
    private float _retry;
    private readonly AvatarFaceWearer _wearer = new();

    public override void OnInit()
    {
        base.OnInit();
        StrengthMultiplier.Value = 1f;
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        if (_wearer.Update(Slot))
            Release();
        if (_wearer.Current == null)
            return;

        // Held by an animation that works the mouth itself: the shapes are its until every holder lets go.
        if (FaceHold.MouthHeld(Slot))
        {
            Release();
            EnsureSource();
            return;
        }

        if (!_resolved)
        {
            _retry -= delta;
            if (_retry <= 0f)
                ResolveTargets();
        }
        if (_targets.Count == 0)
            return;

        var source = Source.Target;
        float strength = StrengthMultiplier.Value;

        for (int i = 0; i < _targets.Count; i++)
        {
            var (renderer, index, viseme) = _targets[i];
            if (renderer == null || renderer.IsDestroyed || !renderer.OwnsBlendShape(index, this))
            {
                _resolved = false; // mesh went away or our claim was lost; rescan next frame
                _retry = 0f;
                return;
            }
            // No source / silence -> 0 -> mouth closed.
            float w = source != null ? source[viseme] * strength : 0f;
            renderer.DriveBlendShapeWeight(index, System.Math.Clamp(w, 0f, 1f));
        }
    }

    private void Release()
    {
        foreach (var (renderer, index, _) in _targets)
        {
            if (renderer != null && !renderer.IsDestroyed)
                renderer.ReleaseBlendShape(index, this);
        }
        _targets.Clear();
        _resolved = false;
        _retry = 0f;
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

    // The voice this mouth follows, which the animator reads through us as well. A mouth on the face mesh listens
    // to the voice wherever the avatar keeps it, not only below itself.
    private void EnsureSource()
    {
        if (Source.Target != null && !Source.Target.IsDestroyed)
            return;
        var found = Slot.GetComponentInChildren<LipSyncAnalyzer>() ?? Slot.GetComponent<LipSyncAnalyzer>()
            ?? Slot.GetComponentInParents<AvatarForm>()?.Slot.GetComponentInChildren<LipSyncAnalyzer>();
        if (found != null)
            Source.Target = found;
    }

    private void ResolveTargets()
    {
        _targets.Clear();
        EnsureSource();

        // A named shape whose mesh has not loaded its shapes yet is looked for again in a moment. Settling on
        // nothing kept the mouth shut for good whenever it was worn before its mesh arrived. -xlinka
        bool missing = false;
        var named = new HashSet<Viseme>();
        var mesh = ShapeMesh.Target;
        if (mesh != null && !mesh.IsDestroyed)
        {
            for (int v = 0; v < ShapeNames.Count && v < (int)Viseme.COUNT; v++)
            {
                var shape = ShapeNames[v];
                if (string.IsNullOrEmpty(shape))
                    continue;
                int index = mesh.GetBlendShapeIndex(shape);
                if (index < 0 && mesh.BlendShapeCount == 0)
                    missing = true;
                if (index >= 0 && mesh.ClaimBlendShape(index, this, SkinnedMeshRenderer.BlendShapePriorityViseme))
                {
                    _targets.Add((mesh, index, (Viseme)v));
                    named.Add((Viseme)v);
                }
            }
        }
        _retry = 1f;

        if (ExactShapes.Value)
        {
            _resolved = !missing;
            return;
        }

        foreach (var renderer in Slot.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            for (int i = 0; i < renderer.BlendShapeCount; i++)
            {
                var name = renderer.BlendShapeName(i);
                if (string.IsNullOrEmpty(name))
                    continue;
                if (named.Count > 0 && ReferenceEquals(renderer, mesh) && renderer.OwnsBlendShape(i, this))
                    continue;
                var lower = name.ToLowerInvariant();

                // One viseme per blendshape: the whole-name code, else the first viseme a token finds.
                Viseme? found = Codes.TryGetValue(lower.Trim(), out var code) ? code : null;
                if (found == null)
                {
                    foreach (var kv in Keywords)
                    {
                        foreach (var token in kv.Value)
                        {
                            if (FaceShapes.HasToken(lower, token))
                            {
                                found = kv.Key;
                                break;
                            }
                        }
                        if (found != null)
                            break;
                    }
                }
                // Claim the shape so the catch-all expression driver leaves it alone (one owner per shape).
                if (found is { } viseme && !named.Contains(viseme)
                    && renderer.ClaimBlendShape(i, this, SkinnedMeshRenderer.BlendShapePriorityViseme))
                    _targets.Add((renderer, i, viseme));
            }
        }
        _resolved = !missing;
    }
}
