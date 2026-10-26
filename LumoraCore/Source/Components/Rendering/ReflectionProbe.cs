// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Lumora.Core.Math;
using Lumora.Core.Components.Meshes;

namespace Lumora.Core.Components;

public enum ProbeUpdateMode
{
    // then never again until something asks
    Once,

    // six scene renders per frame per probe - use it deliberately
    Always,

    // never looks at the scene: shows Cubemap as stored, from the capture point
    FromCubemap,
}

public enum ProbeAmbientMode
{
    Disabled,

    Environment,

    // for interiors the sky cannot see into
    Color,
}

// Default is ProbeUpdateMode.Once and that default is the whole cost story: a bake is
// six scene renders, so Always is six renders EVERY FRAME on top of the ordinary one. Anything that
// changes occasionally wants Once plus a Rebake when it changes; Always captures moving scenery.
// FromCubemap renders no scene at all: a capture is lit by whatever this machine is lighting at that
// moment, so a room far from the viewer comes out dark, while a stored cubemap is the same for everyone.
// Planar mirrors use CameraPortal instead of this cubemap approximation.
// -xlinka
[ComponentCategory("Rendering")]
public class ReflectionProbe : ImplementableComponent, ICustomInspectorUI, IInspectorActionHandler
{
    // local units, centred on the slot
    public readonly Sync<float3> Size;

    // moves the capture point away from the box centre without moving the box
    public readonly Sync<float3> OriginOffset;

    // see ProbeUpdateMode before reaching for Always
    public readonly Sync<ProbeUpdateMode> UpdateMode;

    [Range(0f, 4f, "0.00")]
    public readonly Sync<float> Intensity;

    // reprojects the captured cubemap onto the box, so a reflection lands where the wall actually is
    // instead of at infinity; right for rooms and corridors, wrong for open ground
    public readonly Sync<bool> BoxProjection;

    // treats the box as an enclosed space: the sky stops leaking in, and AmbientMode takes over
    // ambient light inside it
    public readonly Sync<bool> Interior;

    [Range(0f, 10f, "0.00")]
    public readonly Sync<float> BlendDistance;

    // smaller is cheaper
    public readonly Sync<float> MaxDistance;

    // 0 keeps the renderer default
    public readonly Sync<int> CullMask;

    // captures the sky and nothing in the world: a fallback for everything outside the room probes
    public readonly Sync<bool> SkyOnly;

    // roughly doubles what a bake costs
    public readonly Sync<bool> CaptureShadows;

    // only meaningful with Interior set
    [Group("Interior ambient")]
    public readonly Sync<ProbeAmbientMode> AmbientMode;

    public readonly Sync<color> AmbientColor;

    [Range(0f, 4f, "0.00")]
    public readonly Sync<float> AmbientEnergy;

    // bumped by Rebake; the hook watches this rather than a method call because a hook only ever sees
    // state, and a counter is the smallest piece of state that means "again"
    [HideInInspector]
    public readonly Sync<int> BakeGeneration;

    // A chrome ball at the capture point. A probe has nothing to look at, so whether it captured the
    // room, the sky, or a grey nothing is invisible until some glossy surface happens to sit inside
    // it; this puts that surface where the capture was taken. Local, so it is neither saved nor sent.
    public readonly Sync<bool> DebugVisual;

    // What a FromCubemap probe shows. With none set, or until it has loaded, the probe captures the
    // scene once instead, so it is never left empty.
    public readonly Lumora.Core.Assets.AssetRef<Lumora.Core.Assets.CubemapAsset> Cubemap;

    // What a Once probe bakes again after: a sky swapped or retinted, a lamp switched on. Without it the probe
    // goes on showing them as they were. Changes close together make one bake a second.
    public readonly SyncRefList<IWorldElement> ChangesSources;

    private const float ChangeBakeSpacing = 1f;
    private readonly System.Collections.Generic.List<IChangeable> _watched = new();
    private System.Action<IChangeable>? _sourceChanged;
    private bool _rebakeAsked;
    private bool _rebakeQueued;
    private double _rebakedAt = double.NegativeInfinity;

    public ReflectionProbe()
    {
        Size = new Sync<float3>(this, new float3(10f, 10f, 10f));
        OriginOffset = new Sync<float3>(this, float3.Zero);
        UpdateMode = new Sync<ProbeUpdateMode>(this, ProbeUpdateMode.Once);
        Intensity = new Sync<float>(this, 1f);
        BoxProjection = new Sync<bool>(this, false);
        Interior = new Sync<bool>(this, false);
        BlendDistance = new Sync<float>(this, 1f);
        MaxDistance = new Sync<float>(this, 0f);
        CullMask = new Sync<int>(this, 0);
        SkyOnly = new Sync<bool>(this, false);
        CaptureShadows = new Sync<bool>(this, false);
        AmbientMode = new Sync<ProbeAmbientMode>(this, ProbeAmbientMode.Environment);
        AmbientColor = new Sync<color>(this, new color(0f, 0f, 0f, 1f));
        AmbientEnergy = new Sync<float>(this, 1f);
        BakeGeneration = new Sync<int>(this, 0);
        DebugVisual = new Sync<bool>(this, false);
        Cubemap = new Lumora.Core.Assets.AssetRef<Lumora.Core.Assets.CubemapAsset>(this);
        ChangesSources = new SyncRefList<IWorldElement>(this);
    }

    private const string DebugSphereName = "ProbeDebugSphere";

    public override void OnChanges()
    {
        base.OnChanges();
        SyncDebugVisual();
        WatchChangesSources();
        RebakeIfAsked();
    }

    private void WatchChangesSources()
    {
        _sourceChanged ??= _ =>
        {
            _rebakeAsked = true;
            MarkChangeDirty();
        };
        for (int i = _watched.Count - 1; i >= 0; i--)
        {
            var watched = _watched[i];
            if (watched.IsDestroyed || !Lists(watched))
            {
                watched.Changed -= _sourceChanged;
                _watched.RemoveAt(i);
            }
        }
        foreach (var entry in ChangesSources)
        {
            if (entry is IChangeable source && !source.IsDestroyed && !ReferenceEquals(source, this) && !_watched.Contains(source))
            {
                source.Changed += _sourceChanged;
                _watched.Add(source);
            }
        }
    }

    private bool Lists(IChangeable source)
    {
        foreach (var entry in ChangesSources)
        {
            if (ReferenceEquals(entry, source))
                return true;
        }
        return false;
    }

    // Only the authority counts bakes, so a change seen on every peer bakes once.
    private void RebakeIfAsked()
    {
        if (!_rebakeAsked || World is not { IsAuthority: true } world || UpdateMode.Value != ProbeUpdateMode.Once)
        {
            _rebakeAsked = false;
            return;
        }
        double wait = _rebakedAt + ChangeBakeSpacing - world.TotalTime;
        if (wait > 0)
        {
            if (!_rebakeQueued)
            {
                _rebakeQueued = true;
                RunInSeconds((float)wait, () =>
                {
                    _rebakeQueued = false;
                    if (!IsDestroyed)
                        MarkChangeDirty();
                });
            }
            return;
        }
        _rebakeAsked = false;
        _rebakedAt = world.TotalTime;
        Rebake();
    }

    private void SyncDebugVisual()
    {
        if (Slot == null || Slot.IsDestroyed)
            return;
        var existing = Slot.FindChild(DebugSphereName, recursive: false);
        if (!DebugVisual.Value)
        {
            existing?.Destroy();
            return;
        }
        if (existing == null || existing.IsDestroyed)
        {
            existing = Slot.AddLocalSlot(DebugSphereName);
            var mesh = existing.AttachComponent<SphereMesh>();
            mesh.Radius.Value = 0.15f;
            mesh.Segments.Value = 64;
            mesh.Rings.Value = 32;
            // Fully metallic and fully smooth: nothing of its own to show, only what the probe fed it.
            var material = existing.AttachComponent<Lumora.Core.Assets.PBS_Metallic>();
            material.AlbedoColor.Value = colorHDR.White;
            material.Metallic.Value = 1f;
            material.Smoothness.Value = 1f;
            var renderer = existing.AttachComponent<MeshRenderer>();
            renderer.Mesh.Target = mesh;
            renderer.Material.Target = material;
            renderer.ShadowCastMode.Value = ShadowCastMode.Off;
            // The preview receives probe lighting but must never appear in any capture.
            existing.AttachComponent<RenderLayerOverride>().Layer.Value = 1 << 2;
        }
        existing.LocalPosition.Value = OriginOffset.Value;
    }

    public void HandleInspectorAction(string argument)
    {
        switch (argument)
        {
            case "rebake":
                Rebake();
                break;
            case "debug":
                DebugVisual.Value = !DebugVisual.Value;
                break;
            case "live":
                UpdateMode.Value = UpdateMode.Value == ProbeUpdateMode.Always
                    ? ProbeUpdateMode.Once : ProbeUpdateMode.Always;
                break;
        }
    }

    public override void OnDestroy()
    {
        foreach (var watched in _watched)
            watched.Changed -= _sourceChanged;
        _watched.Clear();
        Slot?.FindChild(DebugSphereName, recursive: false)?.Destroy();
        base.OnDestroy();
    }

    // a Once probe holds the room as it looked when it first came up, so anything that moves the
    // furniture has to say so
    [SyncMethod]
    [ScriptMethod]
    public void Rebake()
    {
        BakeGeneration.Value++;
    }

    // sizing a probe by hand is the fiddly part of using one, and the bounds walk that answers it
    // already exists for the inspector's gizmos
    public static ReflectionProbe? CreateForBounds(Slot slot, float padding = 0.5f)
    {
        if (slot == null || slot.IsDestroyed)
            return null;
        if (!SlotBoundsHelper.TryComputeWorldBounds(slot, out var bounds))
            return null;

        var probeSlot = slot.AddSlot("ReflectionProbe");
        probeSlot.GlobalPosition = bounds.Center;
        probeSlot.GlobalRotation = floatQ.Identity;

        var probe = probeSlot.AttachComponent<ReflectionProbe>();

        // The box is in the probe slot's local space, so a scaled parent would otherwise stretch it.
        var scale = probeSlot.GlobalScale;
        var size = bounds.Size + new float3(padding * 2f, padding * 2f, padding * 2f);
        probe.Size.Value = new float3(
            size.x / SafeScale(scale.x),
            size.y / SafeScale(scale.y),
            size.z / SafeScale(scale.z));
        return probe;
    }

    private static float SafeScale(float value)
    {
        float magnitude = value < 0f ? -value : value;
        return magnitude < 1e-4f ? 1f : magnitude;
    }

    public void BuildInspectorBody(UIBuilder ui)
    {
        InspectorStats.AddRow(ui, "Probes", EngineSettings.ReflectionsEnabled
            ? "on"
            : "off in settings, this probe is not rendering");
        InspectorStats.AddRow(ui, "Face size", "fixed by the renderer's reflection atlas");
        InspectorStats.AddRow(ui, "Cost", UpdateMode.Value switch
        {
            ProbeUpdateMode.Always => "six scene renders per frame",
            ProbeUpdateMode.FromCubemap => $"one small box drawn per bake, {BakeGeneration.Value} requested",
            _ => $"six scene renders per bake, {BakeGeneration.Value} requested",
        });
        if (UpdateMode.Value == ProbeUpdateMode.FromCubemap)
            InspectorStats.AddRow(ui, "Cubemap", Cubemap.Target == null
                ? "none set, the scene is captured once"
                : Cubemap.Asset is { FaceSize: > 0 } asset
                    ? $"{asset.FaceSize} px faces{(asset.IsHdr ? ", HDR" : "")}"
                    : "loading, the scene is captured once until then");
        InspectorUI.RelayButton(ui, this, "rebake", "Rebake now", 0f);
        InspectorUI.RelayButton(ui, this, "live", UpdateMode.Value == ProbeUpdateMode.Always
            ? "Live scene capture: on" : "Live scene capture: off", 0f);
        InspectorUI.RelayButton(ui, this, "debug", DebugVisual.Value ? "Hide chrome ball" : "Show chrome ball", 0f);
    }
}
