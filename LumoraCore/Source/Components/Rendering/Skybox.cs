// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// The world's sky, drawn from a sky material (a gradient sky, a 360 picture) or else from a cubemap asset.
//
// A world has exactly one sky, but nothing stops someone attaching several of these - a loaded item
// brings its own, or two people each add one. Rather than letting whichever hook ran last win (which
// would differ per machine and flicker on reload), the winner is resolved from synced state:
// Priority first, then the lower RefID. Every peer computes the same answer from the
// same data, and MakeActive is a one-liner that just outbids the current winner.
//
// A GradientSkybox in the same world always loses to a Skybox. The shader sky is a
// background and nothing else; this one also feeds ambient light and reflections, so when both exist
// the one carrying more information is the one worth showing. -xlinka
[ComponentCategory("Rendering")]
public class Skybox : ImplementableComponent, ICustomInspectorUI
{
    // A sky material wins over Cubemap; any other kind of material is ignored and the cubemap shows.
    public readonly AssetRef<MaterialAsset> Material;

    // nothing renders until it is set and loaded
    public readonly AssetRef<CubemapAsset> Cubemap;

    // degrees, for lining a panorama up with the world
    [Range(-180f, 180f, "0.0")]
    public readonly Sync<float> Rotation;

    // 1 draws the cubemap as authored
    [Range(0f, 8f, "0.00")]
    public readonly Sync<float> Exposure;

    // higher wins; equal priorities fall back to the lower RefID
    public readonly Sync<int> Priority;

    // off uses AmbientColor, which is the cheaper and more predictable option for an interior
    [Group("Ambient")]
    public readonly Sync<bool> AmbientFromSky;

    // used when AmbientFromSky is off
    public readonly Sync<color> AmbientColor;

    [Range(0f, 4f, "0.00")]
    public readonly Sync<float> AmbientEnergy;

    // off leaves glossy materials reflecting only real lights, which is what you want when the sky has
    // a bright sun disc painted into it and you don't want that disc mirrored across every polished floor
    [Group("Reflections")]
    public readonly Sync<bool> ReflectionsFromSky;

    // Per-world membership. A component cannot ask its world "who else is a Skybox" without walking
    // the whole slot tree, and the answer is needed on every enable, disable and priority change, so
    // the list is maintained on the way in and out instead.
    private static readonly Dictionary<World, List<Skybox>> _registry = new();
    private static readonly object _registryLock = new();

    private bool _isActive;
    private bool _registered;

    public bool IsActiveSkybox => _isActive;

    // The sky material showing, if Material holds one.
    public ISkyboxMaterial? SkyMaterial => Material.Target as ISkyboxMaterial;

    public Skybox()
    {
        Material = new AssetRef<MaterialAsset>(this);
        Cubemap = new AssetRef<CubemapAsset>(this);
        Rotation = new Sync<float>(this, 0f);
        Exposure = new Sync<float>(this, 1f);
        Priority = new Sync<int>(this, 0);
        AmbientFromSky = new Sync<bool>(this, true);
        AmbientColor = new Sync<color>(this, new color(0.32f, 0.36f, 0.42f, 1f));
        AmbientEnergy = new Sync<float>(this, 1f);
        ReflectionsFromSky = new Sync<bool>(this, true);
    }

    public override void OnStart()
    {
        base.OnStart();
        Register();
    }

    public override void OnDestroy()
    {
        Unregister();
        base.OnDestroy();
    }

    public override void OnChanges()
    {
        base.OnChanges();
        // Enabled, Priority and the slot's active state all move the winner, and all three arrive
        // here. Resolving on every change pass is cheap: it is a walk of the skyboxes in one world,
        // which is one or two of them in every world anyone has ever built.
        Resolve(World);
    }

    // outbids whatever holds it now
    [SyncMethod]
    public void MakeActive()
    {
        int highest = int.MinValue;
        foreach (var other in Snapshot(World))
        {
            if (other != this && other.Priority.Value > highest)
                highest = other.Priority.Value;
        }

        if (highest != int.MinValue && Priority.Value <= highest)
            Priority.Value = highest + 1;

        Resolve(World);
    }

    private void Register()
    {
        var world = World;
        if (world == null || _registered)
            return;

        lock (_registryLock)
        {
            if (!_registry.TryGetValue(world, out var list))
                _registry[world] = list = new List<Skybox>();
            list.Add(this);
        }
        _registered = true;
        Resolve(world);
    }

    private void Unregister()
    {
        var world = World;
        if (!_registered)
            return;
        _registered = false;

        lock (_registryLock)
        {
            if (world != null && _registry.TryGetValue(world, out var list))
            {
                list.Remove(this);
                if (list.Count == 0)
                    _registry.Remove(world);
            }
        }

        if (_isActive)
        {
            _isActive = false;
            // The hook is already being torn down for this one; what matters is that whoever takes
            // over gets told to claim the environment.
            Resolve(world);
        }
    }

    // The sky this world shows, or null.
    public static Skybox? ActiveIn(World? world)
    {
        foreach (var candidate in Snapshot(world))
        {
            if (candidate._isActive && !candidate.IsDestroyed)
                return candidate;
        }
        return null;
    }

    // The light the showing sky gives off, as nine coefficients of linear RGB, with its tint and exposure on.
    // Null for a sky drawn from a shader, or one whose picture has not loaded.
    public SphericalHarmonicsL2? Radiance()
    {
        CubemapAsset? cube = null;
        float exposure = Exposure.Value;
        var tint = new float3(1f, 1f, 1f);
        switch (SkyMaterial)
        {
            case Projection360Material picture when picture.Cubemap.Target != null:
                cube = picture.Cubemap.Asset;
                exposure *= picture.Exposure.Value;
                var t = picture.Tint.Value;
                tint = new float3(ColorSpace.GammaToLinear(t.r), ColorSpace.GammaToLinear(t.g), ColorSpace.GammaToLinear(t.b));
                break;
            case null:
                cube = Cubemap.Asset;
                break;
        }
        if (cube?.Radiance is not { } radiance)
            return null;
        var scale = tint * exposure;
        for (int i = 0; i < SphericalHarmonicsL2.Count; i++)
            radiance[i] = radiance[i] * scale;
        return radiance;
    }

    private static Skybox[] Snapshot(World? world)
    {
        if (world == null)
            return System.Array.Empty<Skybox>();
        lock (_registryLock)
        {
            return _registry.TryGetValue(world, out var list) ? list.ToArray() : System.Array.Empty<Skybox>();
        }
    }

    // Pick the winner and tell only the components whose state actually flipped. Marking every skybox
    // dirty on every change pass would put this straight back into the change queue and spin.
    private static void Resolve(World? world)
    {
        var all = Snapshot(world);
        if (all.Length == 0)
            return;

        Skybox? winner = null;
        foreach (var candidate in all)
        {
            if (candidate.IsDestroyed || !candidate.Enabled || candidate.Slot == null || !candidate.Slot.IsActive)
                continue;
            if (winner == null || Outranks(candidate, winner))
                winner = candidate;
        }

        foreach (var candidate in all)
        {
            bool active = candidate == winner;
            if (candidate._isActive == active)
                continue;
            candidate._isActive = active;
            candidate.MarkChangeDirty();
        }
    }

    private static bool Outranks(Skybox candidate, Skybox current)
    {
        if (candidate.Priority.Value != current.Priority.Value)
            return candidate.Priority.Value > current.Priority.Value;
        return candidate.ReferenceID < current.ReferenceID;
    }

    public void BuildInspectorBody(UIBuilder ui)
    {
        InspectorStats.AddRow(ui, "Active sky", _isActive ? "yes" : "no, another Skybox outranks it");
        InspectorStats.AddRow(ui, "Material", Material.Target == null ? "none, the cubemap shows"
            : SkyMaterial != null ? Material.Target.GetType().Name : "not a sky material, the cubemap shows");

        var asset = Cubemap.Asset;
        InspectorStats.AddRow(ui, "Cubemap", Cubemap.Target == null
            ? "none assigned"
            : asset is { FaceSize: > 0 } ? $"{asset.FaceSize} px faces" : "not loaded");
        InspectorStats.AddRow(ui, "Ambient", AmbientFromSky.Value ? "from sky" : "flat color");
        InspectorStats.AddRow(ui, "Reflections", ReflectionsFromSky.Value ? "from sky" : "probes and lights only");
    }
}
