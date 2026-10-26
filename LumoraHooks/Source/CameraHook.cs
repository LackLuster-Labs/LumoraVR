// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Godot.Helpers;

namespace Lumora.Godot.Hooks;

// Camera -> the SubViewport behind its target RenderTexture.
//
// There is deliberately NO Camera3D in the main scene tree here. The old hook made one and set
// Camera.Current on it, which meant attaching a Camera component anywhere hijacked the local player's
// whole view; the empty UpdateClearMode/UpdateViewport stubs beside it were the giveaway that nobody
// had ever run the thing. A camera now only exists as parameters pushed at a render texture, and with
// no texture it renders nothing at all.
//
// Selective and excluded rendering are layer work, since Godot has no per-camera object list. The
// camera borrows one free render layer for as long as either list has anything in it. -xlinka
[ImplementableHook(typeof(Camera))]
public class CameraHook : ComponentHook<Camera>
{
    // A subtree that gained children after the lists were applied would never get the layer, and the
    // lists change far too rarely to justify walking them every frame. Same shape as the particle scan:
    // bounded by TIME, not by a call count, because ApplyChanges is queue-driven and a call-count
    // throttle on a static camera never elapses. -xlinka
    private const double LayerRescanInterval = 0.5;

    // The camera is placed from the slot's own transform; nothing hangs under the node.
    protected override bool ReadsNodePose => false;

    private RenderTexture? _boundTexture;
    private int _selectiveLayer;
    private readonly List<Node3D> _selectiveRoots = new();
    private readonly List<Node3D> _excludedRoots = new();
    private double _nextLayerScan;
    private string _lastListSignature = string.Empty;

    public override void ApplyChanges()
    {
        var target = Owner.TargetTexture.Target?.Asset;

        if (target != _boundTexture)
        {
            ReleaseTexture();
            _boundTexture = target;
        }

        if (target == null)
        {
            ReleaseLayers();
            return;
        }

        UpdateSelectiveLayers();
        target.SetCameraOverride(BuildParameters());
        // The texture draws only while this camera is on and somebody can see what it shows. Its renderer can
        // come up a frame after the camera does; it is told then.
        if (target.Hook is RenderTextureHook textureHook)
            textureHook.SetFilmingCamera(Owner);
        else
            Owner.MarkChangeDirty();
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld)
        {
            ReleaseTexture();
            ReleaseLayers();
        }
        else
        {
            RenderHelper.ReleaseSelectiveLayer(_selectiveLayer);
            _selectiveLayer = 0;
        }

        base.Destroy(destroyingWorld);
    }

    // Parameters

    private RenderCameraParameters BuildParameters()
    {
        var slot = Owner.Slot;
        float lens = Owner.LensScale;
        return new RenderCameraParameters
        {
            Perspective = Owner.Projection.Value != ProjectionType.Orthographic,
            FieldOfView = Owner.FieldOfView.Value,
            OrthographicSize = Owner.OrthographicSize.Value * lens,
            NearClip = Owner.NearClip.Value * lens,
            FarClip = Owner.FarClip.Value * lens,
            Position = slot.GlobalPosition,
            Rotation = Owner.ViewRotation,
            CullMask = EffectiveCullMask(),
            Clear = Owner.Clear.Value,
            BackgroundColor = Owner.BackgroundColor.Value,
            RenderShadows = Owner.RenderShadows.Value,
            OcclusionCulling = Owner.UseOcclusionCulling.Value,
            Msaa = Owner.AllowMSAA.Value,
            PostProcessing = Owner.RenderPostProcessing.Value,
            WearerView = Owner.WearerView.Value,
            StudioLight = Owner.StudioLight.Value,
        };
    }

    // The lent bit this camera films its listed objects alone on, which no other view draws; 0 when it films
    // everything or leaves objects out.
    internal int FilmedAloneBit => _selectiveLayer != 0 && _selectiveRoots.Count > 0 ? _selectiveLayer : 0;

    private int EffectiveCullMask()
    {
        // The authored mask never gets to show the bands that are private by construction - a camera
        // filming into a texture other people can see must not pick up somebody's private UI, and the
        // eyes-only band (the wearer's headless twin) is masked out of PUBLIC_RENDER_MASK for the same
        // reason: a mirror has to show the head.
        int mask = Owner.CullingMask.Value & (Owner.RenderPrivateUI.Value ? RenderHelper.PRIVATE_RENDER_MASK : RenderHelper.PUBLIC_RENDER_MASK);

        if (_selectiveLayer == 0)
            return mask;

        if (_selectiveRoots.Count > 0)
            return _selectiveLayer;

        return mask & ~_selectiveLayer;
    }

    // Selective / excluded layers

    private void UpdateSelectiveLayers()
    {
        string signature = BuildListSignature();
        double now = Owner.World?.Time?.TotalTime ?? 0.0;
        bool listsChanged = signature != _lastListSignature;

        if (!listsChanged && now < _nextLayerScan)
            return;

        _lastListSignature = signature;
        _nextLayerScan = now + LayerRescanInterval;

        bool wantsSelective = HasAny(Owner.SelectiveRender);
        bool wantsExclude = !wantsSelective && HasAny(Owner.ExcludeRender);

        if (!wantsSelective && !wantsExclude)
        {
            ReleaseLayers();
            return;
        }

        if (_selectiveLayer == 0)
        {
            _selectiveLayer = RenderHelper.AllocateSelectiveLayer();
            if (_selectiveLayer == 0)
            {
                Lumora.Core.Logging.Logger.Warn(
                    $"Camera '{Owner.Slot.SlotName.Value}': no free render layer left for selective rendering, " +
                    "rendering everything instead.");
                return;
            }
        }

        if (listsChanged)
            RestoreRoots();

        RenderHelper.SetFilmedAlone(_selectiveLayer, wantsSelective);

        if (wantsSelective)
        {
            CollectRoots(Owner.SelectiveRender, _selectiveRoots);
            foreach (var root in _selectiveRoots)
                RenderHelper.AddHierarchyLayerBit(root, _selectiveLayer);
        }
        else
        {
            CollectRoots(Owner.ExcludeRender, _excludedRoots);
            foreach (var root in _excludedRoots)
                RenderHelper.SwapPublicLayerForBit(root, _selectiveLayer);
        }
    }

    private void ReleaseLayers()
    {
        if (_selectiveLayer == 0)
            return;

        RestoreRoots();
        RenderHelper.ReleaseSelectiveLayer(_selectiveLayer);
        _selectiveLayer = 0;
        _lastListSignature = string.Empty;
    }

    private void RestoreRoots()
    {
        foreach (var root in _selectiveRoots)
            RenderHelper.RemoveHierarchyLayerBit(root, _selectiveLayer);
        _selectiveRoots.Clear();

        foreach (var root in _excludedRoots)
            RenderHelper.RestorePublicLayerFromBit(root, _selectiveLayer);
        _excludedRoots.Clear();
    }

    private void ReleaseTexture()
    {
        (_boundTexture?.Hook as RenderTextureHook)?.SetFilmingCamera(null);
        _boundTexture?.SetCameraOverride(null);
        _boundTexture = null;
    }

    private static bool HasAny(SyncRefList<Slot> list)
    {
        foreach (var slot in list)
        {
            if (slot != null)
                return true;
        }
        return false;
    }

    private static void CollectRoots(SyncRefList<Slot> list, List<Node3D> into)
    {
        into.Clear();
        foreach (var slot in list)
        {
            if (slot?.Hook is not SlotHook hook)
                continue;
            var node = hook.GeneratedNode3D;
            if (node != null && GodotObject.IsInstanceValid(node))
                into.Add(node);
        }
    }

    // Cheap membership fingerprint: what matters is which slots are listed, not their transforms.
    private string BuildListSignature()
    {
        var builder = new System.Text.StringBuilder();
        AppendList(builder, Owner.SelectiveRender);
        builder.Append('|');
        AppendList(builder, Owner.ExcludeRender);
        return builder.ToString();
    }

    private static void AppendList(System.Text.StringBuilder builder, SyncRefList<Slot> list)
    {
        foreach (var slot in list)
        {
            builder.Append(slot?.ReferenceID.ToString() ?? "0");
            builder.Append(',');
        }
    }
}
